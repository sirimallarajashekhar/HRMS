using System.Net;
using FluentAssertions;
using Hrms.Contracts;
using Hrms.Contracts.Events;
using Hrms.Core.IntegrationTests.Persistence;
using Hrms.Employee.Infrastructure;
using Hrms.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hrms.Core.IntegrationTests.Employee;

/// <summary>The path from "something took effect" to "an event was delivered": the due-event scan, the outbox relay, the lock and the service.</summary>
[Collection(MySqlTestGroup.Name)]
public class EmployeeEventsTests(EmployeeApiFixture fixture) : EmployeeApiTestBase(fixture), IClassFixture<EmployeeApiFixture>
{
    private sealed class CapturingPublisher : IEventPublisher
    {
        public List<IDomainEvent> Events { get; } = [];

        public bool Fail { get; set; }

        public Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new InvalidOperationException("the broker is down");
            }

            Events.Add(domainEvent);
            return Task.CompletedTask;
        }
    }

    private Hrms.Employee.Infrastructure.Persistence.EmployeeDbContext Db() => EmployeeTestDb.Context(ConnectionString, Acme.Tenant.TenantId);

    private static readonly DateTime Noon = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    // --- due events -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFutureJobChange_IsAnnouncedOnItsEffectiveDate_AndOnlyOnce()
    {
        var employee = await CreateEmployeeAsync(Acme, CreateBody(Acme, joining: new DateOnly(2026, 1, 1)));
        var from = Today.AddDays(5);
        await ChangeJobAsync(Id(employee), Version(employee), from, JobBody(Acme, unit: Acme.DepartmentBId), "promotion");

        await using var db = Db();
        var tenant = Acme.Tenant.TenantId;
        async Task<List<Hrms.Employee.Domain.OutboxMessage>> EventsAsync() =>
            await db.Outbox.AsNoTracking().Where(m => m.EventType == "EmployeeJobChanged" && m.Payload.Contains(Id(employee).ToString())).ToListAsync();

        await EmployeeEventPipeline.EmitDueEventsAsync(db, tenant, from.AddDays(-1), Noon, CancellationToken.None);
        var early = await EventsAsync();
        await EmployeeEventPipeline.EmitDueEventsAsync(db, tenant, from, Noon, CancellationToken.None);
        var onTheDay = await EventsAsync();
        await EmployeeEventPipeline.EmitDueEventsAsync(db, tenant, from.AddDays(3), Noon, CancellationToken.None);
        var again = await EventsAsync();

        early.Should().BeEmpty("the day before, this employee's change is not yet in force");
        onTheDay.Should().ContainSingle();
        again.Should().ContainSingle("the row is marked, so it is never announced twice");
        onTheDay[0].Payload.Should().Contain("promotion").And.Contain(Iso(from));
    }

    [Fact]
    public async Task ASeparation_IsAnnouncedTheDayAfterTheLastWorkingDay()
    {
        var employee = await CreateEmployeeAsync(Acme);
        var last = Today.AddDays(7);
        using var recorded = await PutAsync($"/employees/{Id(employee)}/separation", new { type = "resignation", lastWorkingDay = Iso(last) }, Acme.HrAdmin);
        recorded.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = Db();
        var tenant = Acme.Tenant.TenantId;
        await EmployeeEventPipeline.EmitDueEventsAsync(db, tenant, last, Noon, CancellationToken.None);
        var sameDay = await db.Outbox.AsNoTracking().CountAsync(m => m.EventType == "EmployeeSeparated" && m.Payload.Contains(Id(employee).ToString()));
        await EmployeeEventPipeline.EmitDueEventsAsync(db, tenant, last.AddDays(1), Noon, CancellationToken.None);
        var nextDay = await db.Outbox.AsNoTracking().CountAsync(m => m.EventType == "EmployeeSeparated" && m.Payload.Contains(Id(employee).ToString()));

        sameDay.Should().Be(0, "the person is still employed on the last working day");
        nextDay.Should().Be(1);
    }

    [Fact]
    public async Task ACorrectedSeparation_IsAnnouncedForTheCorrectedDate_NotTheOldOne()
    {
        var employee = await CreateEmployeeAsync(Acme);
        using var first = await PutAsync($"/employees/{Id(employee)}/separation", new { type = "resignation", lastWorkingDay = Iso(Today.AddDays(2)) }, Acme.HrAdmin);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        await using (var db = Db())
        {
            await EmployeeEventPipeline.EmitDueEventsAsync(db, Acme.Tenant.TenantId, Today.AddDays(3), Noon, CancellationToken.None);
        }

        // The scan marked the separation as announced, which is a change to the row: read the current version before correcting it.
        var current = (await ProfileAsync(Id(employee), Acme.HrAdmin)).GetProperty("separation");
        using var corrected = await PutAsync($"/employees/{Id(employee)}/separation", new { type = "resignation", lastWorkingDay = Iso(Today.AddDays(20)), rowVersion = Version(current) }, Acme.HrAdmin);

        corrected.StatusCode.Should().Be(HttpStatusCode.OK, await corrected.Content.ReadAsStringAsync());
        await using var check = Db();
        await EmployeeEventPipeline.EmitDueEventsAsync(check, Acme.Tenant.TenantId, Today.AddDays(10), Noon, CancellationToken.None);
        var count = await check.Outbox.AsNoTracking().CountAsync(m => m.EventType == "EmployeeSeparated" && m.Payload.Contains(Id(employee).ToString()));
        count.Should().Be(1, "only the first announcement exists until the corrected last day passes");
    }

    // --- relay ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Relay_DeliversEachMessage_MarksItProcessed_AndDoesNotRepeat()
    {
        var employee = await CreateEmployeeAsync(Acme);
        var publisher = new CapturingPublisher();

        await using var db = Db();
        var first = await EmployeeEventPipeline.RelayAsync(db, publisher, Noon, NullLogger.Instance, CancellationToken.None);
        var second = await EmployeeEventPipeline.RelayAsync(db, publisher, Noon, NullLogger.Instance, CancellationToken.None);

        first.Should().BeGreaterThanOrEqualTo(1);
        second.Should().Be(0);
        publisher.Events.OfType<EmployeeCreated>().Should().Contain(e => e.EmployeeId == Id(employee) && e.TenantId == Acme.Tenant.TenantId.Value);
        (await db.Outbox.AsNoTracking().CountAsync(m => m.ProcessedAt == null && m.Attempts < EmployeeEventPipeline.MaxAttempts && m.Payload.Contains(Id(employee).ToString()))).Should().Be(0);
    }

    [Fact]
    public async Task Relay_KeepsAMessageThatCouldNotBeDelivered_AndCountsTheAttempt()
    {
        var employee = await CreateEmployeeAsync(Acme);
        var broken = new CapturingPublisher { Fail = true };

        await using var db = Db();
        var delivered = await EmployeeEventPipeline.RelayAsync(db, broken, Noon, NullLogger.Instance, CancellationToken.None);
        var pending = await db.Outbox.AsNoTracking().Where(m => m.ProcessedAt == null && m.Payload.Contains(Id(employee).ToString())).ToListAsync();

        delivered.Should().Be(0);
        pending.Should().NotBeEmpty().And.OnlyContain(m => m.Attempts >= 1 && m.LastError == "InvalidOperationException");
        pending.Select(m => m.LastError).Should().NotContain(e => e!.Contains("broker is down"), "error text can carry data, so only the type is kept");

        var healthy = new CapturingPublisher();
        await EmployeeEventPipeline.RelayAsync(db, healthy, Noon, NullLogger.Instance, CancellationToken.None);
        healthy.Events.OfType<EmployeeCreated>().Should().Contain(e => e.EmployeeId == Id(employee));
    }

    [Fact]
    public async Task Relay_GivesUpOnAMessageAfterTheMaximumAttempts_InsteadOfRetryingForever()
    {
        await CreateEmployeeAsync(Acme);
        await using var db = Db();
        var message = await db.Outbox.Where(m => m.ProcessedAt == null).OrderBy(m => m.OccurredAt).FirstAsync();
        for (var i = 0; i < EmployeeEventPipeline.MaxAttempts; i++)
        {
            message.MarkFailed("x");
        }

        await db.SaveChangesAsync();

        var publisher = new CapturingPublisher();
        await EmployeeEventPipeline.RelayAsync(db, publisher, Noon, NullLogger.Instance, CancellationToken.None);

        var after = await db.Outbox.AsNoTracking().SingleAsync(m => m.Id == message.Id);
        after.ProcessedAt.Should().BeNull("a poisoned message waits for a person instead of looping");
        after.Attempts.Should().Be(EmployeeEventPipeline.MaxAttempts, "it is no longer tried");
    }

    [Fact]
    public async Task OutboxMessages_AreIsolatedPerTenant()
    {
        await CreateEmployeeAsync(Globex);

        await using var acme = Db();
        await using var globex = EmployeeTestDb.Context(ConnectionString, Globex.Tenant.TenantId);

        var globexEvents = await globex.Outbox.AsNoTracking().Select(m => m.Payload).ToListAsync();
        var acmeEvents = await acme.Outbox.AsNoTracking().Select(m => m.Payload).ToListAsync();
        globexEvents.Should().NotBeEmpty();
        acmeEvents.Where(p => p.Contains(Globex.Tenant.TenantId.Value.ToString())).Should().BeEmpty();
    }

    // --- the lock and the service ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task JobLock_AllowsOneHolderAtATime_AndFreesTheLockOnDispose()
    {
        var jobLock = Host.Services.GetRequiredService<IJobLock>();
        var name = $"test-{Guid.NewGuid():N}"[..30];

        var first = await jobLock.TryAcquireAsync(name, CancellationToken.None);
        var second = await jobLock.TryAcquireAsync(name, CancellationToken.None);
        var other = await jobLock.TryAcquireAsync(name + "-b", CancellationToken.None);

        first.Should().NotBeNull();
        second.Should().BeNull("someone else holds it");
        other.Should().NotBeNull("a different name is a different lock");
        await first!.DisposeAsync();
        var afterRelease = await jobLock.TryAcquireAsync(name, CancellationToken.None);
        afterRelease.Should().NotBeNull();
        await afterRelease!.DisposeAsync();
        await other!.DisposeAsync();
    }

    [Fact]
    public async Task JobLock_RejectsALongName()
    {
        var jobLock = Host.Services.GetRequiredService<IJobLock>();

        var act = async () => await jobLock.TryAcquireAsync(new string('x', 101), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private EmployeeEventService NewService(IJobLock? jobLock = null) => new(
        Host.Services.GetRequiredService<IServiceScopeFactory>(),
        jobLock ?? Host.Services.GetRequiredService<IJobLock>(),
        TimeProvider.System,
        NullLogger<EmployeeEventService>.Instance);

    [Fact]
    public async Task Service_ProcessesEveryActiveTenant_AndDeliversPendingEvents()
    {
        await CreateEmployeeAsync(Acme);
        await CreateEmployeeAsync(Globex);

        var ran = await NewService().TickAsync(CancellationToken.None);

        ran.Should().BeTrue();
        await using var acmeDb = Db();
        await using var globexDb = EmployeeTestDb.Context(ConnectionString, Globex.Tenant.TenantId);
        (await acmeDb.Outbox.AsNoTracking().Where(m => m.ProcessedAt == null && m.Attempts < EmployeeEventPipeline.MaxAttempts).CountAsync()).Should().Be(0);
        (await globexDb.Outbox.AsNoTracking().Where(m => m.ProcessedAt == null && m.Attempts < EmployeeEventPipeline.MaxAttempts).CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Service_DoesNothing_WhileAnotherInstanceHoldsTheLock()
    {
        await CreateEmployeeAsync(Acme);
        var jobLock = Host.Services.GetRequiredService<IJobLock>();
        var held = await jobLock.TryAcquireAsync(EmployeeEventService.LockName, CancellationToken.None);
        held.Should().NotBeNull();

        bool ran;
        try
        {
            ran = await NewService().TickAsync(CancellationToken.None);
        }
        finally
        {
            await held!.DisposeAsync();
        }

        ran.Should().BeFalse();
        await using var db = Db();
        (await db.Outbox.AsNoTracking().CountAsync(m => m.ProcessedAt == null)).Should().BeGreaterThan(0, "nothing was delivered by the instance that did not hold the lock");
    }
}
