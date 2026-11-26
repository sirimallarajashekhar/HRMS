namespace Hrms.Api.Bootstrap;

/// <summary>
/// <c>dotnet run --project src/Host/Hrms.Api -- bootstrap-tenant --code ACME --name "Acme Ltd" --admin-email admin@acme.example --admin-name "Acme Admin"</c>
/// with the administrator password in the HRMS_BOOTSTRAP_ADMIN_PASSWORD environment variable. A password on a command line is visible to
/// every user of the machine and ends up in shell history, so it is only ever read from the environment and never printed.
/// Exit codes: 0 done, 2 bad arguments or password, 3 the tenant or administrator could not be created.
/// </summary>
public static class BootstrapCommand
{
    public const string Name = "bootstrap-tenant";
    public const string PasswordVariable = "HRMS_BOOTSTRAP_ADMIN_PASSWORD";

    public static bool IsRequested(string[] args) => args is { Length: > 0 } && args[0] == Name;

    public static bool TryParse(string[] args, out BootstrapRequest request, out string error)
    {
        request = new BootstrapRequest(string.Empty, string.Empty, string.Empty, string.Empty);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 1; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Expected '--option value' pairs; problem near '{args[i]}'.";
                return false;
            }

            values[args[i][2..]] = args[i + 1];
        }

        string[] required = ["code", "name", "admin-email", "admin-name"];
        var missing = required.Where(key => !values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)).ToArray();
        var unknown = values.Keys.Except(required, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0 || unknown.Length > 0)
        {
            error = missing.Length > 0
                ? $"Missing: {string.Join(", ", missing.Select(m => "--" + m))}."
                : $"Unknown option: {string.Join(", ", unknown.Select(u => "--" + u))}.";
            return false;
        }

        request = new BootstrapRequest(values["code"], values["name"], values["admin-email"], values["admin-name"]);
        error = string.Empty;
        return true;
    }

    public static async Task<int> RunAsync(
        IServiceProvider services, string[] args, TextWriter output, Func<string, string?> environment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(environment);

        if (!TryParse(args, out var request, out var error))
        {
            await output.WriteLineAsync(error);
            return 2;
        }

        var password = environment(PasswordVariable);
        if (string.IsNullOrEmpty(password))
        {
            await output.WriteLineAsync($"Set the administrator password in the {PasswordVariable} environment variable.");
            return 2;
        }

        var result = await TenantBootstrapper.RunAsync(services, request, password, cancellationToken);
        if (result.IsFailure)
        {
            await output.WriteLineAsync($"Bootstrap failed: {result.Error.Code}: {result.Error.Message}");
            return result.Error.Type == Hrms.SharedKernel.ErrorType.Validation ? 2 : 3;
        }

        var outcome = result.Value;
        await output.WriteLineAsync($"Tenant {request.TenantCode.Trim().ToUpperInvariant()} {(outcome.TenantCreated ? "created" : "already existed")}: {outcome.TenantId}");
        await output.WriteLineAsync(outcome.AdminCreated
            ? $"Administrator {request.AdminEmail.Trim().ToLowerInvariant()} created. They must change the password and enrol an authenticator at first sign-in."
            : "Administrator already existed; nothing changed.");
        return 0;
    }
}
