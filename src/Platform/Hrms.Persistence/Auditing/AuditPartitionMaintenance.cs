using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Hrms.Persistence.Auditing;

public sealed class AuditPartitionOptions
{
    public const string SectionName = "Audit:Partitions";

    /// <summary>
    /// Whole months after the current one that must have a partition. A year by default, so the job can fail for a long time before
    /// rows reach the catch-all.
    /// </summary>
    [Range(1, 36)]
    public int MonthsAhead { get; set; } = 12;

    /// <summary>
    /// How long the ALTER may run before the client cancels it. Audited saves can be blocked while it runs, so this is also the
    /// longest the job is prepared to hold them up. It only ever runs while the catch-all is empty, so it should have no rows to copy.
    /// </summary>
    [Range(10, 3600)]
    public int AlterTimeoutSeconds { get; set; } = 120;
}

/// <summary>
/// What one run did. <see cref="CatchAllHasRows"/> means nothing was changed and someone has to act:
/// <see cref="ManualStatement"/> adds the missing months, and is null when none are missing yet.
/// </summary>
public sealed record AuditPartitionRun(IReadOnlyList<AuditPartition> Added, bool CatchAllHasRows, string? ManualStatement);

/// <summary>
/// The connection that may alter audit_log. In production the application's own user should only be able to read and insert audit
/// rows, so adding partitions needs a more privileged user (ConnectionStrings:MysqlMaintenance).
/// </summary>
public sealed record AuditMaintenanceConnection(string? ConnectionString);

