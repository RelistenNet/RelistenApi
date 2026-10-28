using System.Text.Json;
using FluentAssertions;
using Relisten.Normalization;

namespace Relisten.NormalizationTests;

public class TestModelCalls
{
    [TestCase("""{"name":"Shakedown\u0000 Street"}""", "Shakedown� Street")]
    [TestCase("""{"name":"Shakedown\U0000 Street"}""", "Shakedown� Street")]
    [TestCase("""{"name":"a\\\u0000b"}""", "a\\�b")]
    [TestCase("""{"name":"a\\u0000b"}""", "a\\u0000b")]
    [TestCase("""{"name":"a\\\\u0000b"}""", "a\\\\u0000b")]
    public void ANulInAReplyIsReplacedAndEverythingElseKept(string reply, string name)
    {
        var stored = ModelCalls.WithoutNul(reply);

        stored.Should().NotContainAny("\0");
        JsonDocument.Parse(stored).RootElement.GetProperty("name").GetString().Should().Be(name);
    }

    [Test]
    public void ARawNulCharacterIsReplaced()
    {
        ModelCalls.WithoutNul("not json \0 at all").Should().Be("not json � at all");
    }
}
