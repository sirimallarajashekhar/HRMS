using Hrms.SharedKernel;

namespace Hrms.Core.Domain.Org;

/// <summary>
/// A level of the organisation tree the tenant defines (Division, Department, Team...). A unit's parent must sit on a lower level
/// number, so the levels fix the shape of the tree. The level of a type that has units cannot change (the service refuses it).
/// </summary>
public sealed class OrgUnitType : CodedMaster
{
    public const string Master = "org_unit_type";
    public const int MaxLevel = 20;

    private OrgUnitType(string code, string name, int level)
        : base(code, name) => Level = level;

    /// <summary>For the persistence layer only.</summary>
    private OrgUnitType()
    {
    }

    /// <summary>1 is the top of the tree.</summary>
    public int Level { get; private set; }

    public static Result<OrgUnitType> Create(string? code, string? name, int level)
    {
        var identity = ValidateIdentity(Master, code, name);
        if (identity.IsFailure)
        {
            return Result.Failure<OrgUnitType>(identity.Error);
        }

        return ValidLevel(level)
            ? Result.Success(new OrgUnitType(identity.Value.Code, identity.Value.Name, level))
            : Result.Failure<OrgUnitType>(LevelInvalid());
    }

    public Result Update(string? name, int level, bool isActive)
    {
        if (!ValidLevel(level))
        {
            return Result.Failure(LevelInvalid());
        }

        var applied = ApplyIdentity(Master, name, isActive);
        if (applied.IsFailure)
        {
            return applied;
        }

        Level = level;
        return Result.Success();
    }

    private static bool ValidLevel(int level) => level is >= 1 and <= MaxLevel;

    private static Error LevelInvalid() => Error.Validation("core.org_unit_type.level_invalid", $"The level is a number from 1 to {MaxLevel}.");
}
