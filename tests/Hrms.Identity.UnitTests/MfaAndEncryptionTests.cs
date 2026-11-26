using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Hrms.Identity.Domain.Mfa;
using Hrms.Persistence.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hrms.Identity.UnitTests;

public class MfaAndEncryptionTests
{
    // RFC 6238 appendix B: the secret is the ASCII string "12345678901234567890" and the published codes are 8 digits; a 6-digit code
    // is the last six digits of the same value.
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Compute_MatchesTheRfc6238TestVectors(long unixSeconds, string expected) =>
        Totp.Compute(RfcSecret, unixSeconds / Totp.PeriodSeconds).Should().Be(expected);

    [Fact]
    public void Verify_AcceptsTheCodeForTheCurrentStepAndOneStepEitherSide()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111109);
        var step = Totp.StepAt(now);

        Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step), now, null).Should().Be(step);
        Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step - 1), now, null).Should().Be(step - 1);
        Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step + 1), now, null).Should().Be(step + 1);
    }

    [Fact]
    public void Verify_RejectsCodesOutsideTheDriftWindow()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111109);
        var step = Totp.StepAt(now);

        Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step - 2), now, null).Should().BeNull();
        Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step + 2), now, null).Should().BeNull();
    }

    [Fact]
    public void Verify_RejectsAStepThatWasAlreadyUsed_SoACodeCannotBeReplayed()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111109);
        var step = Totp.StepAt(now);
        var code = Totp.Compute(RfcSecret, step);

        var first = Totp.Verify(RfcSecret, code, now, null);
        var replay = Totp.Verify(RfcSecret, code, now, first);

        first.Should().Be(step);
        replay.Should().BeNull();
        Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step + 1), now, first).Should().Be(step + 1, "a later step is still fine");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12 456")]
    [InlineData("abcdef")]
    [InlineData("١٢٣٤٥٦")]
    public void Verify_RejectsMalformedInputWithoutThrowing(string? code) =>
        Totp.Verify(RfcSecret, code, DateTimeOffset.FromUnixTimeSeconds(1111111109), null).Should().BeNull();

    [Fact]
    public void Verify_RejectsTheWrongSecret()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111109);
        var code = Totp.Compute(RfcSecret, Totp.StepAt(now));

        Totp.Verify(Totp.NewSecret(), code, now, null).Should().BeNull();
    }

    [Fact]
    public void NewSecret_Is160BitsAndDiffersEachTime()
    {
        Totp.NewSecret().Should().HaveCount(20);
        Totp.NewSecret().Should().NotEqual(Totp.NewSecret());
    }

    [Fact]
    public void ProvisioningUri_CarriesTheSecretIssuerAndParametersAuthenticatorsExpect()
    {
        var uri = Totp.ProvisioningUri("HRMS", "asha@example.com", RfcSecret);

        uri.Should().StartWith("otpauth://totp/HRMS:asha%40example.com?");
        uri.Should().Contain($"secret={Base32.Encode(RfcSecret)}").And.Contain("issuer=HRMS")
            .And.Contain("algorithm=SHA1").And.Contain("digits=6").And.Contain("period=30");
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_MatchesTheRfc4648Vectors(string text, string expected)
    {
        var bytes = Encoding.ASCII.GetBytes(text);

        Base32.Encode(bytes).Should().Be(expected);
        Base32.TryDecode(expected)?.Should().Equal(bytes);
    }

    [Theory]
    [InlineData("MZXW6===")]
    [InlineData("mzxw6")]
    public void Base32_DecodesWithPaddingAndInAnyCase(string text) =>
        Base32.TryDecode(text).Should().Equal(Encoding.ASCII.GetBytes("foo"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("MZXW1")]
    [InlineData("not base32!")]
    public void Base32_RefusesInvalidInput(string? text) => Base32.TryDecode(text).Should().BeNull();

    // --- recovery codes -------------------------------------------------------------------------------------------------

    [Fact]
    public void RecoveryCodes_AreEightDistinctSixteenCharacterCodesInGroupsOfFour()
    {
        var codes = RecoveryCodes.Generate();

        codes.Should().HaveCount(RecoveryCodes.CodeCount).And.OnlyHaveUniqueItems();
        codes.Should().OnlyContain(code => System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Z2-7]{4}(-[A-Z2-7]{4}){3}$"));
    }

    [Fact]
    public void RecoveryCodes_NormaliseAnyReasonableTypingToTheSameThing()
    {
        var code = RecoveryCodes.Generate(1)[0];

        RecoveryCodes.Normalise(code).Should().Be(code.Replace("-", string.Empty, StringComparison.Ordinal));
        RecoveryCodes.Normalise(code.ToLowerInvariant().Replace('-', ' ')).Should().Be(RecoveryCodes.Normalise(code));
        RecoveryCodes.LooksValid(RecoveryCodes.Normalise(code)).Should().BeTrue();
        RecoveryCodes.LooksValid("SHORT").Should().BeFalse();
        RecoveryCodes.LooksValid("AAAA1AAAAAAAAAAA").Should().BeFalse("1 is not in the base32 alphabet");
    }

    [Fact]
    public void RecoveryCodes_HashIsStableAndNeverTheCodeItself()
    {
        var normalised = RecoveryCodes.Normalise(RecoveryCodes.Generate(1)[0]);

        RecoveryCodes.Hash(normalised).Should().Be(RecoveryCodes.Hash(normalised)).And.HaveLength(64).And.NotContain(normalised);
        RecoveryCodes.Hash(normalised).Should().NotBe(RecoveryCodes.Hash(normalised + "A"));
    }

    // --- field encryption -----------------------------------------------------------------------------------------------

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static AesGcmFieldEncryptor Encryptor(string currentId, params (string Id, string Key)[] keys) =>
        new(Options.Create(new FieldEncryptionOptions
        {
            CurrentKeyId = currentId,
            Keys = [.. keys.Select(k => new FieldEncryptionKey { Id = k.Id, Key = k.Key })],
        }));

    [Fact]
    public void Protect_ThenUnprotect_WithTheSameContext_ReturnsTheOriginal()
    {
        var encryptor = Encryptor("k1", ("k1", NewKey()));

        var protectedValue = encryptor.Protect("JBSWY3DPEHPK3PXP", "user_account.mfa_secret:t:u");

        protectedValue.Should().StartWith("v1.k1.").And.NotContain("JBSWY3DPEHPK3PXP");
        encryptor.Unprotect(protectedValue, "user_account.mfa_secret:t:u").Should().Be("JBSWY3DPEHPK3PXP");
    }

    [Fact]
    public void Protect_OfTheSameValueTwice_GivesDifferentCiphertextBecauseTheNonceIsRandom()
    {
        var encryptor = Encryptor("k1", ("k1", NewKey()));

        encryptor.Protect("same", "ctx").Should().NotBe(encryptor.Protect("same", "ctx"));
    }

    [Fact]
    public void Unprotect_WithADifferentContext_Fails_SoACiphertextCannotBeMovedToAnotherRowOrTenant()
    {
        var encryptor = Encryptor("k1", ("k1", NewKey()));
        var protectedValue = encryptor.Protect("secret", "user_account.mfa_secret:tenantA:userA");

        FluentActions.Invoking(() => encryptor.Unprotect(protectedValue, "user_account.mfa_secret:tenantB:userA"))
            .Should().Throw<CryptographicException>();
        FluentActions.Invoking(() => encryptor.Unprotect(protectedValue, "user_account.mfa_secret:tenantA:userB"))
            .Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Unprotect_OfATamperedValue_Fails()
    {
        var encryptor = Encryptor("k1", ("k1", NewKey()));
        var parts = encryptor.Protect("secret", "ctx").Split('.');
        var bytes = Convert.FromBase64String(parts[2]);
        bytes[^1] ^= 0x01;
        var tampered = $"{parts[0]}.{parts[1]}.{Convert.ToBase64String(bytes)}";

        FluentActions.Invoking(() => encryptor.Unprotect(tampered, "ctx")).Should().Throw<CryptographicException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("v1.k1.not-base64!!")]
    [InlineData("v1.k1.AAAA")]
    [InlineData("v2.k1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("v1.unknown.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Unprotect_OfMalformedInput_FailsWithACryptographicException(string value)
    {
        var encryptor = Encryptor("k1", ("k1", NewKey()));

        FluentActions.Invoking(() => encryptor.Unprotect(value, "ctx")).Should().Throw<CryptographicException>();
    }

    [Fact]
    public void KeyRotation_ValuesMadeWithAnOldKeyStillOpen_AndNewOnesUseTheNewKey()
    {
        var oldKey = NewKey();
        var newKey = NewKey();
        var before = Encryptor("old", ("old", oldKey)).Protect("secret", "ctx");

        var rotated = Encryptor("new", ("old", oldKey), ("new", newKey));

        rotated.Unprotect(before, "ctx").Should().Be("secret");
        rotated.Protect("secret", "ctx").Should().StartWith("v1.new.");
        FluentActions.Invoking(() => Encryptor("new", ("new", newKey)).Unprotect(before, "ctx")).Should().Throw<CryptographicException>();
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "test";

        public string ContentRootPath { get; set; } = ".";

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static bool Valid(string environment, FieldEncryptionOptions options) =>
        new FieldEncryptionOptionsValidator(new FakeEnvironment(environment)).Validate(null, options).Succeeded;

    [Fact]
    public void Validator_RequiresAUsableKeySet()
    {
        var good = new FieldEncryptionOptions { CurrentKeyId = "k1", Keys = [new FieldEncryptionKey { Id = "k1", Key = NewKey() }] };

        Valid("Production", good).Should().BeTrue();
        Valid("Production", new FieldEncryptionOptions()).Should().BeFalse("no keys");
        Valid("Production", new FieldEncryptionOptions { CurrentKeyId = "missing", Keys = good.Keys }).Should().BeFalse("current key not configured");
        Valid("Production", new FieldEncryptionOptions
        {
            CurrentKeyId = "k1",
            Keys = [new FieldEncryptionKey { Id = "k1", Key = Convert.ToBase64String(new byte[16]) }],
        }).Should().BeFalse("a 128-bit key is too short");
        Valid("Production", new FieldEncryptionOptions
        {
            CurrentKeyId = "k1",
            Keys = [new FieldEncryptionKey { Id = "k1", Key = "not base64!" }],
        }).Should().BeFalse("not base64");
        Valid("Production", new FieldEncryptionOptions
        {
            CurrentKeyId = "k1",
            Keys = [new FieldEncryptionKey { Id = "k1", Key = NewKey() }, new FieldEncryptionKey { Id = "k1", Key = NewKey() }],
        }).Should().BeFalse("duplicate ids");
    }

    [Fact]
    public void Validator_RefusesTheDevelopmentKeyOutsideDevelopment()
    {
        var dev = new FieldEncryptionOptions
        {
            CurrentKeyId = FieldEncryptionOptions.DevelopmentKeyId,
            Keys = [new FieldEncryptionKey { Id = FieldEncryptionOptions.DevelopmentKeyId, Key = NewKey() }],
        };

        Valid("Development", dev).Should().BeTrue();
        Valid("Production", dev).Should().BeFalse();
        Valid("Staging", dev).Should().BeFalse();
    }
}
