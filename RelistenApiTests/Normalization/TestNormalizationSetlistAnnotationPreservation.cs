using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

/// <summary>Provider notes retain their source coordinates independently of display text and numeric references.</summary>
[TestFixture]
public sealed class TestNormalizationSetlistAnnotationPreservation
{
    private readonly NormalizationProviderDecoder decoder = new();
    private readonly NormalizationSetlistExtractor extractor = new();

    [Test]
    public void PhantasyDefinitionsKeepDuplicateAndMissingNumbersWithoutInventingReferences()
    {
        var input = Phantasy();
        var decoded = decoder.Decode(input);
        var account = Extract(input, decoded).Accounts.Single().Account;

        account.Footnotes.Select(note => note.Number?.Value).Should().Equal(2, 2, null, 3, null);
        account
            .Footnotes.Select(note => note.Text.Text.Value)
            .Should()
            .Equal(
                "First guest",
                "Second guest",
                "Unnumbered note",
                "Unreferenced note",
                "Malformed number"
            );
        account.Footnotes[0].Number!.Evidence.JsonPointer.Should().Be("/data/footnotes/0/id");
        account.Footnotes[1].Number!.Evidence.JsonPointer.Should().Be("/data/footnotes/1/id");
        account
            .Footnotes.Select(note => note.Text.Text.Evidence.JsonPointer)
            .Should()
            .Equal(Enumerable.Range(0, 5).Select(index => $"/data/footnotes/{index}/note"));
        account
            .Passages.Single()
            .FootnoteNumbers.Select(number => number.Value)
            .Should()
            .Equal(2, 99);
        account
            .Passages.Single()
            .Annotations.Select(note => note.Text.Value)
            .Should()
            .Equal("First guest", "Second guest");
        account
            .Footnotes.Should()
            .OnlyContain(note =>
                note.Text.Format == ProviderTextFormat.PlainText && note.Text.PlainText == null
            );
        decoded
            .Issues.Should()
            .Contain(issue =>
                issue.Code == "unresolved_footnote"
                && issue.Evidence.JsonPointer == "/data/sets/0/songs/0/footnotes/1"
            );
        decoded
            .Issues.Should()
            .Contain(issue =>
                issue.Code == "expected_integer"
                && issue.Evidence.JsonPointer == "/data/footnotes/4/id"
            );
    }

    [TestCase("setlist.fm")]
    [TestCase("phantasytour.com")]
    public void SetNotesDistinguishAbsentExplicitEmptyAndUncapturedValues(string provider)
    {
        var sets = new[]
        {
            new Dictionary<string, object?>(),
            new Dictionary<string, object?> { ["notes"] = Array.Empty<string>() },
            new Dictionary<string, object?> { ["info"] = "Set note & literal text" },
            new Dictionary<string, object?> { ["notes"] = null },
            new Dictionary<string, object?> { ["notes"] = new[] { 7 } },
        };
        foreach (var set in sets)
        {
            set[provider == "setlist.fm" ? "song" : "songs"] = Array.Empty<object>();
        }
        var input =
            provider == "setlist.fm"
                ? Input(
                    provider,
                    "setlist-page",
                    "artist:page=1",
                    "setlist-fm-http-v1",
                    JsonSerializer.Serialize(
                        new
                        {
                            page = 1,
                            itemsPerPage = 20,
                            total = 1,
                            setlist = new[] { new { id = "show", sets = new { set = sets } } },
                        }
                    )
                )
                : Input(
                    provider,
                    "show",
                    "4",
                    "phantasy-tour-http-v1",
                    JsonSerializer.Serialize(new { data = new { id = 4, sets } })
                );
        var decoded = decoder.Decode(input);
        var observed = Extract(input, decoded).Accounts.Single().Account.Sets;

        observed[0].Annotations.Should().BeNull();
        observed[1].Annotations.Should().NotBeNull().And.BeEmpty();
        observed[2]
            .Annotations.Should()
            .ContainSingle()
            .Which.Text.Value.Should()
            .Be("Set note & literal text");
        observed[2].Annotations!.Single().Text.Evidence.JsonPointer.Should().EndWith("/2/info");
        observed[3].Annotations.Should().BeNull();
        observed[4].Annotations.Should().BeNull();
        decoded.Issues.Should().Contain(issue => issue.Code == "expected_text");
        var absent = JsonSerializer.SerializeToElement(observed[0]);
        absent.TryGetProperty("Annotations", out _).Should().BeFalse();
        JsonSerializer
            .SerializeToElement(observed[1])
            .GetProperty("Annotations")
            .GetArrayLength()
            .Should()
            .Be(0);
    }

