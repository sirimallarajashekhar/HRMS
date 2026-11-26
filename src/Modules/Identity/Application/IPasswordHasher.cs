namespace Hrms.Identity.Application;

public enum PasswordCheck
{
    Failed,
    Success,

    /// <summary>The password is right but the stored hash uses older settings; store a fresh hash after sign-in.</summary>
    SuccessRehashNeeded,
}

/// <summary>Hashes and checks passwords. The infrastructure implementation is the only place that knows the algorithm.</summary>
public interface IPasswordHasher
{
    string Hash(string password);

    PasswordCheck Verify(string passwordHash, string password);

    /// <summary>
    /// A valid hash of a password nobody knows, made with the current settings. Sign-in verifies against it when the tenant or
    /// the account does not exist, so an unknown account takes as long to refuse as a wrong password and timing cannot be used
    /// to find out which accounts exist.
    /// </summary>
    string DummyHash { get; }
}
