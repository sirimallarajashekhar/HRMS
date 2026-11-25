// Deliberate violation used to prove ModuleBoundaryRule can fail (a rule that never fails is worthless).
// Namespaces mimic two modules: FixtureA owns a Domain type, FixtureB's Application layer reaches into it.
namespace Hrms.FixtureA.Domain
{
    public sealed class SecretEntity;
}

namespace Hrms.FixtureB.Application
{
    public sealed class BadHandler
    {
        public static object Leak() => new Hrms.FixtureA.Domain.SecretEntity();
    }
}
