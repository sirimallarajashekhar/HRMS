// Deliberate violations used to prove ModuleBoundaryRule can fail (a rule that never fails is worthless).
// Namespaces mimic two modules: FixtureB's Application layer reaches into FixtureA's Domain and Infrastructure.
namespace Hrms.FixtureA.Domain
{
    public sealed class SecretEntity;
}

namespace Hrms.FixtureA.Infrastructure
{
    public sealed class SecretRepository;
}

namespace Hrms.FixtureB.Application
{
    public sealed class BadDomainHandler
    {
        public static object Leak() => new Hrms.FixtureA.Domain.SecretEntity();
    }

    public sealed class BadInfrastructureHandler
    {
        public static object Leak() => new Hrms.FixtureA.Infrastructure.SecretRepository();
    }
}
