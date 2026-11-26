using System.Text.RegularExpressions;
using Hrms.SharedKernel;

namespace Hrms.Employee.Domain;

/// <summary>
/// A national or statutory id (PAN, Aadhaar, SSN... the kinds come from the country pack). The value is stored encrypted, bound to this
/// row, field and tenant; only the last four characters stay readable so a masked value can be shown without decrypting.
/// </summary>
public sealed partial class StatutoryId : TenantEntity
{
    public const int IdTypeMaxLength = 32;
    public const int ValueMaxLength = 64;

    private StatutoryId(Guid employeeId, string idType, string protectedValue, string lastFour)
    {
        EmployeeId = employeeId;
        IdType = idType;
        ProtectedValue = protectedValue;
        LastFour = lastFour;
    }

    /// <summary>For the persistence layer only.</summary>
    private StatutoryId() => IdType = ProtectedValue = LastFour = string.Empty;

    public Guid EmployeeId { get; private set; }

    /// <summary>Upper case snake style, for example PAN. One live id of each type per employee.</summary>
    public string IdType { get; private set; }

    /// <summary>The encrypted value (see IFieldEncryptor); never the plaintext.</summary>
    public string ProtectedValue { get; private set; }

    public string LastFour { get; private set; }

    public static Result<string> NormaliseType(string? idType)
    {
        var normalised = idType?.Trim().ToUpperInvariant() ?? string.Empty;
        return TypePattern().IsMatch(normalised)
            ? Result.Success(normalised)
            : Result.Failure<string>(Validation.Invalid("statutory_id", "type_invalid", "The id type is 1 to 32 characters: letters, digits and underscores."));
    }

