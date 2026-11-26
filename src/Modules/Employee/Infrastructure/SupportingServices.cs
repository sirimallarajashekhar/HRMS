using System.Security.Cryptography;
using Hrms.Contracts;
using Hrms.Employee.Application;
using Hrms.Employee.Domain;
using Hrms.Employee.Infrastructure.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hrms.Employee.Infrastructure;

/// <summary>
/// Encrypts and decrypts statutory id values. The context string is bound into the ciphertext (tenant, employee, id type), so a value
/// copied to another employee or tenant will not decrypt. A value that cannot be decrypted shows as unreadable and is logged without it.
/// </summary>
internal interface IStatutoryIdProtector
{
    string Protect(TenantId tenant, Guid employeeId, string idType, string normalisedValue);

    StatutoryIdView View(TenantId tenant, StatutoryId id, bool reveal);
}

internal sealed class StatutoryIdProtector(IFieldEncryptor encryptor, ILogger<StatutoryIdProtector> logger) : IStatutoryIdProtector
{
    public const string Unreadable = "(unreadable)";

    public string Protect(TenantId tenant, Guid employeeId, string idType, string normalisedValue) =>
        encryptor.Protect(normalisedValue, Context(tenant, employeeId, idType));

    public StatutoryIdView View(TenantId tenant, StatutoryId id, bool reveal)
    {
        string value;
        try
        {
            var plain = encryptor.Unprotect(id.ProtectedValue, Context(tenant, id.EmployeeId, id.IdType));
            value = (reveal ? plain : Masking.Mask(plain, SensitiveKind.NationalId)) ?? string.Empty;
        }
        catch (CryptographicException)
        {
            logger.LogWarning("A statutory id could not be decrypted (id {StatutoryIdRecord}); check the encryption keys", id.Id);
            value = Unreadable;
        }

        return new StatutoryIdView(id.Id, id.IdType, value, id.RowVersion);
    }

    private static string Context(TenantId tenant, Guid employeeId, string idType) => $"employee_statutory_id.value:{tenant}:{employeeId}:{idType}";
}

