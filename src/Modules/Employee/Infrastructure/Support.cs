using System.Text.Json;
using Hrms.Contracts;
using Hrms.Contracts.Events;
using Hrms.Employee.Application;
using Hrms.Employee.Domain;
using Hrms.Employee.Infrastructure.Persistence;
using Hrms.SharedKernel;

namespace Hrms.Employee.Infrastructure;


/// <summary>Parses the enum words the API accepts (case-insensitive names only, never numbers).</summary>
internal static class EnumText
{
    public static bool TryParse<T>(string? text, out T value)
        where T : struct, Enum
    {
        value = default;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !Enum.GetNames<T>().Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return Enum.TryParse(trimmed, ignoreCase: true, out value);
    }

    public static string Camel<T>(T value)
        where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}

/// <summary>Writes domain events to the outbox in the same unit of work as the change that caused them.</summary>
internal static class Outbox
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Add(EmployeeDbContext db, IDomainEvent domainEvent)
    {
        var type = domainEvent.GetType();
        db.Outbox.Add(OutboxMessage.Create(type.Name, JsonSerializer.Serialize(domainEvent, type, Json), domainEvent.OccurredAt));
    }

    public static IDomainEvent? Read(string eventType, string payload)
    {
        var type = EventTypes.Find(eventType);
        return type is null ? null : JsonSerializer.Deserialize(payload, type, Json) as IDomainEvent;
    }
}

internal static class EventTypes
{
    private static readonly Dictionary<string, Type> Known = typeof(IDomainEvent).Assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsAssignableTo(typeof(IDomainEvent)))
        .ToDictionary(t => t.Name, StringComparer.Ordinal);

    public static Type? Find(string name) => Known.GetValueOrDefault(name);
}

/// <summary>Mapping from entities to the views the API returns.</summary>
internal static class Views
{
    public static JobView Job(EmployeeJob j) => new(
        j.Id, j.EffectiveFrom, j.EffectiveTo, j.LegalEntityId, j.LocationId, j.OrgUnitId, j.DesignationId, j.EmployeeTypeId, j.GradeId,
        j.CostCentreId, j.ManagerId, j.ProbationEnd, j.NoticePeriodDays, EnumText.Camel(j.Reason));

    public static string Status(DateOnly joining, DateOnly? lastWorkingDay, DateOnly today) =>
        joining > today ? "notJoined" : lastWorkingDay is { } last && last < today ? "separated" : "active";

    public static EmployeeSummaryView Summary(EmployeeRecord e, string displayName, EmployeeJob? job, DateOnly today) => new(
        e.Id, e.Code, displayName, e.JoiningDate, e.ConfirmationDate, e.LastWorkingDay, Status(e.JoiningDate, e.LastWorkingDay, today),
        job is null ? null : Job(job), e.RowVersion);

    public static PersonalView Personal(EmployeePersonal p, bool reveal) => new(
        p.FirstName, p.MiddleName, p.LastName, p.DateOfBirth, EnumText.Camel(p.Gender),
        Masking.MaskUnless(reveal, p.PersonalEmail, SensitiveKind.Email),
        Masking.MaskUnless(reveal, p.Phone, SensitiveKind.Phone),
        p.RowVersion);

    public static SeparationView Separation(Separation s) => new(EnumText.Camel(s.Type), s.NoticeDate, s.LastWorkingDay, s.Reason, s.RowVersion);

    public static DocumentView Document(EmployeeDocument d, DateOnly today) =>
        new(d.Id, d.DocumentType, d.Title, d.FileKey, d.IssuedOn, d.ExpiresOn, d.IsExpiredOn(today), d.RowVersion);

    public static DeviceView Device(EmployeeDevice d) =>
        new(d.Id, d.DeviceId, EnumText.Camel(d.Platform), d.Name, EnumText.Camel(d.Status), d.RegisteredAt, d.RevokedAt, d.RowVersion);

    public static ConsentView Consent(EmployeeConsent c) =>
        new(c.Id, c.ConsentType, c.Version, c.Source, c.GrantedAt, c.WithdrawnAt, c.RowVersion);
}

/// <summary>The default sources of "who" and "what" for hosts that have no web request (the worker): nobody signed in.</summary>
internal sealed class NoCurrentUser : ICurrentUser
{
    public Guid? UserId => null;
}
