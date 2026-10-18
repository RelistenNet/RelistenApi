using System.Text;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

/// <summary>Actor IDs and citations retain each actual input account, array entry and raw HTML span.</summary>
[TestFixture]
public sealed class TestRetainedRecordingActorEvidence
{
    [Test]
    public void RepeatedArrayCreditsHaveDistinctAccountReferencesButJointRolesShareOneReference()
    {
        var input = Input(
            """{"metadata":{"identifier":"actor-test","description":["Recorded and transferred by Alice Vale aka Night Owl","Recorded and transferred by Alice Vale aka Night Owl"]},"files":[]}""",
            "application/json"
        );
        var result = Extract(input);

        result.Credits.Should().HaveCount(4);
        var groups = result.Credits.GroupBy(credit => credit.Evidence.JsonPointer).ToArray();
        groups.Should().HaveCount(2);
        foreach (var group in groups)
        {
            group
                .Select(credit => credit.ActorFacts!.ActorUuid)
                .Distinct()
                .Should()
                .ContainSingle();
            group
                .Select(credit => credit.ActorFacts!.ReferenceUuid)
                .Distinct()
                .Should()
                .ContainSingle();
        }
        groups
            .Select(group => group.First().ActorFacts!.ActorUuid)
            .Distinct()
            .Should()
            .HaveCount(2);
        using var document = NormalizationRetainedDocument.Open(input);
        foreach (
            var citation in result.Credits.SelectMany(credit => credit.ActorFacts!.Citations())
        )
        {
            document.ValidateEvidence(citation);
        }
    }

    [Test]
    public void NarrowHtmlActorFactsMapEveryCitationToOriginalUnicodeCodePoints()
    {
        const string prefix = "<header>🎵</header><section>";
        const string prose = "Taped by Zoë Vale aka Night Owl (zoe@example.test)";
        var input = Input(prefix + prose + "</section>", "text/html");
        var start = prefix.EnumerateRunes().Count();
        var field = new ProviderTextObservation(
            "description",
            new(prose, new(input.Uuid, "", start, start + prose.EnumerateRunes().Count(), prose))
        );
        var result = new RetainedRecordingCreditExtractor().Extract(input, [field]);

        var facts = result.Credits.Should().ContainSingle().Which.ActorFacts!;
        facts.Anchor.Quote.Should().Be("Zoë Vale");
        facts.Anchor.StartCodePoint.Should().Be(start + "Taped by ".Length);
        facts.Aliases.Should().ContainSingle().Which.Wording.Should().Be("Night Owl");
        facts.Contacts.Should().ContainSingle().Which.LookupKey.Should().Be("zoe@example.test");
        using var document = NormalizationRetainedDocument.Open(input);
        foreach (var citation in facts.Citations())
        {
            citation.InputRevisionUuid.Should().Be(input.Uuid);
            document.ValidateEvidence(citation);
        }
    }

    [Test]
    public void NewInputRevisionDoesNotReuseSourceLocalActorIdentities()
    {
        const string json =
            """{"metadata":{"identifier":"actor-test","taper":"Alice Vale"},"files":[]}""";
        var first = Input(json, "application/json");
        var second = Input(json, "application/json");

        var one = Extract(first).Credits.Single().ActorFacts!;
        var two = Extract(second).Credits.Single().ActorFacts!;
        one.Anchor.Quote.Should().Be(two.Anchor.Quote);
        one.ActorUuid.Should().NotBe(two.ActorUuid);
        one.ReferenceUuid.Should().NotBe(two.ReferenceUuid);
    }

    [Test]
    public void JsonHtmlSuffixAndAngleContactKeepTheirOriginalRetainedFieldAndStableReferences()
    {
        var input = Input(
            """{"metadata":{"identifier":"actor-test","description":["🎵<div>Recorded by <b>Renée&nbsp;River, IV</b> <river&#64;example.test></div>"]},"files":[]}""",
            "application/json"
        );

        var result = Extract(input);
        var replay = Extract(input);

        var credit = result.Credits.Should().ContainSingle().Which;
        credit.Name.Should().Be("Renée River, IV");
        credit.Evidence.JsonPointer.Should().Be("/metadata/description/0");
        credit.Evidence.Quote.Should().Contain("<river&#64;example.test>");
        var facts = credit.ActorFacts!;
        facts.Version.Should().Be("recording-actor-evidence-v1");
        facts.Anchor.Quote.Should().Be("Renée&nbsp;River, IV");
        var contact = facts.Contacts.Should().ContainSingle().Which;
        contact.Literal.Should().Be("river&#64;example.test");
        contact.LookupKey.Should().Be("river@example.test");
        facts.Kind.Should().BeNull();
        replay.Credits.Single().ActorFacts.Should().BeEquivalentTo(facts);
        using var document = NormalizationRetainedDocument.Open(input);
        foreach (var citation in facts.Citations())
        {
            citation.InputRevisionUuid.Should().Be(input.Uuid);
            citation.JsonPointer.Should().Be("/metadata/description/0");
            document.ValidateEvidence(citation);
        }
    }

    private static RetainedCreditExtraction Extract(NormalizationInputRevision input) =>
        new RetainedRecordingCreditExtractor().Extract(
            input,
            new NormalizationProviderDecoder().Decode(input).Recordings.Single().Text
        );

    private static NormalizationInputRevision Input(string payload, string mediaType) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Resource = new("archive.org", "item_metadata", "actor-test"),
            Payload = Encoding.UTF8.GetBytes(payload),
            MediaType = mediaType,
            CaptureVersion = "archive-item-response-v1",
        };
}
