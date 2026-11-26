using Hrms.Contracts;
using Hrms.SharedKernel;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Hrms.Http.Tenancy;

/// <summary>Marks an endpoint that works without a tenant (health probes, API description, the ping).</summary>
public sealed class AllowWithoutTenantMetadata
{
    public static AllowWithoutTenantMetadata Instance { get; } = new();

    private AllowWithoutTenantMetadata()
    {
    }
}

public static class TenancyExtensions
{
    public static TBuilder AllowWithoutTenant<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(AllowWithoutTenantMetadata.Instance);

    /// <summary>
    /// Registers the per-request tenant context, the resolver chosen by configuration and a deny-all tenant directory.
    /// Register the real <see cref="ITenantDirectory"/> after this call so it replaces the default.
    /// </summary>
    public static IServiceCollection AddHrmsTenancy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<TenancyOptions>().BindConfiguration(TenancyOptions.SectionName).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TenancyOptions>, TenancyOptionsValidator>());

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.TryAddSingleton<IActorContext, SystemActorContext>();

        services.AddSingleton<ITenantResolver>(sp => sp.GetRequiredService<IOptions<TenancyOptions>>().Value.HeaderResolver.Enabled
            ? new HeaderTenantResolver()
            : new NoTenantResolver());
        services.TryAddScoped<ITenantDirectory, DenyAllTenantDirectory>();

        return services;
    }

    /// <summary>Add after UseRouting and before the endpoints.</summary>
    public static IApplicationBuilder UseHrmsTenancy(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<TenantMiddleware>();
    }
}
