using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hrms.Http.Tenancy;

public sealed class TenancyOptions
{
    public const string SectionName = "Tenancy";

    public HeaderResolverOptions HeaderResolver { get; init; } = new();
}

public sealed class HeaderResolverOptions
{
    /// <summary>Trust the X-Tenant-Id header. Development and tests only.</summary>
    public bool Enabled { get; init; }
}

/// <summary>Refuses to start the host when the unauthenticated header resolver is enabled anywhere but Development.</summary>
public sealed class TenancyOptionsValidator(IHostEnvironment environment) : IValidateOptions<TenancyOptions>
{
    public ValidateOptionsResult Validate(string? name, TenancyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.HeaderResolver.Enabled && !environment.IsDevelopment()
            ? ValidateOptionsResult.Fail(
                $"{TenancyOptions.SectionName}:HeaderResolver:Enabled trusts the caller's X-Tenant-Id and is allowed only in the Development environment.")
            : ValidateOptionsResult.Success;
    }
}
