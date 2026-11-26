using Hrms.SharedKernel;

namespace Hrms.Core.Domain.Org;

/// <summary>A pay or seniority grade. Rank orders grades (higher is more senior). Pay bands are payroll's business and are not here.</summary>
public sealed class Grade : CodedMaster
{
    public const string Master = "grade";
    public const int MaxRank = 10_000;

    private Grade(string code, string name, int rank)
        : base(code, name) => Rank = rank;

    /// <summary>For the persistence layer only.</summary>
    private Grade()
    {
    }

    public int Rank { get; private set; }

    public static Result<Grade> Create(string? code, string? name, int rank)
    {
        var identity = ValidateIdentity(Master, code, name);
        if (identity.IsFailure)
        {
            return Result.Failure<Grade>(identity.Error);
        }

        return ValidRank(rank)
            ? Result.Success(new Grade(identity.Value.Code, identity.Value.Name, rank))
            : Result.Failure<Grade>(RankInvalid());
    }

    public Result Update(string? name, int rank, bool isActive)
    {
        if (!ValidRank(rank))
        {
            return Result.Failure(RankInvalid());
        }

        var applied = ApplyIdentity(Master, name, isActive);
        if (applied.IsFailure)
        {
            return applied;
        }

        Rank = rank;
        return Result.Success();
    }

    private static bool ValidRank(int rank) => rank is >= 0 and <= MaxRank;

    private static Error RankInvalid() => Error.Validation("core.grade.rank_invalid", $"The rank is a number from 0 to {MaxRank}.");
}
