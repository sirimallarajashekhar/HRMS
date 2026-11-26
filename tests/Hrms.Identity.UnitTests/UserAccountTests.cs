using FluentAssertions;
using Hrms.Identity.Domain.Security;
using Hrms.Identity.Domain.Users;

namespace Hrms.Identity.UnitTests;

public class UserAccountTests
{
    private static readonly DateTime Now = new(2026, 11, 26, 9, 0, 0, DateTimeKind.Utc);
    private static readonly LockoutPolicy ThreeStrikes = new(MaxFailedAttempts: 3, Duration: TimeSpan.FromMinutes(10));

    private static UserAccount NewUser() => UserAccount.Create("Asha.Rao@Example.com", "Asha Rao", "hash-1", Now).Value;

    [Theory]
    [InlineData("Asha.Rao@Example.com", "asha.rao@example.com")]
    [InlineData("  USER@EXAMPLE.ORG  ", "user@example.org")]
    public void Create_NormalisesTheEmail(string input, string expected) =>
        UserAccount.Create(input, "Name", "hash", Now).Value.Email.Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-at-sign")]
    [InlineData("two@@example.com")]
    [InlineData("space in@example.com")]
    [InlineData("missing@tld")]
    public void Create_WithAnInvalidEmail_FailsWithAStableCode(string? email)
    {
        var result = UserAccount.Create(email, "Name", "hash", Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("identity.user.email_invalid");
    }

    [Fact]
    public void Create_WithAnEmailOver254Characters_Fails() =>
        UserAccount.Create(new string('a', 250) + "@example.com", "Name", "hash", Now).Error.Code.Should().Be("identity.user.email_invalid");

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Create_WithoutADisplayName_Fails(string? name) =>
        UserAccount.Create("a@example.com", name, "hash", Now).Error.Code.Should().Be("identity.user.display_name_invalid");

    [Fact]
    public void Create_WithANonUtcTime_Throws() =>
        FluentActions.Invoking(() => UserAccount.Create("a@example.com", "Name", "hash", DateTime.Now))
            .Should().Throw<ArgumentException>();

    [Fact]
    public void Create_StartsActiveUnlockedWithAStamp()
    {
        var user = NewUser();

        user.Status.Should().Be(UserStatus.Active);
        user.FailedAccessCount.Should().Be(0);
        user.LockoutUntil.Should().BeNull();
        user.SecurityStamp.Should().NotBeNullOrEmpty();
        user.CanSignIn(Now).Should().BeTrue();
    }

    [Fact]
    public void RegisterFailedAttempt_BelowTheLimit_CountsWithoutLocking()
    {
        var user = NewUser();

        var first = user.RegisterFailedAttempt(Now, ThreeStrikes);
        var second = user.RegisterFailedAttempt(Now, ThreeStrikes);

        first.LockedNow.Should().BeFalse();
        second.LockedNow.Should().BeFalse();
        user.FailedAccessCount.Should().Be(2);
        user.IsLockedOut(Now).Should().BeFalse();
    }

    [Fact]
    public void RegisterFailedAttempt_AtTheLimit_LocksForTheConfiguredDuration()
    {
        var user = NewUser();
        user.RegisterFailedAttempt(Now, ThreeStrikes);
        user.RegisterFailedAttempt(Now, ThreeStrikes);

        var third = user.RegisterFailedAttempt(Now, ThreeStrikes);

        third.LockedNow.Should().BeTrue();
        third.LockoutUntil.Should().Be(Now.AddMinutes(10));
        user.LockoutUntil.Should().Be(Now.AddMinutes(10));
        user.FailedAccessCount.Should().Be(0);
        user.IsLockedOut(Now.AddMinutes(9)).Should().BeTrue();
        user.CanSignIn(Now.AddMinutes(9)).Should().BeFalse();
    }

    [Fact]
    public void RegisterFailedAttempt_WhileLockedOut_IsIgnoredAndCannotExtendTheLock()
    {
        var user = NewUser();
        for (var i = 0; i < 3; i++)
        {
            user.RegisterFailedAttempt(Now, ThreeStrikes);
        }

        var duringLock = user.RegisterFailedAttempt(Now.AddMinutes(5), ThreeStrikes);

        duringLock.LockedNow.Should().BeFalse();
        user.LockoutUntil.Should().Be(Now.AddMinutes(10), "a failed attempt during the lock must not push the end later");
        user.FailedAccessCount.Should().Be(0);
    }

    [Fact]
    public void RegisterFailedAttempt_AfterTheLockExpires_StartsAFreshCount()
    {
        var user = NewUser();
        for (var i = 0; i < 3; i++)
        {
            user.RegisterFailedAttempt(Now, ThreeStrikes);
        }

        var later = Now.AddMinutes(11);
        var outcome = user.RegisterFailedAttempt(later, ThreeStrikes);

        outcome.LockedNow.Should().BeFalse();
        user.LockoutUntil.Should().BeNull();
        user.FailedAccessCount.Should().Be(1);
        user.CanSignIn(later).Should().BeTrue();
    }

    [Fact]
    public void RegisterSuccessfulSignIn_ClearsTheCountAndTheLockAndRecordsTheTime()
    {
        var user = NewUser();
        user.RegisterFailedAttempt(Now, ThreeStrikes);
        user.RegisterFailedAttempt(Now, ThreeStrikes);

        user.RegisterSuccessfulSignIn(Now.AddMinutes(1));

        user.FailedAccessCount.Should().Be(0);
        user.LastLoginAt.Should().Be(Now.AddMinutes(1));
    }

    [Fact]
    public void ChangePassword_ReplacesTheHashRotatesTheStampAndClearsLockoutState()
    {
        var user = NewUser();
        var oldStamp = user.SecurityStamp;
        for (var i = 0; i < 3; i++)
        {
            user.RegisterFailedAttempt(Now, ThreeStrikes);
        }

        user.ChangePassword("hash-2", Now.AddHours(1));

        user.PasswordHash.Should().Be("hash-2");
        user.SecurityStamp.Should().NotBe(oldStamp);
        user.PasswordChangedAt.Should().Be(Now.AddHours(1));
        user.LockoutUntil.Should().BeNull();
        user.FailedAccessCount.Should().Be(0);
        user.MustChangePassword.Should().BeFalse();
    }

    [Fact]
    public void Create_WithMustChangePassword_FlagsItUntilThePasswordIsChanged()
    {
        var user = UserAccount.Create("a@example.com", "Name", "hash", Now, mustChangePassword: true).Value;

        user.MustChangePassword.Should().BeTrue();
        user.ChangePassword("hash-2", Now);
        user.MustChangePassword.Should().BeFalse();
    }

    [Fact]
    public void Disable_BlocksSignInAndRotatesTheStampSoIssuedTokensStopWorking()
    {
        var user = NewUser();
        var oldStamp = user.SecurityStamp;

        user.Disable();

        user.CanSignIn(Now).Should().BeFalse();
        user.SecurityStamp.Should().NotBe(oldStamp);
        user.Enable();
        user.CanSignIn(Now).Should().BeTrue();
    }

    [Theory]
    [InlineData(UserStatus.Active, false, true)]
    [InlineData(UserStatus.Active, true, false)]
    [InlineData(UserStatus.Disabled, false, false)]
    [InlineData(UserStatus.Disabled, true, false)]
    public void CanSignIn_RequiresActiveAndNotLockedOut(UserStatus status, bool locked, bool expected)
    {
        var user = NewUser();
        if (status == UserStatus.Disabled)
        {
            user.Disable();
        }

        if (locked)
        {
            for (var i = 0; i < 3; i++)
            {
                user.RegisterFailedAttempt(Now, ThreeStrikes);
            }
        }

        user.CanSignIn(Now).Should().Be(expected);
    }
}