/// <summary>
/// Adds monthly partitions to audit_log ahead of time by splitting the catch-all. It only ever adds: nothing is dropped or deleted.
/// It reads the table's definition and changes its layout, and returns no tenant's rows. MySQL cannot bind identifiers or partition
/// bounds as parameters, so the statement is built from dates this code computed, never from input.
/// <para>
/// Splitting the catch-all copies whatever it holds, and audited saves can be blocked until that finishes. So the job only does it
/// while the catch-all is empty. Once rows have reached it, how long the copy takes depends on how many there are, and a person
/// has to choose when to run it: the job changes nothing and logs an error with the statement.
/// </para>
/// </summary>
public sealed partial class AuditPartitionMaintenance(
    AuditMaintenanceConnection maintenance,
    IOptions<AuditPartitionOptions> options,
    IJobLock jobLock,
    TimeProvider time,
    ILogger<AuditPartitionMaintenance> logger)
{
    public const string LockName = "audit-partitions";

    /// <summary>
    /// Before it can start, an ALTER waits for open transactions that touched the table, and new writes queue behind it while it
    /// waits, so it gives up quickly and tries again later. This limits that wait only, not how long the ALTER blocks once it runs.
    /// </summary>
    private const int LockWaitSeconds = 5;

    /// <returns>What the run did, or null when another instance holds the lock.</returns>
    public async Task<AuditPartitionRun?> RunAsync(CancellationToken cancellationToken)
    {
        var lease = await jobLock.TryAcquireAsync(LockName, cancellationToken);
        if (lease is null)
        {
            return null;
        }

        await using (lease)
        {
            return await AddMissingAsync(cancellationToken);
        }
    }

    private async Task<AuditPartitionRun> AddMissingAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(maintenance.ConnectionString))
        {
            throw new InvalidOperationException("Neither 'ConnectionStrings:MysqlMaintenance' nor 'ConnectionStrings:Mysql' is configured.");
        }

        await using var connection = new MySqlConnection(MySqlConnectionStrings.ForHrms(maintenance.ConnectionString));
        await connection.OpenAsync(cancellationToken);

        var coveredUpTo = await ReadCoveredUpToAsync(connection, cancellationToken);
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var missing = AuditPartitionPlan.Missing(coveredUpTo, today, options.Value.MonthsAhead);

        // Checked even when no month is missing: rows dated past the last month are a fault someone needs to hear about.
        if (await CatchAllHasRowsAsync(connection, cancellationToken))
        {
            if (missing.Count == 0)
            {
                LogCatchAllHasUnexpectedRows(logger);
                return new AuditPartitionRun([], CatchAllHasRows: true, ManualStatement: null);
            }

            var statement = SplitStatement(missing);
            LogManualSplitNeeded(logger, missing.Count, statement);
            return new AuditPartitionRun([], CatchAllHasRows: true, statement);
        }

        if (missing.Count > 0)
        {
            await ExecuteAsync(
                connection,
                string.Create(CultureInfo.InvariantCulture, $"SET SESSION lock_wait_timeout = {LockWaitSeconds}"),
                options.Value.AlterTimeoutSeconds,
                cancellationToken);
            await ExecuteAsync(connection, SplitStatement(missing), options.Value.AlterTimeoutSeconds, cancellationToken);
            LogAdded(logger, missing.Count, missing[0].Name, missing[^1].Name);
        }

        return new AuditPartitionRun(missing, CatchAllHasRows: false, ManualStatement: null);
    }

    private static string SplitStatement(IReadOnlyList<AuditPartition> missing)
    {
        var sql = new StringBuilder($"ALTER TABLE `audit_log` REORGANIZE PARTITION {AuditPartitionPlan.CatchAll} INTO (");
        foreach (var partition in missing)
        {
            sql.Append(CultureInfo.InvariantCulture, $"PARTITION {partition.Name} VALUES LESS THAN ('{partition.LessThan:yyyy-MM-dd}'), ");
        }

        return sql.Append($"PARTITION {AuditPartitionPlan.CatchAll} VALUES LESS THAN (MAXVALUE))").ToString();
    }

    /// <summary>The upper bound of the last monthly partition. Refuses a table that is not laid out as the migration made it.</summary>
    private static async Task<DateOnly> ReadCoveredUpToAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT PARTITION_NAME, PARTITION_DESCRIPTION FROM information_schema.PARTITIONS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'audit_log' AND PARTITION_NAME IS NOT NULL
            ORDER BY PARTITION_ORDINAL_POSITION DESC LIMIT 2
            """;

        var last = new List<(string Name, string Bound)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                last.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        if (last.Count < 2 || last[0].Name != AuditPartitionPlan.CatchAll
            || last[1].Bound.Trim('\'') is not { Length: >= 10 } bound
            || !DateOnly.TryParseExact(bound[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var covered))
        {
            throw new InvalidOperationException(
                $"audit_log must be partitioned by month and end with {AuditPartitionPlan.CatchAll}. Apply the Hrms.Audit migrations.");
        }

        return covered;
    }

    private static async Task<bool> CatchAllHasRowsAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM `audit_log` PARTITION ({AuditPartitionPlan.CatchAll}))";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task ExecuteAsync(MySqlConnection connection, string sql, int timeoutSeconds, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeoutSeconds;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Added {Count} audit log partitions, {First} to {Last}")]
    private static partial void LogAdded(ILogger logger, int count, string first, string last);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "ACTION NEEDED: the audit log catch-all partition holds rows, so {Count} missing partitions were not added. "
                  + "Run this in a maintenance window, as a user that may alter audit_log, because audited saves can be blocked until "
                  + "it finishes: {Statement}")]
    private static partial void LogManualSplitNeeded(ILogger logger, int count, string statement);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "ACTION NEEDED: the audit log catch-all partition holds rows although no month is missing. "
                  + "Check for entries dated in the future (a wrong clock on a server)")]
    private static partial void LogCatchAllHasUnexpectedRows(ILogger logger);
}

/// <summary>
/// Runs the partition maintenance when the worker starts and then daily. After a failure, or while someone has to act, it runs
/// hourly, which also repeats the alert.
/// </summary>
public sealed partial class AuditPartitionService(
    AuditPartitionMaintenance maintenance,
    TimeProvider time,
    ILogger<AuditPartitionService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = Interval;
            try
            {
                if (await maintenance.RunAsync(stoppingToken) is { CatchAllHasRows: true })
                {
                    wait = RetryInterval;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
                wait = RetryInterval;
            }

            await Task.Delay(wait, time, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Adding audit log partitions failed; it will try again")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

public static class AuditPartitionServiceExtensions
{
    /// <summary>The worker's partition job. Needs the shared persistence services, for the job lock.</summary>
    public static IServiceCollection AddAuditPartitionMaintenance(this IServiceCollection services, string? maintenanceConnectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<AuditPartitionOptions>()
            .BindConfiguration(AuditPartitionOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton(new AuditMaintenanceConnection(maintenanceConnectionString));
        services.AddSingleton<AuditPartitionMaintenance>();
        services.AddHostedService<AuditPartitionService>();
        return services;
    }
}
