using System.Text.RegularExpressions;
using Hrms.Contracts;
using Hrms.Contracts.Events;
using Hrms.Employee.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hrms.Employee.Infrastructure;

/// <summary>
/// Used until a country pack registers rules for a country: it only checks that the value is a plausible identifier (3 to 64 letters,
/// digits, hyphens or slashes). Country packs replace it with the real format and checksum rules.
/// </summary>
internal sealed partial class BasicStatutoryIdValidator : IStatutoryIdValidator
{
    public Task<string?> ValidateAsync(string countryPackCode, string idType, string value, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(Shape().IsMatch(value) ? null : "The id must be 3 to 64 letters, digits, hyphens or slashes.");

    [GeneratedRegex("^[A-Z0-9/-]{3,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();
}

/// <summary>
/// The default publisher: it logs that an event happened (type, tenant and ids only, never personal data) and succeeds. A message-broker
/// publisher replaces it when the first consumer outside this process exists.
/// </summary>
internal sealed class LoggingEventPublisher(ILogger<LoggingEventPublisher> logger) : IEventPublisher
{
    public Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Domain event {EventType} for tenant {TenantId} occurred at {OccurredAt:o}", domainEvent.GetType().Name, domainEvent.TenantId, domainEvent.OccurredAt);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Tells Core a master is in use while any job period (past, current or future) points at it, so history is never orphaned.</summary>
internal sealed class JobReferenceUsageCheck(EmployeeDbContext db, string master) : IMasterUsageCheck
{
    public string Master { get; } = master;

    public Task<bool> IsInUseAsync(Guid id, CancellationToken cancellationToken)
    {
        var jobs = db.Jobs.AsNoTracking();
        return Master switch
        {
            MasterKinds.Location => jobs.AnyAsync(j => j.LocationId == id, cancellationToken),
            MasterKinds.OrgUnit => jobs.AnyAsync(j => j.OrgUnitId == id, cancellationToken),
            MasterKinds.Grade => jobs.AnyAsync(j => j.GradeId == id, cancellationToken),
            MasterKinds.Designation => jobs.AnyAsync(j => j.DesignationId == id, cancellationToken),
            MasterKinds.CostCentre => jobs.AnyAsync(j => j.CostCentreId == id, cancellationToken),
            MasterKinds.EmployeeType => jobs.AnyAsync(j => j.EmployeeTypeId == id, cancellationToken),
            _ => Task.FromResult(false),
        };
    }
}

internal sealed class EmployeeDirectory(EmployeeDbContext db) : IEmployeeDirectory
{
    public Task<bool> ExistsAsync(Guid employeeId, CancellationToken cancellationToken) =>
        db.Employees.AsNoTracking().AnyAsync(e => e.Id == employeeId, cancellationToken);
}
