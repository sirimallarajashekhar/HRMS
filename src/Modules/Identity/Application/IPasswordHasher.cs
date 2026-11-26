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
}
