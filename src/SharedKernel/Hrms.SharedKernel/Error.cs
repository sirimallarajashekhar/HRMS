namespace Hrms.SharedKernel;

/// <summary>A stable, machine-readable business failure (maps to a problem-details code).</summary>
public sealed record Error(string Code, string Message)
{
    public static readonly Error None = new(string.Empty, string.Empty);
}