    /// <summary>The value as it is checked and encrypted: trimmed, no inner spaces, upper case.</summary>
    public static Result<string> NormaliseValue(string? value)
    {
        var normalised = new string((value ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        return normalised.Length is 0 or > ValueMaxLength
            ? Result.Failure<string>(Validation.Invalid("statutory_id", "value_invalid", "The id value is 1 to 64 characters."))
            : Result.Success(normalised);
    }

    public static StatutoryId Create(Guid employeeId, string normalisedType, string normalisedValue, string protectedValue) =>
        new(employeeId, normalisedType, protectedValue, normalisedValue.Length <= 4 ? normalisedValue : normalisedValue[^4..]);

    /// <summary>Replaces the value (already normalised and encrypted by the caller).</summary>
    public void Replace(string normalisedValue, string protectedValue)
    {
        ProtectedValue = protectedValue;
        LastFour = normalisedValue.Length <= 4 ? normalisedValue : normalisedValue[^4..];
    }

    [GeneratedRegex("^[A-Z0-9_]{1,32}$")]
    private static partial Regex TypePattern();
}

/// <summary>Metadata of a document held for an employee (contract, ID proof, visa). The file itself lives in object storage under an opaque key.</summary>
public sealed class EmployeeDocument : TenantEntity
{
    public const int TypeMaxLength = 64;
    public const int TitleMaxLength = 200;
    public const int FileKeyMaxLength = 500;

    private EmployeeDocument(Guid employeeId, string documentType, string title, string? fileKey, DateOnly? issuedOn, DateOnly? expiresOn)
    {
        EmployeeId = employeeId;
        DocumentType = documentType;
        Title = title;
        FileKey = fileKey;
        IssuedOn = issuedOn;
        ExpiresOn = expiresOn;
    }

    /// <summary>For the persistence layer only.</summary>
    private EmployeeDocument() => DocumentType = Title = string.Empty;

    public Guid EmployeeId { get; private set; }

    public string DocumentType { get; private set; }

    public string Title { get; private set; }

    /// <summary>Opaque key in object storage; null until a file is attached.</summary>
    public string? FileKey { get; private set; }

    public DateOnly? IssuedOn { get; private set; }

    public DateOnly? ExpiresOn { get; private set; }

    public bool IsExpiredOn(DateOnly date) => ExpiresOn is { } expiry && expiry < date;

    public static Result<EmployeeDocument> Create(Guid employeeId, string? documentType, string? title, string? fileKey, DateOnly? issuedOn, DateOnly? expiresOn)
    {
        var valid = Validate(documentType, title, fileKey, issuedOn, expiresOn);
        return valid.IsFailure
            ? Result.Failure<EmployeeDocument>(valid.Error)
            : Result.Success(new EmployeeDocument(employeeId, valid.Value.Type, valid.Value.Title, valid.Value.Key, issuedOn, expiresOn));
    }

    public Result Update(string? documentType, string? title, string? fileKey, DateOnly? issuedOn, DateOnly? expiresOn)
    {
        var valid = Validate(documentType, title, fileKey, issuedOn, expiresOn);
        if (valid.IsFailure)
        {
            return valid;
        }

        DocumentType = valid.Value.Type;
        Title = valid.Value.Title;
        FileKey = valid.Value.Key;
        IssuedOn = issuedOn;
        ExpiresOn = expiresOn;
        return Result.Success();
    }

    private static Result<(string Type, string Title, string? Key)> Validate(string? documentType, string? title, string? fileKey, DateOnly? issuedOn, DateOnly? expiresOn)
    {
        var type = Validation.Required(documentType, TypeMaxLength)?.ToUpperInvariant();
        var name = Validation.Required(title, TitleMaxLength);
        if (type is null || name is null)
        {
            return Fail("details_invalid", "A document type (up to 64 characters) and a title (up to 200) are required.");
        }

        var key = Validation.Optional(fileKey, FileKeyMaxLength, out var keyTooLong);
        if (keyTooLong || (key is not null && (key.Contains("..", StringComparison.Ordinal) || key.StartsWith('/'))))
        {
            return Fail("file_key_invalid", "The file key is an opaque storage key of at most 500 characters.");
        }

        if (issuedOn is { } issued && expiresOn is { } expires && expires < issued)
        {
            return Fail("expiry_before_issue", "The expiry date cannot be before the issue date.");
        }

        return Result.Success((type, name, key));
    }

    private static Result<(string, string, string?)> Fail(string reason, string message) =>
        Result.Failure<(string, string, string?)>(Validation.Invalid("document", reason, message));
}

public enum DevicePlatform
{
    Android = 1,
    iOS = 2,
    Web = 3,
}

public enum DeviceStatus
{
    Active = 1,
    Revoked = 2,
}

/// <summary>A device an employee may punch from. One device belongs to one employee at a time; revoking it keeps the record.</summary>
public sealed class EmployeeDevice : TenantEntity
{
    public const int DeviceIdMaxLength = 128;
    public const int NameMaxLength = 100;

    private EmployeeDevice(Guid employeeId, string deviceId, DevicePlatform platform, string? name, DateTime registeredAt)
    {
        EmployeeId = employeeId;
        DeviceId = deviceId;
        Platform = platform;
        Name = name;
        RegisteredAt = registeredAt;
        Status = DeviceStatus.Active;
    }

    /// <summary>For the persistence layer only.</summary>
    private EmployeeDevice() => DeviceId = string.Empty;

    public Guid EmployeeId { get; private set; }

    /// <summary>The identifier the device app reports (unique per tenant among live rows).</summary>
    public string DeviceId { get; private set; }

    public DevicePlatform Platform { get; private set; }

    public string? Name { get; private set; }

    public DeviceStatus Status { get; private set; }

    public DateTime RegisteredAt { get; private set; }

    public DateTime? RevokedAt { get; private set; }

    public static Result<EmployeeDevice> Register(Guid employeeId, string? deviceId, DevicePlatform platform, string? name, DateTime now)
    {
        var id = Validation.Required(deviceId, DeviceIdMaxLength);
        if (id is null)
        {
            return Result.Failure<EmployeeDevice>(Validation.Invalid("device", "device_id_invalid", "The device id is 1 to 128 characters."));
        }

        if (!Enum.IsDefined(platform))
        {
            return Result.Failure<EmployeeDevice>(Validation.Invalid("device", "platform_invalid", "The platform is not recognised."));
        }

        var label = Validation.Optional(name, NameMaxLength, out var tooLong);
        return tooLong
            ? Result.Failure<EmployeeDevice>(Validation.Invalid("device", "name_invalid", "The device name is at most 100 characters."))
            : Result.Success(new EmployeeDevice(employeeId, id, platform, label, now));
    }

    public Result Revoke(DateTime now)
    {
        if (Status == DeviceStatus.Revoked)
        {
            return Result.Failure(Error.Conflict("employee.device.already_revoked", "The device is already revoked."));
        }

        Status = DeviceStatus.Revoked;
        RevokedAt = now;
        return Result.Success();
    }
}

/// <summary>A consent an employee gave (or withdrew) for a purpose, with the version of the text they agreed to.</summary>
public sealed class EmployeeConsent : TenantEntity
{
    public const int TypeMaxLength = 64;
    public const int VersionMaxLength = 32;
    public const int SourceMaxLength = 64;

    private EmployeeConsent(Guid employeeId, string consentType, string version, string? source, DateTime grantedAt)
    {
        EmployeeId = employeeId;
        ConsentType = consentType;
        Version = version;
        Source = source;
        GrantedAt = grantedAt;
    }

    /// <summary>For the persistence layer only.</summary>
    private EmployeeConsent() => ConsentType = Version = string.Empty;

    public Guid EmployeeId { get; private set; }

    public string ConsentType { get; private set; }

    public string Version { get; private set; }

    public string? Source { get; private set; }

    public DateTime GrantedAt { get; private set; }

    public DateTime? WithdrawnAt { get; private set; }

    public bool IsActive => WithdrawnAt is null;

    public static Result<EmployeeConsent> Grant(Guid employeeId, string? consentType, string? version, string? source, DateTime now)
    {
        var type = Validation.Required(consentType, TypeMaxLength)?.ToUpperInvariant();
        var ver = Validation.Required(version, VersionMaxLength);
        if (type is null || ver is null)
        {
            return Result.Failure<EmployeeConsent>(Validation.Invalid("consent", "details_invalid", "A consent type (up to 64 characters) and a version (up to 32) are required."));
        }

        var origin = Validation.Optional(source, SourceMaxLength, out var tooLong);
        return tooLong
            ? Result.Failure<EmployeeConsent>(Validation.Invalid("consent", "source_invalid", "The source is at most 64 characters."))
            : Result.Success(new EmployeeConsent(employeeId, type, ver, origin, now));
    }

    public Result Withdraw(DateTime now)
    {
        if (WithdrawnAt is not null)
        {
            return Result.Failure(Error.Conflict("employee.consent.already_withdrawn", "The consent was already withdrawn."));
        }

        WithdrawnAt = now;
        return Result.Success();
    }
}

/// <summary>
/// A domain event waiting to be delivered. It is written in the same transaction as the change that caused it, so a rolled-back change
/// leaves no event and a committed one is never lost; a relay delivers it afterwards, at least once.
/// </summary>
public sealed class OutboxMessage : TenantEntity
{
    public const int TypeMaxLength = 200;

    private OutboxMessage(string eventType, string payload, DateTime occurredAt)
    {
        EventType = eventType;
        Payload = payload;
        OccurredAt = occurredAt;
    }

    /// <summary>For the persistence layer only.</summary>
    private OutboxMessage() => EventType = Payload = string.Empty;

    public string EventType { get; private set; }

    public string Payload { get; private set; }

    public DateTime OccurredAt { get; private set; }

    public DateTime? ProcessedAt { get; private set; }

    public int Attempts { get; private set; }

    public string? LastError { get; private set; }

    public static OutboxMessage Create(string eventType, string payload, DateTime occurredAt) => new(eventType, payload, occurredAt);

    public void MarkProcessed(DateTime at)
    {
        ProcessedAt = at;
        LastError = null;
    }

    public void MarkFailed(string error)
    {
        Attempts++;
        LastError = error.Length > 500 ? error[..500] : error;
    }
}
