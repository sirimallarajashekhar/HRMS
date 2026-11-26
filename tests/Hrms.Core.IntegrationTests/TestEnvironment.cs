using System.Runtime.CompilerServices;
using Hrms.Core.IntegrationTests.Identity;

namespace Hrms.Core.IntegrationTests;

/// <summary>
/// The real API host refuses to start without a field-encryption key, and the repository commits none. Tests that start it
/// (WebApplicationFactory) get a fixed test-only key from environment variables, set once before any test runs.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Initialise()
    {
        foreach (var (name, value) in TestEncryption.Settings())
        {
            Environment.SetEnvironmentVariable(name.Replace(":", "__", StringComparison.Ordinal), value);
        }
    }
}
