using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

/// <summary>Original Go Set representations retain provider identity and cited upstream facts.</summary>
[TestFixture]
public sealed class TestNormalizationProviderDecoderGoSet
{
    private readonly NormalizationProviderDecoder decoder = new();

    [Test]
    public void RealSongHistoryKeepsBothSongKeysActualOccurrencesAndOriginalPerformerWithoutAuthorship()
    {
        var input = Song();

        var result = decoder.Decode(input);

        var song = result.Repertoire.Should().ContainSingle().Subject;
        song.Title!.Value.Should().Be("Such a Night");
        song.Identities.Select(value => value.Identity)
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    new NormalizationResourceKey("go-set.net", "song_slug", "such-a-night"),
                    new NormalizationResourceKey("go-set.net", "song", "34"),
                }
            );
        song.WorkCredits.Should().BeEmpty();
        song.WorkIdentities.Should().BeEmpty();
        song.CompleteAuthorship.Should().BeFalse();
        result.Recordings.Should().BeEmpty();
        result.Setlists.Should().HaveCount(26);
        result
            .Setlists.Should()
            .OnlyContain(account =>
                !account.Ordered && account.Origin.Scope == ProviderObservationScope.PageMember
            );
        foreach (var account in result.Setlists)
        {
            var passage = account.Passages.Should().ContainSingle().Subject;
            passage
                .SongIdentities.Select(value => value.Identity)
                .Should()
                .Equal(song.Identities.Select(value => value.Identity));
            passage.OccurrenceIdentity.Should().NotBeNull();
            passage
                .Ordinal.Should()
                .BeNull("history order does not establish a song position within a show");
            var performer = passage.PerformerAnnotations.Should().ContainSingle().Subject;
            performer.Kind.Should().Be("cover_artist");
            performer.Name!.Value.Should().Be("Dr. John");
            performer.Name.Evidence.Quote.Should().Contain("was originally performed by Dr. John.");
        }
        var first = result.Setlists.Single(account => account.LiteralDate!.Value == "2023-04-12");
        first
            .Origin.Item.Should()
            .Be(new NormalizationResourceKey("go-set.net", "show", "1673482876"));
        first
            .Passages.Single()
            .OccurrenceIdentity!.Identity.Should()
            .Be(new NormalizationResourceKey("go-set.net", "passage", "18216"));
        first.Passages.Single().Annotations.Single().PlainText.Should().Be("Dr. John");
        var later = result.Setlists.Single(account => account.LiteralDate!.Value == "2026-04-30");
        later.Origin.Item.ProviderKey.Should().Be("1766949127");
        later.Passages.Single().OccurrenceIdentity!.Identity.ProviderKey.Should().Be("93798");
        ValidateEvidence(input, result);
    }

    [TestCase("2023-04-12", "1673482876", 24, "Lost Lake Lounge")]
    [TestCase("2026-04-30", "1766949127", 11, "Mohawk")]
    public void RealShowKeepsItsAccountSequenceAndMixedFootnotesWithoutInventingRolesOrOccurrences(
        string date,
        string showId,
        int count,
        string venue
    )
    {
        var input = Show(date);

        var result = decoder.Decode(input);

        var account = result.Setlists.Should().ContainSingle().Subject;
        account.Origin.Item.Should().Be(new NormalizationResourceKey("go-set.net", "show", showId));
        account.Origin.Evidence.Quote.Should().Contain("api/embed.php?id=" + showId);
        account.Origin.Scope.Should().Be(ProviderObservationScope.PageMember);
        account.LiteralDate!.Value.Should().Be(date);
        account.Venue!.Names.Single().Value.Should().Be(venue);
        account.Ordered.Should().BeTrue();
        account.Passages.Should().HaveCount(count);
        account
            .Passages.Select(passage => passage.Ordinal)
            .Should()
            .Equal(Enumerable.Range(1, count).Select(value => (int?)value));
        account.Passages.Should().OnlyContain(passage => passage.OccurrenceIdentity == null);
        account.Passages.Should().OnlyContain(passage => passage.PerformerAnnotations.Count == 0);
        account.Sets.Last().Label!.Value.Should().Be("Encore:");
        var night = account.Passages.Last();
        night.Title.Value.Should().Be("Such a Night");
        night
            .SongIdentities.Single()
            .Identity.Should()
            .Be(new NormalizationResourceKey("go-set.net", "song_slug", "such-a-night"));
        night
            .Annotations.Should()
            .Contain(note => note.Field == "footnote" && note.PlainText == "Dr. John");
        account.Footnotes.Should().Contain(note => note.Text.PlainText!.Contains("Dr. John"));
        result.Repertoire.Should().BeEmpty();
        result.Recordings.Should().BeEmpty();
        if (date == "2023-04-12")
        {
            account
                .Sets.Select(set => set.Label!.Value)
                .Should()
                .Equal("Set 1:", "Set 2:", "Encore:");
            var struggle = account.Passages.Single(passage => passage.Title.Value == "Struggle");
            struggle.TransitionMarker!.Value.Should().Be("->");
            struggle
                .Annotations.Single()
                .PlainText.Should()
                .Contain("with Andy Frasco, Shawn Eckels, and Nick Gerlach");
            account
                .Passages.Single(passage => passage.Title.Value == "¿Por Que, Pedro?")
                .Title.Evidence.Quote.Should()
                .Contain("¿Por Que, Pedro?");
        }
        else
        {
            account.Sets.Select(set => set.Label!.Value).Should().Equal("One Set:", "Encore:");
        }
        ValidateEvidence(input, result);
    }

    [Test]
    public void WrongCanonicalSongOrRequestedShowDateProducesNoBorrowedAccount()
    {
        var wrongSong = Song(raw =>
            raw.Replace(
                "https://go-set.net/song/such-a-night",
                "https://go-set.net/song/another-song",
                StringComparison.Ordinal
            )
        );
        var song = decoder.Decode(wrongSong);
        song.Repertoire.Should().BeEmpty();
        song.Setlists.Should().BeEmpty();
        song.Issues.Should().Contain(issue => issue.Code == "go_set_song_identity_mismatch");
        var wrongShow = Html(
            "show-2023-04-12.html",
            "setlist_page",
            "date:2026-04-30",
            "go-set-setlist-html-v1"
        );
        var show = decoder.Decode(wrongShow);
        show.Setlists.Should().BeEmpty();
        show.Issues.Should().Contain(issue => issue.Code == "go_set_setlist_identity_mismatch");
        ValidateEvidence(wrongSong, song);
        ValidateEvidence(wrongShow, show);
    }

    [Test]
    public void APositionCannotReplaceAMissingHistoryOccurrenceOrCreateAnOriginalPerformerRole()
    {
        var input = Song(raw =>
            raw.Replace("data-uniqueid='18216'", "data-position='24'", StringComparison.Ordinal)
                .Replace(
                    "was originally performed by",
                    "was performed alongside",
                    StringComparison.Ordinal
                )
        );

        var result = decoder.Decode(input);

        result
            .Setlists.Single(account => account.LiteralDate!.Value == "2023-04-12")
            .Passages.Single()
            .OccurrenceIdentity.Should()
            .BeNull();
        result
            .Setlists.SelectMany(account => account.Passages)
            .Should()
            .OnlyContain(passage => passage.PerformerAnnotations.Count == 0);
        result.Repertoire.Single().WorkCredits.Should().BeEmpty();
        ValidateEvidence(input, result);
    }

    [Test]
    public void MalformedFootnoteAndUnlinkedTitleStayVisibleWithoutInventedNumberIdentityOrGuestRole()
    {
        var input = Show(
            "2023-04-12",
            raw =>
                raw.Replace(
                        "<sup title=\"Dr. John\">[7]</sup>",
                        "<sup title=\"with Dr. John\">[guest]</sup>",
                        StringComparison.Ordinal
                    )
                    .Replace(
                        "<a href=\"/song/such-a-night\" class=\"\" title=\"Such a Night\">Such a Night</a>",
                        "Such a Night",
                        StringComparison.Ordinal
                    )
        );

        var result = decoder.Decode(input);

        var passage = result.Setlists.Single().Passages.Last();
        passage.Title.Value.Should().Be("Such a Night");
        passage.SongIdentities.Should().BeEmpty();
        passage.OccurrenceIdentity.Should().BeNull();
        passage.FootnoteNumbers.Should().BeEmpty();
        passage.PerformerAnnotations.Should().BeEmpty();
        passage.Annotations.Single().PlainText.Should().Be("with Dr. John");
        result.Issues.Should().Contain(issue => issue.Code == "invalid_go_set_footnote_number");
        result
            .Issues.Should()
            .Contain(issue => issue.Code == "go_set_passage_song_link_unavailable");
        ValidateEvidence(input, result);
    }

    [Test]
    public void UnicodePrefixCannotShiftCitationsIntoUtf16Coordinates()
    {
        var input = Show("2023-04-12", raw => "<!-- 🎵 😀 retained test prefix -->\n" + raw);

        var result = decoder.Decode(input);

        result
            .Setlists.Single()
            .Passages.Single(passage => passage.Title.Value == "¿Por Que, Pedro?")
            .Title.Evidence.Quote.Should()
            .Contain("¿Por Que, Pedro?");
        ValidateEvidence(input, result);
    }

    [Test]
    public void FixtureReceiptsAuthenticateEveryOriginalBody()
    {
        using var metadata = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(FixtureDirectory, "captures.json"))
        );
        var captures = metadata.RootElement.GetProperty("captures").EnumerateArray().ToArray();
        captures.Should().HaveCount(3);
        foreach (var capture in captures)
        {
            var bytes = File.ReadAllBytes(
                Path.Combine(FixtureDirectory, capture.GetProperty("file").GetString()!)
            );
            bytes.LongLength.Should().Be(capture.GetProperty("bytes").GetInt64());
            Convert
                .ToHexStringLower(SHA256.HashData(bytes))
                .Should()
                .Be(capture.GetProperty("sha256").GetString());
            capture.GetProperty("mediaType").GetString().Should().StartWith("text/html");
            capture
                .GetProperty("requestedUrl")
                .GetString()
                .Should()
                .StartWith("https://go-set.net/");
            capture
                .GetProperty("capturedAt")
                .GetDateTimeOffset()
                .Should()
                .BeAfter(DateTimeOffset.Parse("2026-10-18T00:00:00Z"));
        }
    }

    private static string FixtureDirectory =>
        Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Fixtures",
            "normalization",
            "go-set"
        );

    private static NormalizationInputRevision Song(Func<string, string>? transform = null) =>
        Html(
            "song-such-a-night.html",
            "song_page",
            "such-a-night",
            "go-set-song-html-v1",
            transform
        );

    private static NormalizationInputRevision Show(
        string date,
        Func<string, string>? transform = null
    ) =>
        Html(
            "show-" + date + ".html",
            "setlist_page",
            "date:" + date,
            "go-set-setlist-html-v1",
            transform
        );

    private static NormalizationInputRevision Html(
        string file,
        string kind,
        string key,
        string version,
        Func<string, string>? transform = null
    )
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDirectory, file));
        var bytes = Encoding.UTF8.GetBytes(transform == null ? raw : transform(raw));
        return new()
        {
            Uuid = Guid.NewGuid(),
            Resource = new("go-set.net", kind, key),
            CaptureVersion = version,
            MediaType = "text/html; charset=UTF-8",
            Payload = bytes,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
        };
    }

    private static void ValidateEvidence(
        NormalizationInputRevision input,
        ProviderDecodeResult result
    )
    {
        using var document = NormalizationRetainedDocument.Open(input);
        Visit(JsonSerializer.SerializeToElement(result));
        return;

        void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (value.TryGetProperty("JsonPointer", out _))
                {
                    document.ValidateEvidence(value.Deserialize<NormalizedEvidenceReference>()!);
                }
                foreach (var property in value.EnumerateObject())
                {
                    Visit(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in value.EnumerateArray())
                {
                    Visit(element);
                }
            }
        }
    }
}
