using FluentAssertions;
using NUnit.Framework;
using RelistenUserService.Identity.Usernames;

namespace RelistenUserServiceTests;

[TestFixture]
public sealed class TestUsernamePolicy
{
    private readonly UsernamePolicy _policy = new();

    [TestCase("admin")]
    [TestCase("accounts")]
    [TestCase("support_team")]
    [TestCase("team_official")]
    [TestCase("r_e_l_i_s_t_e_n_fan")]
    [TestCase("sonos")]
    public void RejectsSystemAndImpersonationNames(string username)
    {
        _policy.TryNormalize(username, out _).Should().BeFalse();
    }

    [TestCase("f_u_c_k")]
    [TestCase("n4zi_listener")]
    [TestCase("pedophile")]
    [TestCase("friendly_nigger")]
    public void RejectsObviousAndObfuscatedAbuse(string username)
    {
        _policy.TryNormalize(username, out _).Should().BeFalse();
    }

    [TestCase("alice_77", "alice_77")]
    [TestCase("Stafford", "stafford")]
    [TestCase("concertgoer42", "concertgoer42")]
    [TestCase("supporter42", "supporter42")]
    [TestCase("grapefruit", "grapefruit")]
    [TestCase("therapist", "therapist")]
    public void KeepsLegitimateAsciiNames(string username, string expected)
    {
        _policy.TryNormalize(username, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Test]
    public void DoesNotTrimUserInput()
    {
        _policy.TryNormalize(" alice ", out _).Should().BeFalse();
    }
}
