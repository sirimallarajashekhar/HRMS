using Hrms.SharedKernel;

namespace Hrms.Core.Application.Org;

/// <summary>A null <paramref name="EmployeeId"/> leaves the unit without a head from the date.</summary>
public sealed record ChangeOrgUnitHeadRequest(Guid? EmployeeId, DateOnly? EffectiveFrom, long? RowVersion);

public sealed record OrgUnitHeadView(Guid EmployeeId, DateOnly EffectiveFrom, DateOnly? EffectiveTo);

/// <summary><paramref name="RowVersion"/> is the unit's, for the next change.</summary>
public sealed record OrgUnitHeadHistoryView(IReadOnlyList<OrgUnitHeadView> Periods, long RowVersion);

public interface IOrgUnitHeadService
{
    Task<Result<OrgUnitHeadHistoryView>> HistoryAsync(Guid orgUnitId, CancellationToken cancellationToken);

    Task<Result<OrgUnitHeadHistoryView>> ChangeAsync(Guid orgUnitId, ChangeOrgUnitHeadRequest request, CancellationToken cancellationToken);
}
