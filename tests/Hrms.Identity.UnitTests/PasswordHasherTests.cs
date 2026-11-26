using FluentAssertions;
using Hrms.Identity.Application;
using Hrms.Identity.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Hrms.Identity.UnitTests;

public class PasswordHasherTests
{
    private static IdentityPasswordHasher Hasher(int iterations = PasswordHashingOptions.MinimumIterationCount) =>
        new(Options.Create(new PasswordHashingOptions { IterationCount = iterations }));

    [Fact]
    public void Hash_ThenVerify_WithTheSamePassword_Succeeds()
    {
        var hasher = Hasher();

        var hash = hasher.Hash("correct horse battery staple");

        hasher.Verify(hash, "correct horse battery staple").Should().Be(PasswordCheck.Success);
    }

    [Theory]
    [InlineData("wrong password")]
    [InlineData("Correct horse battery staple")]
    [InlineData("correct horse battery staple ")]
    [InlineData("")]
    public void Verify_WithAnotherPassword_Fails(string attempt)
    {
        var hasher = Hasher();
        var hash = hasher.Hash("correct horse battery staple");

        hasher.Verify(hash, attempt).Should().Be(PasswordCheck.Failed);
    }

    [Fact]
    public void Hash_OfTheSamePasswordTwice_DiffersBecauseEachHashHasItsOwnSalt()
    {
        var hasher = Hasher();

        hasher.Hash("same password here").Should().NotBe(hasher.Hash("same password here"));
    }

    [Fact]
    public void Hash_DoesNotContainThePassword() =>
        Hasher().Hash("plain-text-secret-123").Should().NotContain("plain-text-secret-123");

    [Fact]
    public void Verify_WhenTheHashWasMadeWithFewerIterations_SucceedsAndAsksForARehash()
    {
        var old = Hasher(100_000).Hash("upgrade me please");

        Hasher(150_000).Verify(old, "upgrade me please").Should().Be(PasswordCheck.SuccessRehashNeeded);
        Hasher(150_000).Verify(old, "not it").Should().Be(PasswordCheck.Failed);
    }

    [Theory]
    [InlineData("not-a-hash")]
    [InlineData("AQAAAAEAA")]
    [InlineData("!!!!")]
    public void Verify_WithAMalformedStoredHash_FailsInsteadOfThrowing(string garbage) =>
        Hasher().Verify(garbage, "anything").Should().Be(PasswordCheck.Failed);

    [Fact]
    public void Hash_WithAnEmptyPassword_Throws() =>
        FluentActions.Invoking(() => Hasher().Hash(string.Empty)).Should().Throw<ArgumentException>();

    [Theory]
    [InlineData(99_999, false)]
    [InlineData(100_000, true)]
    [InlineData(210_000, true)]
    public void OptionsValidator_RequiresAtLeastTheMinimumIterationCount(int iterations, bool valid)
    {
        var result = new PasswordHashingOptionsValidator().Validate(null, new PasswordHashingOptions { IterationCount = iterations });

        result.Succeeded.Should().Be(valid);
    }

    [Fact]
    public void DefaultOptions_UseTheOwaspIterationCountForPbkdf2Sha512() =>
        new PasswordHashingOptions().IterationCount.Should().Be(210_000);
}
