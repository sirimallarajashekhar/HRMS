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
}

/// <summary>
/// The connection that may alter audit_log. In production the application's own user should only be able to read and insert audit
/// rows, so adding partitions needs a more privileged user (ConnectionStrings:MysqlMaintenance).
/// </summary>
public sealed record AuditMaintenanceConnection(string? ConnectionString);

/// <summary>
/// Adds monthly partitions to audit_log ahead of time by splitting the catch-all. It only ever adds: nothing is dropped or deleted.
/// It reads the table's definition and changes its layout, and returns no tenant's rows. MySQL cannot bind identifiers or partition
/// bounds as parameters, so the statement is built from dates this code computed, never from input.
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
    /// An ALTER waits for open transactions that touched the table, and new writes queue behind it while it waits. Giving up quickly
    /// and trying again later is better than stalling every save.
    /// </summary>
    private const int LockWaitSeconds = 5;

    /// <returns>The partitions added, or null when another instance holds the lock.</returns>
    public async Task<IReadOnlyList<AuditPartition>?> RunAsync(CancellationToken cancellationToken)
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

    private async Task<IReadOnlyList<AuditPartition>> AddMissingAsync(CancellationToken cancellationToken)
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
        if (missing.Count == 0)
        {
            return missing;
        }

        if (await CatchAllHasRowsAsync(connection, cancellationToken))
        {
            LogCatchAllHasRows(logger);
        }

        await ExecuteAsync(
            connection, string.Create(CultureInfo.InvariantCulture, $"SET SESSION lock_wait_timeout = {LockWaitSeconds}"), cancellationToken);
        await ExecuteAsync(connection, SplitStatement(missing), cancellationToken);
        LogAdded(logger, missing.Count, missing[0].Name, missing[^1].Name);
        return missing;
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

    private static async Task ExecuteAsync(MySqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Added {Count} audit log partitions, {First} to {Last}")]
    private static partial void LogAdded(ILogger logger, int count, string first, string last);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The audit log catch-all partition holds rows, so partitions fell behind. Moving them can block audited saves until it finishes")]
    private static partial void LogCatchAllHasRows(ILogger logger);
}

/// <summary>Runs the partition maintenance when the worker starts and then daily; after a failure it tries again within the hour.</summary>
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
                await maintenance.RunAsync(stoppingToken);
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
