using FluentAssertions;
using Hrms.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hrms.Architecture.Tests;

public class WorkerStartupTests
{
    /// <summary>
    /// The integration tests run background jobs inside a host that registers everything, so they cannot tell whether the real worker
    /// can be built. This builds the worker's own registrations with the checks Development applies at startup. Nothing connects.
    /// </summary>
    [Fact]
    public void TheWorkersServices_CanAllBeConstructed()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));
        builder.Services.AddHrmsWorker("Server=127.0.0.1;Database=unused;User ID=x;Password=x");

        var build = () =>
        {
            using var host = builder.Build();
        };

        build.Should().NotThrow();
    }
}
