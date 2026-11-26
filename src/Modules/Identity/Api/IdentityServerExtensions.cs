using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Hrms.Identity.Api;

public static class IdentityServerExtensions
{
    public const string TokenRateLimitPolicy = "token";
    public const string AccountRateLimitPolicy = "account";
    public const string TokenEndpointPath = "/connect/token";

    /// <summary>
    /// Adds the OpenIddict token server (password, refresh-token and client-credentials grants) and validation of its tokens in this
    /// process. Entry validation is enabled for BOTH authorizations and tokens: enabling only the first is the configuration the
    /// OpenIddict advisory GHSA-cxgj-hwwh-855g (fixed after 7.1) breaks, and revocation needs both anyway. Client assertions are never
    /// enabled (GHSA-925x-4h4v-2792). Outside Development the signing and encryption certificates must be configured and valid.
    /// </summary>
    public static IServiceCollection AddIdentityServer(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var options = configuration.GetSection(IdentityServerOptions.SectionName).Get<IdentityServerOptions>() ?? new IdentityServerOptions();
        options.Validate();
        services.AddSingleton(options);

        services.AddOpenIddict()
            .AddServer(server =>
            {
                server.SetTokenEndpointUris(TokenEndpointPath);
                server.AllowPasswordFlow().AllowRefreshTokenFlow().AllowClientCredentialsFlow();
                server.RegisterScopes(Scopes.OfflineAccess);

                server.SetAccessTokenLifetime(TimeSpan.FromMinutes(options.AccessTokenMinutes));
                server.SetRefreshTokenLifetime(TimeSpan.FromDays(options.RefreshTokenDays));
                server.SetRefreshTokenReuseLeeway(TimeSpan.FromSeconds(options.RefreshReuseLeewaySeconds));
                server.DisableSlidingRefreshTokenExpiration();

                if (environment.IsDevelopment())
                {
                    server.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
                }
                else
                {
                    server.AddEncryptionCertificate(Load(options.EncryptionCertificate, "EncryptionCertificate"))
                        .AddSigningCertificate(Load(options.SigningCertificate, "SigningCertificate"));
                }

                var aspNet = server.UseAspNetCore().EnableTokenEndpointPassthrough();
                if (environment.IsDevelopment())
                {
                    aspNet.DisableTransportSecurityRequirement();
                }
            })
            .AddValidation(validation =>
            {
                validation.UseLocalServer();
                validation.UseAspNetCore();
                validation.EnableAuthorizationEntryValidation();
                validation.EnableTokenEntryValidation();
            });

        services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        services.AddOpenIddict().AddServer(server => server.RegisterScopes(TokenEndpoint.MfaSetupScope));

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(TokenRateLimitPolicy, http => RateLimitPartition.GetSlidingWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = options.TokenRequestsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                    QueueLimit = 0,
                }));

            // Per signed-in user (their subject), falling back to the address, so one user cannot grind through codes or passwords.
            limiter.AddPolicy(AccountRateLimitPolicy, http => RateLimitPartition.GetSlidingWindowLimiter(
                http.User.FindFirst(Claims.Subject)?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = options.AccountRequestsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                    QueueLimit = 0,
                }));
        });

        return services;
    }

    private static X509Certificate2 Load(CertificateOptions certificate, string name)
    {
        if (string.IsNullOrWhiteSpace(certificate.Path))
        {
            throw new InvalidOperationException(
                $"{IdentityServerOptions.SectionName}:{name}:Path must be set outside Development: the token server needs real signing and encryption keys.");
        }

        if (!File.Exists(certificate.Path))
        {
            throw new InvalidOperationException($"{IdentityServerOptions.SectionName}:{name}:Path points to a file that does not exist.");
        }

        var loaded = X509CertificateLoader.LoadPkcs12FromFile(certificate.Path, certificate.Password);
        if (!loaded.HasPrivateKey)
        {
            throw new InvalidOperationException($"{IdentityServerOptions.SectionName}:{name} has no private key.");
        }

        if (loaded.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
        {
            throw new InvalidOperationException($"{IdentityServerOptions.SectionName}:{name} has expired.");
        }

        return loaded;
    }
}
