using FluentAssertions;
using Hrms.Identity.Domain.Users;
using Hrms.SharedKernel;

namespace Hrms.Identity.UnitTests;

public class MfaDomainTests
{
    private static readonly DateTime Now = new(2026, 11, 26, 9, 0, 0, DateTimeKind.Utc);

    private static UserAccount NewUser() => UserAccount.Create("a@example.com", "Asha", "hash", Now).Value;

    [Fact]
    public void NewAccount_HasNoMfaAndNoPendingEnrolment()
    {
        var user = NewUser();

        user.MfaEnabled.Should().BeFalse();
        user.HasPendingMfaEnrollment.Should().BeFalse();
        user.MfaSecretProtected.Should().BeNull();
    }

    [Fact]
    public void BeginEnrollment_StoresAPendingSecret_AndAnotherBeginReplacesIt()
    {
        var user = NewUser();

        user.BeginMfaEnrollment("v1.k.first").IsSuccess.Should().BeTrue();
        user.BeginMfaEnrollment("v1.k.second").IsSuccess.Should().BeTrue();

        user.HasPendingMfaEnrollment.Should().BeTrue();
        user.MfaSecretProtected.Should().Be("v1.k.second");
        user.MfaEnabled.Should().BeFalse("nothing is on until the code is confirmed");
    }

    [Fact]
    public void ConfirmEnrollment_TurnsMfaOnRecordsTheStepAndRotatesTheSecurityStamp()
    {
        var user = NewUser();
        var stamp = user.SecurityStamp;
        user.BeginMfaEnrollment("v1.k.secret");

        var result = user.ConfirmMfaEnrollment(acceptedStep: 100, Now);

        result.IsSuccess.Should().BeTrue();
        user.MfaEnabled.Should().BeTrue();
        user.HasPendingMfaEnrollment.Should().BeFalse();
        user.MfaEnrolledAt.Should().Be(Now);
        user.MfaLastUsedStep.Should().Be(100);
        user.SecurityStamp.Should().NotBe(stamp, "sessions that never passed MFA must end");
    }

    [Fact]
    public void ConfirmEnrollment_WithoutAPendingSecret_IsAConflict()
    {
        var result = NewUser().ConfirmMfaEnrollment(1, Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("identity.mfa.no_pending_enrollment");
        result.Error.Type.Should().Be(ErrorType.Conflict);
    }

    [Fact]
    public void BeginEnrollment_AfterMfaIsOn_IsAConflictAndKeepsTheActiveSecret()
    {
        var user = NewUser();
        user.BeginMfaEnrollment("v1.k.active");
        user.ConfirmMfaEnrollment(1, Now);

        var result = user.BeginMfaEnrollment("v1.k.other");

        result.Error.Code.Should().Be("identity.mfa.already_enabled");
        user.MfaSecretProtected.Should().Be("v1.k.active");
    }

    [Fact]
    public void Disable_ForgetsTheSecretAndStepAndRotatesTheStamp()
    {
        var user = NewUser();
        user.BeginMfaEnrollment("v1.k.secret");
        user.ConfirmMfaEnrollment(5, Now);
        var stamp = user.SecurityStamp;

        user.DisableMfa();

        user.MfaEnabled.Should().BeFalse();
        user.MfaSecretProtected.Should().BeNull();
        user.MfaLastUsedStep.Should().BeNull();
        user.MfaEnrolledAt.Should().BeNull();
        user.SecurityStamp.Should().NotBe(stamp);
    }

    [Theory]
    [InlineData(null, 10, true)]
    [InlineData(10L, 11, true)]
    [InlineData(10L, 10, false)]
    [InlineData(10L, 9, false)]
    public void TryAcceptMfaStep_AcceptsOnlyAStepNewerThanTheLastOne(long? last, long step, bool accepted)
    {
        var user = NewUser();
        if (last is { } value)
        {
            user.BeginMfaEnrollment("v1.k.secret");
            user.ConfirmMfaEnrollment(value, Now);
        }

        user.TryAcceptMfaStep(step).Should().Be(accepted);
        user.MfaLastUsedStep.Should().Be(accepted ? step : last);
    }

    [Fact]
    public void RecoveryCode_CanBeUsedOnceAndRecordsWhen()
    {
        var code = RecoveryCode.Create(Guid.CreateVersion7(), "ABCDEF");

        code.IsUsed.Should().BeFalse();
        code.TryUse(Now).Should().BeTrue();
        code.IsUsed.Should().BeTrue();
        code.UsedAt.Should().Be(Now);
        code.TryUse(Now.AddMinutes(1)).Should().BeFalse("a used code never works again");
        code.UsedAt.Should().Be(Now);
    }

    [Fact]
    public void RecoveryCode_RequiresAUserAndAHash()
    {
        FluentActions.Invoking(() => RecoveryCode.Create(Guid.Empty, "ABCDEF")).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => RecoveryCode.Create(Guid.CreateVersion7(), " ")).Should().Throw<ArgumentException>();
    }
}
