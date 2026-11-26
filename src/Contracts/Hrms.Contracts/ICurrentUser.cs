namespace Hrms.Contracts;

/// <summary>The signed-in user of the current request, for modules that need to know who is asking (data scopes, ownership checks).</summary>
public interface ICurrentUser
{
    /// <summary>The user account id from the token subject, or null for an anonymous caller or a token that is not a user (an integration client).</summary>
    Guid? UserId { get; }
}
