using System.Net.Sockets;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MySqlConnector;
using StackExchange.Redis;

namespace Hrms.Api.Health;

/// <summary>Registers readiness checks for the infrastructure dependencies the API needs.</summary>
public static class DependencyHealthChecks
{
    public const string ReadyTag = "ready";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// In Development a dependency without a connection string is skipped, so the bare API still runs.
    /// Everywhere else a missing connection string registers a check that reports Unhealthy: readiness must
    /// fail closed, otherwise a typo in a secret name would route traffic to an instance with no database.
    /// Failure details stay in logs, never in the response body.
    /// </summary>
    public static IHealthChecksBuilder AddDependencyChecks(this IHealthChecksBuilder builder, IConfiguration configuration, IHostEnvironment environment)
    {
        var required = !environment.IsDevelopment();

        Register(builder, "mysql", configuration.GetConnectionString("Mysql"), required, cs => new MySqlHealthCheck(cs));
        Register(builder, "redis", configuration.GetConnectionString("Redis"), required, cs => new RedisHealthCheck(cs));
        Register(builder, "rabbitmq", configuration.GetConnectionString("RabbitMq"), required, cs => new AmqpTcpHealthCheck(cs));

        return builder;
    }

    private static void Register(IHealthChecksBuilder builder, string name, string? connectionString, bool required, Func<string, IHealthCheck> create)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            builder.Add(new HealthCheckRegistration(name, create(connectionString), HealthStatus.Unhealthy, [ReadyTag], CheckTimeout));
        }
        else if (required)
        {
            builder.Add(new HealthCheckRegistration(name, new NotConfiguredHealthCheck(name), HealthStatus.Unhealthy, [ReadyTag], CheckTimeout));
        }
    }
}

internal sealed class NotConfiguredHealthCheck(string name) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(HealthCheckResult.Unhealthy($"{name} connection string is not configured."));
}

internal sealed class MySqlHealthCheck(string connectionString) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new MySqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("MySQL is unreachable.", ex);
        }
    }
}

internal sealed class RedisHealthCheck : IHealthCheck
{
    private readonly Lazy<Task<ConnectionMultiplexer>> _connection;

    public RedisHealthCheck(string connectionString)
    {
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false; // keep retrying in the background; the PING below reports the real state
        _connection = new Lazy<Task<ConnectionMultiplexer>>(() => ConnectionMultiplexer.ConnectAsync(options));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var multiplexer = await _connection.Value.WaitAsync(cancellationToken);
            await multiplexer.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Redis is unreachable.", ex);
        }
    }
}

/// <summary>Proves the broker accepts TCP connections on its AMQP port (connection string is an amqp:// URI).</summary>
internal sealed class AmqpTcpHealthCheck(string amqpUri) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var uri = new Uri(amqpUri);
            using var client = new TcpClient();
            await client.ConnectAsync(uri.Host, uri.Port > 0 ? uri.Port : 5672, cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("RabbitMQ is unreachable.", ex);
        }
    }
}
