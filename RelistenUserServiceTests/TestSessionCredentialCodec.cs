using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;
using RelistenUserService.Authentication.Sessions;

namespace RelistenUserServiceTests;

[TestFixture]
public sealed class TestSessionCredentialCodec
{
    private readonly SessionCredentialCodec _codec = new();

    [Test]
    public void Issues_a_versioned_256_bit_credential_for_a_uuid_v7_session()
    {
        var sessionId = Guid.CreateVersion7();

        var issued = _codec.Issue(sessionId);
        var parsed = _codec.TryParse(issued.CookieValue, out var credential);

        parsed.Should().BeTrue();
        credential.Should().NotBeNull();
        credential!.SessionId.Should().Be(sessionId);
        credential.ValidatorHash.Should().HaveCount(32);
        _codec.ValidatorMatches(
            credential.ValidatorHash,
            issued.ValidatorHash).Should().BeTrue();
        issued.CookieValue.Split('.').Should().HaveCount(3);
        issued.CookieValue.Should().MatchRegex("^[A-Za-z0-9._-]+$");
        credential.Clear();
    }

    [Test]
    public void Reissuing_the_same_session_id_uses_a_new_validator()
    {
        var sessionId = Guid.CreateVersion7();

        var first = _codec.Issue(sessionId);
        var second = _codec.Issue(sessionId);

        CryptographicOperations.FixedTimeEquals(
            first.ValidatorHash,
            second.ValidatorHash).Should().BeFalse();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("v2.00000000000000000000000000000000.invalid")]
    [TestCase("v1.not-a-guid.invalid")]
    [TestCase("v1.00000000000000000000000000000000.invalid")]
    [TestCase("v1.00000000000000000000000000000000.invalid.extra")]
    public void Rejects_malformed_credentials(string? value)
    {
        _codec.TryParse(value, out var credential).Should().BeFalse();
        credential.Should().BeNull();
    }

    [Test]
    public void Rejects_a_uuid_that_is_not_version_seven()
    {
        var issued = _codec.Issue(Guid.CreateVersion7());
        var parts = issued.CookieValue.Split('.');
        parts[1] = Guid.NewGuid().ToString("N");

        _codec.TryParse(string.Join('.', parts), out _).Should().BeFalse();
    }

    [Test]
    public void Rejects_any_changed_validator()
    {
        var issued = _codec.Issue(Guid.CreateVersion7());
        _codec.TryParse(issued.CookieValue, out var credential).Should().BeTrue();
        var changedHash = credential!.ValidatorHash.ToArray();
        changedHash[16] ^= 1;

        _codec.ValidatorMatches(changedHash, issued.ValidatorHash).Should().BeFalse();
        credential.Clear();
        CryptographicOperations.ZeroMemory(changedHash);
    }
}
