using System.Text;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestNormalizationProviderDecoderJerryRealPage
{
    [Test]
    public void RealJerryPageRetainsCitedLabelsDateAndShowFactsWithoutChangingItsSongSequence()
    {
        // The adjacent provenance JSON identifies the exact public S3 response and byte ranges.
        // This is a development source-coverage regression, not an identity-accuracy holdout.
        var raw = File.ReadAllText(
            Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "Fixtures",
                "Jerry",
                "1995-07-02-real-show-excerpt.html"
            )
        );
        var input = new NormalizationInputRevision
        {
            Uuid = Guid.NewGuid(),
            Resource = new(
                "jerrygarcia.com",
                "mirror_show_page",
                "grateful_dead/1995-07-02-deer-creek-music-center-noblesville-in-usa"
            ),
            CaptureVersion = "jerry-mirror-html:1",
            MediaType = "text/html; charset=utf-8",
            Payload = Encoding.UTF8.GetBytes(raw),
        };

        var decoded = new NormalizationProviderDecoder().Decode(input);
        var account = decoded.Setlists.Should().ContainSingle().Which;

        using var assertions = new AssertionScope();
        account.Ordered.Should().BeTrue();
        (account.LiteralDate?.Value).Should().Be("July 2, 1995");
        (account.LiteralDate?.Evidence.Quote)
            .Should()
            .Be("<p class=\"details-date\">July 2, 1995</p>");
        account.Sets.Select(set => set.Label?.Value).Should().Equal("Set 1", "Set 2", "Encore");
        account
            .Sets.Select(set => set.Label?.Evidence.Quote)
            .Should()
            .Equal(
                "<h3 class=\"set-title\">Set 1</h3>",
                "<h3 class=\"set-title\">Set 2</h3>",
                "<h3 class=\"set-title\">Encore</h3>"
            );
        account.Sets.Select(set => set.Ordinal).Should().Equal(1, 2, 3);
        account
            .Passages.Select(passage => passage.SetOrdinal)
            .Should()
            .Equal(Enumerable.Repeat<int?>(1, 8).Concat(Enumerable.Repeat<int?>(2, 9)).Append(3));
        account
            .Passages.Select(passage => passage.SetLabel?.Value)
            .Should()
            .Equal(
                Enumerable.Repeat("Set 1", 8).Concat(Enumerable.Repeat("Set 2", 9)).Append("Encore")
            );
        account
            .Passages.Select(passage => passage.Ordinal)
            .Should()
            .Equal(Enumerable.Range(1, 18).Select(value => (int?)value));
        account
            .Passages.Select(passage => passage.Title.Value)
            .Should()
            .Equal(
                "Here Comes Sunshine",
                "Walkin' Blues",
                "Dire Wolf",
                "It's All Over Now",
                "Brokedown Palace",
                "Desolation Row",
                "Tennessee Jed",
                "Let It Grow",
                "Scarlet Begonias",
                "Fire On The Mountain",
                "Victim Or The Crime",
                "It's All Too Much",
                "New Speedway Boogie",
                "Drums",
                "Space",
                "Attics Of My Life",
                "Sugar Magnolia",
                "The Mighty Quinn (Quinn The Eskimo)"
            );
        account
            .Passages.Where(passage => passage.TransitionMarker != null)
            .Select(passage => passage.Ordinal)
            .Should()
            .Equal(Enumerable.Range(9, 8).Select(value => (int?)value));
        account
            .Passages.Where(passage => passage.TransitionMarker != null)
            .Should()
            .OnlyContain(passage => passage.TransitionMarker!.Value == ">");
        account
            .Passages.SelectMany(passage => passage.SongIdentities)
            .Select(identity => identity.Identity.ProviderKey)
            .Should()
            .Equal(
                "here-comes-sunshine",
                "dire-wolf",
                "brokedown-palace",
                "tennessee-jed",
                "let-it-grow",
                "scarlet-begonias",
                "fire-on-the-mountain",
                "victim-or-the-crime",
                "new-speedway-boogie",
                "attics-of-my-life",
                "sugar-magnolia"
            );
        account
            .Passages.SelectMany(passage => passage.SongIdentities)
            .Should()
            .OnlyContain(identity =>
                identity.Identity.Provider == "jerrygarcia.com"
                && identity.Identity.ResourceType == "song"
            );
        account.Passages.Should().OnlyContain(passage => passage.OccurrenceIdentity == null);

        var note = account.Text.SingleOrDefault(text => text.Field == "show_facts");
        account.Text.Should().ContainSingle();
        note.Should().NotBeNull();
        if (note != null)
        {
            note.Format.Should().Be(ProviderTextFormat.SourceMarkup);
            note.Text.Value.Should().Be(note.Text.Evidence.Quote);
            note.Text.Value.Should().Contain("<br />");
            note.PlainText.Should().Contain("\n");
            note.PlainText!.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Should()
                .HaveCount(14);
            note.PlainText.Should().Contain("Final \"Broken Arrow\".");
            note.PlainText.Should()
                .Contain("Second show scheduled for the following night was cancelled.");
        }
        decoded.Issues.Should().NotContain(issue => issue.Code == "html_source_span_unavailable");
        using var document = NormalizationRetainedDocument.Open(input);
        ValidateEvidence(JsonSerializer.SerializeToElement(decoded), document);
    }

    private static void ValidateEvidence(JsonElement value, NormalizationRetainedDocument document)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("JsonPointer", out _))
            {
                document.ValidateEvidence(value.Deserialize<NormalizedEvidenceReference>()!);
            }
            foreach (var property in value.EnumerateObject())
            {
                ValidateEvidence(property.Value, document);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                ValidateEvidence(item, document);
            }
        }
    }
}
