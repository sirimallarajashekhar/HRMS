using Hrms.Contracts;

namespace Hrms.Workflow.Infrastructure;

/// <summary>
/// Definitions are tenant-wide configuration. A delegation is about a person; to keep its history simple and safe, only a
/// tenant-wide delegation administrator sees it.
/// </summary>
internal sealed class WorkflowAuditAccess(IDataScopeService scopes, ICurrentUser user) : IAuditRecordAccess
{
    private const string DefinitionType = "workflow_def";
    private const string DelegationType = "delegation";

    public IReadOnlyList<AuditRecordType> RecordTypes { get; } =
    [
        new(DefinitionType,
        [
            new(DefinitionType, Permissions.Workflow.DefinitionView),
            new("workflow_stage_def", Permissions.Workflow.DefinitionView),
        ]),
        new(DelegationType, [new(DelegationType, Permissions.Workflow.DelegationManage)]),
    ];

    public async Task<bool> IsInScopeAsync(string rootType, Guid recordId, string permission, CancellationToken cancellationToken) =>
        rootType switch
        {
            DefinitionType => true,
            DelegationType => user.UserId is { } userId
                              && (await scopes.ResolveAsync(userId, permission, cancellationToken)).IsTenantWide,
            _ => false,
        };
}
