using Hrms.Contracts;
using Hrms.Contracts.Events;
using Hrms.Employee.Infrastructure.Persistence;
using Hrms.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hrms.Employee.Infrastructure;

/// <summary>
/// The two steps that turn data into events, each safe to repeat. <see cref="EmitDueEventsAsync"/> writes "job changed" and "separated"
/// events for rows that have just taken effect (the rows are marked, so each goes out once). <see cref="RelayAsync"/> hands outbox
/// messages to the publisher and marks them processed; delivery is at least once, so consumers must tolerate repeats.
/// </summary>
public static class EmployeeEventPipeline
{
    public const int BatchSize = 500;
    public const int MaxAttempts = 20;

    public static async Task<int> EmitDueEventsAsync(EmployeeDbContext db, TenantId tenant, DateOnly today, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var jobs = await db.Jobs.Where(j => j.EffectiveFrom <= today && j.EventPublishedAt == null)
            .OrderBy(j => j.EffectiveFrom).Take(BatchSize).ToListAsync(cancellationToken);
        foreach (var job in jobs)
        {
            Outbox.Add(db, new EmployeeJobChanged(tenant.Value, job.EmployeeId, job.Id, job.EffectiveFrom, EnumText.Camel(job.Reason), now));
            job.MarkEventPublished(now);
        }

        var separations = await db.Separations.Where(s => s.LastWorkingDay < today && s.EventPublishedAt == null)
            .OrderBy(s => s.LastWorkingDay).Take(BatchSize).ToListAsync(cancellationToken);
        foreach (var separation in separations)
        {
            Outbox.Add(db, new EmployeeSeparated(tenant.Value, separation.EmployeeId, separation.LastWorkingDay, now));
            separation.MarkEventPublished(now);
        }

        if (jobs.Count + separations.Count == 0)
        {
            return 0;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another runner (or an edit of the same row) got there first; nothing was written, and the next tick looks again.
            db.ChangeTracker.Clear();
            return 0;
        }

        return jobs.Count + separations.Count;
    }

    public static async Task<int> RelayAsync(EmployeeDbContext db, IEventPublisher publisher, DateTime now, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(logger);

        var messages = await db.Outbox.Where(m => m.ProcessedAt == null && m.Attempts < MaxAttempts)
            .OrderBy(m => m.OccurredAt).ThenBy(m => m.Id).Take(BatchSize).ToListAsync(cancellationToken);

        var delivered = 0;
        foreach (var message in messages)
        {
            try
            {
                var domainEvent = Outbox.Read(message.EventType, message.Payload)
                                  ?? throw new InvalidOperationException($"Unknown event type {message.EventType}.");
                await publisher.PublishAsync(domainEvent, cancellationToken);
                message.MarkProcessed(now);
                delivered++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Delivering outbox message {MessageId} ({EventType}) failed (attempt {Attempt})", message.Id, message.EventType, message.Attempts + 1);
                message.MarkFailed(ex.GetType().Name);
            }
        }

        if (messages.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return delivered;
    }
}

/// <summary>
/// Runs the event pipeline for every active tenant on a short timer, under a database lock so only one worker instance does it at a time.
/// It replaces a once-a-day job on purpose: the scan is one indexed query per tenant, so changes announce themselves within a tick of
/// becoming effective instead of up to a day later.
/// </summary>
public sealed class EmployeeEventService(
    IServiceScopeFactory scopes,
    IJobLock jobLock,
    TimeProvider time,
    ILogger<EmployeeEventService> logger) : BackgroundService
{
    public const string LockName = "employee-events";

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The employee event service tick failed; it will try again");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass over every active tenant. Returns false when another instance holds the lock.</summary>
    public async Task<bool> TickAsync(CancellationToken cancellationToken)
    {
        var lease = await jobLock.TryAcquireAsync(LockName, cancellationToken);
        if (lease is null)
        {
            return false;
        }

        await using (lease)
        {
            IReadOnlyList<Guid> tenants;
            await using (var outer = scopes.CreateAsyncScope())
            {
                tenants = await outer.ServiceProvider.GetRequiredService<IActiveTenantSource>().ListActiveTenantIdsAsync(cancellationToken);
            }

            foreach (var tenantId in tenants)
            {
                try
                {
                    await ProcessTenantAsync(tenantId, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Processing employee events failed for tenant {TenantId}", tenantId);
                }
            }
        }

        return true;
    }

    public async Task ProcessTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        provider.GetRequiredService<TenantContext>().Set(new TenantId(tenantId));

        var db = provider.GetRequiredService<EmployeeDbContext>();
        var now = time.GetUtcNow().UtcDateTime;
        var emitted = await EmployeeEventPipeline.EmitDueEventsAsync(db, new TenantId(tenantId), DateOnly.FromDateTime(now), now, cancellationToken);
        var delivered = await EmployeeEventPipeline.RelayAsync(db, provider.GetRequiredService<IEventPublisher>(), now, logger, cancellationToken);
        if (emitted + delivered > 0)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Tenant {TenantId}: {Emitted} events became due and {Delivered} were delivered", tenantId, emitted, delivered);
            }
        }
    }
}
