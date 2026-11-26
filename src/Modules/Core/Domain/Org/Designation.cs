using Hrms.SharedKernel;

namespace Hrms.Core.Domain.Org;

/// <summary>A job title, optionally tied to the grade it normally sits in. The grade link is a hint for the UI, not a constraint on employees.</summary>
public sealed class Designation : CodedMaster
{
    public const string Master = "designation";

    private Designation(string code, string name, Guid? gradeId)
        : base(code, name) => GradeId = gradeId;

    /// <summary>For the persistence layer only.</summary>
    private Designation()
    {
    }

    public Guid? GradeId { get; private set; }

    public static Result<Designation> Create(string? code, string? name, Guid? gradeId)
    {
        var identity = ValidateIdentity(Master, code, name);
        return identity.IsFailure
            ? Result.Failure<Designation>(identity.Error)
            : Result.Success(new Designation(identity.Value.Code, identity.Value.Name, gradeId));
    }

    public Result Update(string? name, Guid? gradeId, bool isActive)
    {
        var applied = ApplyIdentity(Master, name, isActive);
        if (applied.IsSuccess)
        {
            GradeId = gradeId;
        }

        return applied;
    }
}
