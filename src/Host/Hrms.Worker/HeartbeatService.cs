namespace Hrms.Worker;

/// <summary>Placeholder so the worker host has a visible lifecycle until real jobs exist.</summary>
public sealed partial class HeartbeatService(ILogger<HeartbeatService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            LogHeartbeat(logger);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker started")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Worker heartbeat")]
    private static partial void LogHeartbeat(ILogger logger);
}