internal sealed class StatutoryIdService(
    EmployeeDbContext db,
    EmployeeAccess access,
    IStatutoryIdProtector protector,
    IStatutoryIdValidator validator,
    IOrgStructureLookup org,
    ITenantContext tenantContext) : IStatutoryIdService
{
    public async Task<Result<IReadOnlyList<StatutoryIdView>>> ListAsync(Guid employeeId, bool revealSensitive, CancellationToken cancellationToken)
    {
        var employee = await access.LoadAsync(employeeId, Permissions.Employee.StatutoryIdView, tracked: false, cancellationToken);
        if (employee.IsFailure)
        {
            return Result.Failure<IReadOnlyList<StatutoryIdView>>(employee.Error);
        }

        var tenant = tenantContext.Require();
        IReadOnlyList<StatutoryIdView> views = (await db.StatutoryIds.AsNoTracking().Where(s => s.EmployeeId == employeeId).OrderBy(s => s.IdType).ToListAsync(cancellationToken))
            .Select(s => protector.View(tenant, s, revealSensitive)).ToList();
        return Result.Success(views);
    }

    public async Task<Result<StatutoryIdView>> SetAsync(Guid employeeId, string idType, SetStatutoryIdRequest request, bool revealSensitive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var employee = await access.LoadAsync(employeeId, Permissions.Employee.StatutoryIdManage, tracked: false, cancellationToken);
        if (employee.IsFailure)
        {
            return Result.Failure<StatutoryIdView>(employee.Error);
        }

        var type = StatutoryId.NormaliseType(idType);
        var value = StatutoryId.NormaliseValue(request.Value);
        if (type.IsFailure || value.IsFailure)
        {
            return Result.Failure<StatutoryIdView>(type.IsFailure ? type.Error : value.Error);
        }

        // The country pack of the legal entity the employee works for decides what a valid id looks like.
        var legalEntityId = await db.Jobs.AsNoTracking().Where(j => j.EmployeeId == employeeId).OrderByDescending(j => j.EffectiveFrom)
            .Select(j => (Guid?)j.LegalEntityId).FirstOrDefaultAsync(cancellationToken);
        var entity = legalEntityId is { } id ? await org.GetLegalEntityAsync(id, cancellationToken) : null;
        if (entity is null)
        {
            return Result.Failure<StatutoryIdView>(Error.Conflict("employee.statutory_id.no_legal_entity", "The employee has no legal entity to validate the id against."));
        }

        var problem = await validator.ValidateAsync(entity.CountryPackCode, type.Value, value.Value, cancellationToken);
        if (problem is not null)
        {
            return Result.Failure<StatutoryIdView>(Error.Validation("employee.statutory_id.value_rejected", problem));
        }

        var tenant = tenantContext.Require();
        var cipher = protector.Protect(tenant, employeeId, type.Value, value.Value);
        var existing = await db.StatutoryIds.SingleOrDefaultAsync(s => s.EmployeeId == employeeId && s.IdType == type.Value, cancellationToken);
        if (existing is null)
        {
            existing = StatutoryId.Create(employeeId, type.Value, cipher);
            db.StatutoryIds.Add(existing);
        }
        else
        {
            existing.Replace(cipher);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException or DbUpdateException)
        {
            return Result.Failure<StatutoryIdView>(Error.Conflict("employee.statutory_id.stale_version", "The record was changed by someone else; try again."));
        }

        return Result.Success(protector.View(tenant, existing, revealSensitive));
    }

    public async Task<Result> DeleteAsync(Guid employeeId, string idType, CancellationToken cancellationToken)
    {
        var employee = await access.LoadAsync(employeeId, Permissions.Employee.StatutoryIdManage, tracked: false, cancellationToken);
        if (employee.IsFailure)
        {
            return Result.Failure(employee.Error);
        }

        var type = StatutoryId.NormaliseType(idType);
        if (type.IsFailure)
        {
            return Result.Failure(type.Error);
        }

        var existing = await db.StatutoryIds.SingleOrDefaultAsync(s => s.EmployeeId == employeeId && s.IdType == type.Value, cancellationToken);
        if (existing is null)
        {
            return Result.Failure(Error.NotFound("employee.statutory_id.not_found", "The employee has no id of that type."));
        }

        db.StatutoryIds.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

internal sealed class DocumentService(EmployeeDbContext db, EmployeeAccess access, TimeProvider time) : IDocumentService
{
    private DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    public async Task<Result<IReadOnlyList<DocumentView>>> ListAsync(Guid employeeId, CancellationToken cancellationToken)
    {
        var employee = await access.LoadAsync(employeeId, Permissions.Employee.DocumentView, tracked: false, cancellationToken);
        if (employee.IsFailure)
        {
            return Result.Failure<IReadOnlyList<DocumentView>>(employee.Error);
        }

        var today = Today;
        IReadOnlyList<DocumentView> views = (await db.Documents.AsNoTracking().Where(d => d.EmployeeId == employeeId)
                .OrderBy(d => d.DocumentType).ThenBy(d => d.Title).ToListAsync(cancellationToken))
            .Select(d => Views.Document(d, today)).ToList();
        return Result.Success(views);
    }

    public async Task<Result<DocumentView>> CreateAsync(Guid employeeId, CreateDocumentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var employee = await access.LoadAsync(employeeId, Permissions.Employee.DocumentManage, tracked: false, cancellationToken);
        if (employee.IsFailure)
        {
            return Result.Failure<DocumentView>(employee.Error);
        }

        var document = EmployeeDocument.Create(employeeId, request.DocumentType, request.Title, request.FileKey, request.IssuedOn, request.ExpiresOn);
        if (document.IsFailure)
        {
            return Result.Failure<DocumentView>(document.Error);
        }

        db.Documents.Add(document.Value);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(Views.Document(document.Value, Today));
    }

    public async Task<Result<DocumentView>> UpdateAsync(Guid employeeId, Guid id, UpdateDocumentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var employee = await access.LoadAsync(employeeId, Permissions.Employee.DocumentManage, tracked: false, cancellationToken);
        if (employee.IsFailure)
        {
            return Result.Failure<DocumentView>(employee.Error);
        }

        if (request.RowVersion is not { } version)
        {
            return Result.Failure<DocumentView>(Error.Validation("employee.row_version_required", "The rowVersion the change is based on is required."));
        }

        var document = await db.Documents.SingleOrDefaultAsync(d => d.EmployeeId == employeeId && d.Id == id, cancellationToken);
        if (document is null)
        {
            return Result.Failure<DocumentView>(NotFound());
        }

        if (document.RowVersion != version)
        {
            return Result.Failure<DocumentView>(Stale());
        }

        var applied = document.Update(request.DocumentType, request.Title, request.FileKey, request.IssuedOn, request.ExpiresOn);
        if (applied.IsFailure)
        {
            return Result.Failure<DocumentView>(applied.Error);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<DocumentView>(Stale());
        }

        return Result.Success(Views.Document(document, Today));
    }

    public async Task<Result> DeleteAsync(Guid employeeId, Guid id, CancellationToken cancellationToken)
    {
        var employee = await access.LoadAsync(employeeId, Permissions.Employee.DocumentManage, tracked: false, cancellationToken);
        if (employee.IsFailure)
        {
            return Result.Failure(employee.Error);
        }

        var document = await db.Documents.SingleOrDefaultAsync(d => d.EmployeeId == employeeId && d.Id == id, cancellationToken);
        if (document is null)
        {
            return Result.Failure(NotFound());
        }

        db.Documents.Remove(document);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private static Error NotFound() => Error.NotFound("employee.document.not_found", "The document does not exist.");

    private static Error Stale() => Error.Conflict("employee.document.stale_version", "The record was changed by someone else. Reload it and apply your change again.");
}

internal sealed class DeviceService(EmployeeDbContext db, EmployeeAccess access, TimeProvider time) : IDeviceService
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<Result<IReadOnlyList<DeviceView>>> ListAsync(Guid employeeId, CancellationToken cancellationToken)
    {
        var employee = await access.LoadAsync(employeeId, Permissions.Employee.DeviceView, tracked: false, cancellationToken);
        if (employee.IsFailure)
        {
            return Result.Failure<IReadOnlyList<DeviceView>>(employee.Error);
        }

        IReadOnlyList<DeviceView> views = (await db.Devices.AsNoTracking().Where(d => d.EmployeeId == employeeId).OrderBy(d => d.RegisteredAt).ToListAsync(cancellationToken))
            .Select(Views.Device).ToList();
        return Result.Success(views);
    }

    public async Task<Result<DeviceView>> RegisterAsync(Guid employeeId, RegisterDeviceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var employee = await access.LoadAsync(employeeId, Permissions.Employee.DeviceManage, tracked: false, cancellationToken);
        if (employee.IsFailure)
        {
            return Result.Failure<DeviceView>(employee.Error);
        }

        if (!EnumText.TryParse<DevicePlatform>(request.Platform, out var platform))
        {
            return Result.Failure<DeviceView>(Error.Validation("employee.device.platform_invalid", "The platform is android, ios or web."));
        }

        var device = EmployeeDevice.Register(employeeId, request.DeviceId, platform, request.Name, Now);
        if (device.IsFailure)
        {
            return Result.Failure<DeviceView>(device.Error);
        }

        if (await db.Devices.AsNoTracking().AnyAsync(d => d.DeviceId == device.Value.DeviceId, cancellationToken))
        {
            return Result.Failure<DeviceView>(Taken());
        }

        db.Devices.Add(device.Value);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Result.Failure<DeviceView>(Taken());
        }

        return Result.Success(Views.Device(device.Value));
    }

    public async Task<Result<DeviceView>> RevokeAsync(Guid employeeId, Guid id, CancellationToken cancellationToken)
    {
        var employee = await access.LoadAsync(employeeId, Permissions.Employee.DeviceManage, tracked: false, cancellationToken);
        if (employee.IsFailure)
        {
            return Result.Failure<DeviceView>(employee.Error);
        }

        var device = await db.Devices.SingleOrDefaultAsync(d => d.EmployeeId == employeeId && d.Id == id, cancellationToken);
        if (device is null)
        {
            return Result.Failure<DeviceView>(NotFound());
        }

        var revoked = device.Revoke(Now);
        if (revoked.IsFailure)
        {
            return Result.Failure<DeviceView>(revoked.Error);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(Views.Device(device));
    }

    public async Task<Result> DeleteAsync(Guid employeeId, Guid id, CancellationToken cancellationToken)
    {
        var employee = await access.LoadAsync(employeeId, Permissions.Employee.DeviceManage, tracked: false, cancellationToken);
        if (employee.IsFailure)
        {
            return Result.Failure(employee.Error);
        }

        var device = await db.Devices.SingleOrDefaultAsync(d => d.EmployeeId == employeeId && d.Id == id, cancellationToken);
        if (device is null)
        {
            return Result.Failure(NotFound());
        }

        db.Devices.Remove(device);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private static Error NotFound() => Error.NotFound("employee.device.not_found", "The device does not exist.");

    private static Error Taken() => Error.Conflict("employee.device.device_taken", "That device is already registered.");
}

internal sealed class ConsentService(EmployeeDbContext db, EmployeeAccess access, TimeProvider time) : IConsentService
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<Result<IReadOnlyList<ConsentView>>> ListAsync(Guid employeeId, CancellationToken cancellationToken)
    {
        var employee = await access.LoadAsync(employeeId, Permissions.Employee.ConsentView, tracked: false, cancellationToken);
        if (employee.IsFailure)
        {
            return Result.Failure<IReadOnlyList<ConsentView>>(employee.Error);
        }

        IReadOnlyList<ConsentView> views = (await db.Consents.AsNoTracking().Where(c => c.EmployeeId == employeeId).OrderBy(c => c.GrantedAt).ToListAsync(cancellationToken))
            .Select(Views.Consent).ToList();
        return Result.Success(views);
    }

    public async Task<Result<ConsentView>> GrantAsync(Guid employeeId, GrantConsentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var employee = await access.LoadAsync(employeeId, Permissions.Employee.ConsentManage, tracked: false, cancellationToken);
        if (employee.IsFailure)
        {
            return Result.Failure<ConsentView>(employee.Error);
        }

        var consent = EmployeeConsent.Grant(employeeId, request.ConsentType, request.Version, request.Source, Now);
        if (consent.IsFailure)
        {
            return Result.Failure<ConsentView>(consent.Error);
        }

        db.Consents.Add(consent.Value);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(Views.Consent(consent.Value));
    }

    public async Task<Result<ConsentView>> WithdrawAsync(Guid employeeId, Guid id, CancellationToken cancellationToken)
    {
        var employee = await access.LoadAsync(employeeId, Permissions.Employee.ConsentManage, tracked: false, cancellationToken);
        if (employee.IsFailure)
        {
            return Result.Failure<ConsentView>(employee.Error);
        }

        var consent = await db.Consents.SingleOrDefaultAsync(c => c.EmployeeId == employeeId && c.Id == id, cancellationToken);
        if (consent is null)
        {
            return Result.Failure<ConsentView>(Error.NotFound("employee.consent.not_found", "The consent does not exist."));
        }

        var withdrawn = consent.Withdraw(Now);
        if (withdrawn.IsFailure)
        {
            return Result.Failure<ConsentView>(withdrawn.Error);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(Views.Consent(consent));
    }
}
