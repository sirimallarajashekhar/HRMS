using System.Net.Sockets;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MySqlConnector;
using StackExchange.Redis;

namespace Hrms.Api.Health;

/// <summary>Registers readiness checks for the infrastructure dependencies that are configured.</summary>
public static class DependencyHealthChecks
{
    public const string ReadyTag = "ready";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A check is registered only when its connection string is present, so the bare API and the test host
    /// still start without infrastructure. Failure details stay in logs, never in the response body.
    /// </summary>
    public static IHealthChecksBuilder AddDependencyChecks(this IHealthChecksBuilder builder, IConfiguration configuration)
    {
        var mysql = configuration.GetConnectionString("Mysql");
        if (!string.IsNullOrWhiteSpace(mysql))
        {
            builder.Add(new HealthCheckRegistration("mysql", new MySqlHealthCheck(mysql), HealthStatus.Unhealthy, [ReadyTag], CheckTimeout));
        }

        var redis = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redis))
        {
            builder.Add(new HealthCheckRegistration("redis", new RedisHealthCheck(redis), HealthStatus.Unhealthy, [ReadyTag], CheckTimeout));
        }

        var rabbit = configuration.GetConnectionString("RabbitMq");
        if (!string.IsNullOrWhiteSpace(rabbit))
        {
            builder.Add(new HealthCheckRegistration("rabbitmq", new AmqpTcpHealthCheck(rabbit), HealthStatus.Unhealthy, [ReadyTag], CheckTimeout));
        }

        return builder;
    }
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