    [Test]
    public void HtmlNotesKeepExactMarkupAndSeparateEntityDecodedDisplayText()
    {
        var raw = """
            🎤<div class="setlist-container">
            <div class="setlist-date"><a>Phish</a><a href="/setlists/phish-june-01-2000.html">Date</a></div>
            <div class="setlist-body"><span class="set-label">SET 1</span>
            <a class="setlist-song" href="/song/song" title="A &amp; B">Song</a><sup title="Guest &amp; friend">1</sup></div>
            <div class="setlist-footer"><em>1</em> Guest &amp; friend</div>
            <div class="setlist-notes"><b>Note</b> &amp; text</div>
            <div class="setlist-footer"><span></span></div>
            </div>
            """;
        var input = Input(
            "phish.net",
            "ratings",
            "2000-06-01",
            "phish-net-http-v1",
            raw,
            "text/html"
        );
        var decoded = decoder.Decode(input);
        var account = Extract(input, decoded).Accounts.Single().Account;
        var definition = account.Footnotes[0];

        definition.Number.Should().BeNull();
        definition.Text.Format.Should().Be(ProviderTextFormat.SourceMarkup);
        definition.Text.PlainText.Should().Be("1 Guest & friend");
        definition
            .Text.Text.Value.Should()
            .Be("<div class=\"setlist-footer\"><em>1</em> Guest &amp; friend</div>");
        definition.Text.Text.Evidence.Quote.Should().Be(definition.Text.Text.Value);
        var note = account.Text.Single(text => text.Field == "setlist_notes");
        note.PlainText.Should().Be("Note & text");
        note.Text.Value.Should().Contain("<b>Note</b> &amp; text");
        var passage = account.Passages.Single();
        passage
            .FootnoteNumbers.Should()
            .BeEmpty("the visible superscript has no provider-supported numeric definition link");
        var tooltip = passage.Annotations.Single(text => text.Field == "footnote");
        tooltip.Text.Value.Should().Be("Guest &amp; friend");
        tooltip.Text.Evidence.Quote.Should().Be("Guest &amp; friend");
        tooltip.PlainText.Should().Be("Guest & friend");
        passage
            .Annotations.Should()
            .OnlyContain(text => text.Format == ProviderTextFormat.SourceMarkup);
        account.Footnotes[1].Text.PlainText.Should().BeNull();
        JsonSerializer
            .SerializeToElement(account.Footnotes[1].Text)
            .TryGetProperty("PlainText", out _)
            .Should()
            .BeFalse();
        using var document = NormalizationRetainedDocument.Open(input);
        foreach (var text in account.Text.Concat(passage.Annotations))
        {
            document.ValidateEvidence(text.Text.Evidence);
        }
    }

    [TestCase("number")]
    [TestCase("definition")]
    [TestCase("set")]
    public void ExtractorValidatesNewDefinitionAndSetAnnotationCitations(string target)
    {
        var input = Phantasy();
        var decoded = decoder.Decode(input);
        var account = decoded.Setlists.Single();
        var changed = target switch
        {
            "number" => account with
            {
                Footnotes =
                [
                    account.Footnotes[0] with
                    {
                        Number = account.Footnotes[0].Number! with { Value = 88 },
                    },
                ],
            },
            "definition" => account with
            {
                Footnotes =
                [
                    account.Footnotes[0] with
                    {
                        Text = account.Footnotes[0].Text with
                        {
                            Text = account.Footnotes[0].Text.Text with { Value = "Invented" },
                        },
                    },
                ],
            },
            _ => account with
            {
                Sets =
                [
                    account.Sets[0] with
                    {
                        Annotations =
                        [
                            account.Sets[0].Annotations!.Single() with
                            {
                                Text = account.Sets[0].Annotations!.Single().Text with
                                {
                                    Evidence = new(Guid.NewGuid(), "/data/sets/0/info"),
                                },
                            },
                        ],
                    },
                ],
            },
        };
        var forged = decoded with { Setlists = [changed] };

        var extract = () => Extract(input, forged);

        extract.Should().Throw<ArgumentException>();
    }

    [TestCase("1992-11-21")]
    [TestCase("1997-11-22")]
    public void RetainedPhishNetPagesExposeDerivedDisplayWithoutReplacingRawSupport(string date)
    {
        var raw = File.ReadAllText(
            Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "Fixtures",
                "PhishNet",
                $"setlist-{date}.html"
            )
        );
        var input = Input("phish.net", "ratings", date, "phish-net-http-v1", raw, "text/html");
        var account = Extract(input, decoder.Decode(input)).Accounts.Single().Account;
        var notes = account
            .Text.Concat(account.Passages.SelectMany(passage => passage.Annotations))
            .ToArray();

        notes.Should().NotBeEmpty();
        notes.Should().OnlyContain(note => note.Format == ProviderTextFormat.SourceMarkup);
        notes.Should().Contain(note => note.PlainText != null && note.PlainText != note.Text.Value);
        account.Sets.Should().OnlyContain(set => set.Annotations == null);
        using var document = NormalizationRetainedDocument.Open(input);
        foreach (var note in notes)
        {
            note.Text.Value.Should().Be(note.Text.Evidence.Quote);
            document.ValidateEvidence(note.Text.Evidence);
        }
    }

    private static NormalizationInputRevision Phantasy() =>
        Input(
            "phantasytour.com",
            "show",
            "4",
            "phantasy-tour-http-v1",
            """
            {"data":{"id":4,"footnotes":[{"id":2,"note":"First guest"},{"id":2,"note":"Second guest"},
            {"note":"Unnumbered note"},{"id":3,"note":"Unreferenced note"},{"id":"bad","note":"Malformed number"}],
            "sets":[{"info":"Explicit set note","songs":[{"name":"Song","footnotes":[2,99]}]}]}}
            """
        );

    private NormalizationSetlistInputExtraction Extract(
        NormalizationInputRevision input,
        ProviderDecodeResult decoded
    ) =>
        extractor.Extract(
            input,
            decoded,
            new(
                input.Resource,
                0,
                new(input.Uuid, input.Resource),
                decoded
                    .Setlists.Select(account => new NormalizationPreparedSetlistAccount(
                        account,
                        new(
                            input.Uuid,
                            input.Resource,
                            account.Origin,
                            new(NormalizationCatalogBindingStatus.Missing, null, "missing"),
                            new(NormalizationCatalogBindingStatus.Missing, null, "missing"),
                            new(NormalizationCatalogBindingStatus.Missing, null, "missing"),
                            [],
                            new(0)
                        )
                    ))
                    .ToArray()
            )
        );

    private static NormalizationInputRevision Input(
        string provider,
        string type,
        string key,
        string version,
        string raw,
        string mediaType = "application/json"
    ) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Resource = new(provider, type, key),
            CaptureVersion = version,
            Payload = Encoding.UTF8.GetBytes(raw),
            MediaType = mediaType,
        };
}
