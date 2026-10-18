using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestShadowPerformanceAdapter
{
    private static readonly ShadowCatalogArtist Artist = new(
        1,
        Guid.NewGuid(),
        "Fixture artist",
        ["phish.in"]
    );
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    [Test]
    public void ExistingEarlyAndLateShowIdsSurviveAndDifferentSetsKeepTheSamePerformance()
    {
        var early = Guid.NewGuid();
        var late = Guid.NewGuid();
        var earlySource = Guid.NewGuid();
        var lateSource = Guid.NewGuid();
        var firstSet = Track(11, earlySource, 101, early, "early", 1, "First set", "A Song");
        var secondSet = Track(
            11,
            earlySource,
            101,
            early,
            "early",
            2,
            "Second set",
            "Another Song"
        );
        var lateSet = Track(12, lateSource, 102, late, "late", 3, "First set", "A Song");
        var adapter = Observe(firstSet, secondSet, lateSet);

        var homes = new[] { firstSet, secondSet, lateSet }
            .Select(row =>
                adapter.Process(Input(row)).Single(result => result.Domain == "performances")
            )
            .ToArray();

        homes.Should().OnlyContain(result => result.Outcome == "proposed");
        homes
            .Select(result => ((ShadowPerformanceHome)result.Proposal!).ShowUuid)
            .Should()
            .Equal(early, early, late);
        homes
            .Select(result => ((ShadowPerformanceHome)result.Proposal!).PerformanceType)
            .Should()
            .Equal("early", "early", "late");
        homes
            .SelectMany(result => result.EntityKeys)
            .Where(key => key.StartsWith("show:"))
            .Distinct()
            .Should()
            .BeEquivalentTo(["show:" + early.ToString("D"), "show:" + late.ToString("D")]);
    }

    [Test]
    public void CompoundFileRetainsRepeatedSongMentionsAndOneDirectPlaybackHome()
    {
        var show = Guid.NewGuid();
        var source = Guid.NewGuid();
        var row = Track(
            11,
            source,
            101,
            show,
            "unknown",
            1,
            "Set",
            "A Song > Another Song > A Song"
        );
        var firstSong = Song("A Song");
        var secondSong = Song("Another Song");
        var adapter = Observe(firstSong, secondSong, row);

        var result = adapter
            .Process(Input(row, Extract(row)))
            .Single(value => value.Domain == "file_contents");
        var contents = (ShadowFileContents)result.Proposal!;

        result.Outcome.Should().Be("proposed");
        contents
            .Playback.Should()
            .Be(
                new ShadowFilePlayback(
                    row.GetProperty("row").GetProperty("uuid").GetGuid(),
                    source,
                    show,
                    "https://example.test/live.mp3"
                )
            );
        contents
            .TitleInterpretation!.Segments.Select(segment => segment.Song!.LegacySongUuid)
            .Should()
            .Equal(
                firstSong.GetProperty("row").GetProperty("uuid").GetGuid(),
                secondSong.GetProperty("row").GetProperty("uuid").GetGuid(),
                firstSong.GetProperty("row").GetProperty("uuid").GetGuid()
            );
        contents
            .TitleInterpretation.Extraction!.Segments.Should()
            .OnlyContain(segment =>
                segment.OccurrenceUuid == null && segment.StartMs == null && segment.EndMs == null
            );
        result
            .EntityKeys.Should()
            .Contain(
                ShadowRepertoireAdapter.KnownEntityKey(
                    firstSong.GetProperty("row").GetProperty("uuid").GetGuid()
                )
            );
    }

    [Test]
    public void MissingShowContextDoesNotWithholdTheFileOrInventAnEarlyLateAssignment()
    {
        var file = Guid.NewGuid();
        var row = JsonSerializer.SerializeToElement(
            new
            {
                kind = "track_sample",
                row = new
                {
                    id = 9,
                    uuid = file,
                    artist_id = 1,
                    source_id = 999,
                    title = "A Song",
                    mp3_url = "https://example.test/live.mp3",
                },
                source_context = (object?)null,
                show_context = (object?)null,
                set_context = (object?)null,
            }
        );
        var adapter = Observe(row);
        var results = adapter.Process(Input(row, Extract(row))).ToArray();
        var performance = results.Single(value => value.Domain == "performances");
        var content = (ShadowFileContents)
            results.Single(value => value.Domain == "file_contents").Proposal!;

        performance.Outcome.Should().Be("unresolved");
        ((ShadowPerformanceHome)performance.Proposal!).ShowUuid.Should().BeNull();
        ((ShadowPerformanceHome)performance.Proposal!).PerformanceType.Should().Be("unknown");
        content.Playback.FileUuid.Should().Be(file);
        content.Playback.Mp3Url.Should().Be("https://example.test/live.mp3");
        content.TitleInterpretation!.Segments.Single().Song!.Basis.Should().Be("title_derived");
        results.Single(value => value.Domain == "file_contents").Outcome.Should().Be("proposed");
    }

    [Test]
    public void SourceRelationshipRetainsUnknownDateAndTheObservedShowUuid()
    {
        var show = Guid.NewGuid();
        var source = Guid.NewGuid();
        var track = Track(11, source, 101, show, "unknown", 1, "Set", "A Song", "1970-xx-xx");
        var record = JsonSerializer.SerializeToElement(
            new { kind = "source_sample", row = track.GetProperty("source_context") }
        );
        var adapter = Observe(track, record);

        var result = adapter.Process(Input(record)).Single();
        var home = (ShadowPerformanceHome)result.Proposal!;

        result.Outcome.Should().Be("proposed");
        home.ShowUuid.Should().Be(show);
        home.SourceUuid.Should().Be(source);
        home.LiteralDate.Should().Be("1970-xx-xx");
        home.PerformanceType.Should().Be("unknown");
        result
            .EntityKeys.Should()
            .Contain("source:" + source.ToString("D"))
            .And.Contain("show:" + show.ToString("D"));
    }

    [Test]
    public void AnotherFilesTitleProofFailsOnlyFileInterpretationAndKeepsPlayback()
    {
        var source = Guid.NewGuid();
        var show = Guid.NewGuid();
        var row = Track(11, source, 101, show, "unknown", 1, "Set", "A Song");
        var anotherFile = Track(11, source, 101, show, "unknown", 1, "Set", "A Song");
        var adapter = Observe(row);

        var results = adapter.Process(Input(row, Extract(anotherFile))).ToArray();
        var contents = results.Single(value => value.Domain == "file_contents");

        results.Single(value => value.Domain == "performances").Outcome.Should().Be("proposed");
        contents.Outcome.Should().Be("failed");
        contents.Reason.Should().Be("invalid_title_extraction");
        ((ShadowFileContents)contents.Proposal!)
            .Playback.FileUuid.Should()
            .Be(row.GetProperty("row").GetProperty("uuid").GetGuid());
        ((ShadowFileContents)contents.Proposal!)
            .Playback.Mp3Url.Should()
            .Be("https://example.test/live.mp3");
    }

    [Test]
    public void FirstPassAcceptsRawRowsAndActualExtractionDtosWithoutReplacingCatalogContext()
    {
        var source = Guid.NewGuid();
        var show = Guid.NewGuid();
        var track = Track(11, source, 101, show, "unknown", 1, "Set", "A Song");
        var rawSource = JsonSerializer.SerializeToElement(
            new { kind = "source_sample", row = track.GetProperty("source_context") }
        );
        var sourceExtraction = JsonSerializer.SerializeToElement(
            new NormalizationBatchExtraction(
                "source_sample",
                11,
                source,
                Artist.Id,
                Artist.Uuid,
                null,
                new Dictionary<string, string?>(),
                null,
                new([], []),
                null
            ),
            Json
        );

        var adapter = Observe(rawSource, track, sourceExtraction, Extract(track));
        var result = adapter
            .Process(Input(track, Extract(track)))
            .Single(value => value.Domain == "performances");

        result.Outcome.Should().Be("proposed");
        ((ShadowPerformanceHome)result.Proposal!).SourceUuid.Should().Be(source);
        ((ShadowPerformanceHome)result.Proposal!).ShowUuid.Should().Be(show);
    }

    [Test]
    public void ConflictingSharedIndexesCannotChangeAFileLocalPlaybackOwner()
    {
        var firstSource = Guid.NewGuid();
        var firstShow = Guid.NewGuid();
        var first = Track(11, firstSource, 101, firstShow, "early", 1, "Set", "A Song");
        var conflicting = Track(
            11,
            Guid.NewGuid(),
            101,
            Guid.NewGuid(),
            "late",
            2,
            "Set",
            "A Song"
        );
        var adapter = Observe(first, conflicting);

        var results = adapter.Process(Input(first, Extract(first))).ToArray();
        var home = (ShadowPerformanceHome)
            results.Single(value => value.Domain == "performances").Proposal!;
        var file = (ShadowFileContents)
            results.Single(value => value.Domain == "file_contents").Proposal!;

        results.Single(value => value.Domain == "performances").Outcome.Should().Be("unresolved");
        home.Unresolved.Should()
            .Contain("conflicting_source_context")
            .And.Contain("conflicting_show_context");
        file.Playback.SourceUuid.Should().Be(firstSource);
        file.Playback.ShowUuid.Should().Be(firstShow);
        file.Playback.FileUuid.Should().Be(first.GetProperty("row").GetProperty("uuid").GetGuid());
    }

    [Test]
    public void MalformedTitleWrapperFailsOnlyFileInterpretationAndRetainsItsAudio()
    {
        var track = Track(11, Guid.NewGuid(), 101, Guid.NewGuid(), "unknown", 1, "Set", "A Song");
        var malformed = JsonSerializer.SerializeToElement(
            new { titleExtraction = new TrackTitleExtractor().Extract(Artist.Uuid, "A Song") },
            Json
        );
        var adapter = Observe(track);

        var results = adapter.Process(Input(track, malformed)).ToArray();

        results.Single(value => value.Domain == "performances").Outcome.Should().Be("proposed");
        var file = results.Single(value => value.Domain == "file_contents");
        file.Outcome.Should().Be("failed");
        file.Reason.Should().Be("invalid_title_extraction");
        ((ShadowFileContents)file.Proposal!)
            .Playback.Mp3Url.Should()
            .Be("https://example.test/live.mp3");
    }

    [Test]
    public void LegacyMembershipsRemainUnorderedAndNeverBecomeOccurrences()
    {
        var song = Song("Repeated Song").GetProperty("row");
        var row = JsonSerializer.SerializeToElement(
            new
            {
                kind = "setlist_sample",
                row = new
                {
                    id = 5,
                    uuid = Guid.NewGuid(),
                    artist_id = 1,
                    date = "1970-xx-xx",
                },
                unordered_memberships = new[]
                {
                    new { song_id = 8, song },
                    new { song_id = 8, song },
                },
            }
        );
        var adapter = Observe(row);
        var result = adapter.Process(Input(row)).Single(value => value.Domain == "setlists");
        var account = (ShadowSetlistAccount)result.Proposal!;

        result.Outcome.Should().Be("unresolved");
        account.Ordered.Should().BeFalse();
        account.Memberships.Should().HaveCount(2);
        account
            .Memberships.Should()
            .OnlyContain(member => member.LegacySongUuid == song.GetProperty("uuid").GetGuid());
        account.Occurrences.Should().BeEmpty();
        account.ShowUuid.Should().BeNull();
    }

    [Test]
    public void ActualProviderDecoderKeepsRepeatedOrderedPassagesAndSetBoundaries()
    {
        var revision = ProviderInput();
        var record = JsonSerializer.SerializeToElement(
            new
            {
                kind = "provider_input",
                provider = "setlist.fm",
                row = new
                {
                    uuid = revision.Uuid,
                    artist_id = Artist.Id,
                    artist_uuid = Artist.Uuid,
                },
                input = revision,
            },
            Json
        );
        var adapter = Observe(record);
        var results = adapter.Process(Input(record)).ToArray();
        var result = results.Single(value => value.Domain == "setlists");
        var account = ((ShadowSetlistAccount[])result.Proposal!).Single();

        result.Outcome.Should().Be("proposed");
        account.Ordered.Should().BeTrue();
        account
            .Occurrences.Select(value => value.Title)
            .Should()
            .Equal("A Song", "A Song", "Another Song");
        account.Occurrences.Select(value => value.Ordinal).Should().Equal(1, 2, 3);
        account.Occurrences.Select(value => value.SetOrdinal).Should().Equal(1, 1, 2);
        account.Occurrences.Select(value => value.EntityKey).Should().OnlyHaveUniqueItems();
        account
            .Occurrences.Take(2)
            .Select(value => value.SongEntityKey)
            .Distinct()
            .Should()
            .ContainSingle();
        account.Occurrences.Should().OnlyContain(value => value.SongInterpretation.Song != null);
        account.ProviderAccount!.Sets.Should().HaveCount(2);
        account
            .Occurrences.Should()
            .OnlyContain(value =>
                value.Observation.Title.Evidence.InputRevisionUuid == revision.Uuid
            );
        account.ShowUuid.Should().BeNull();
        results.Single(value => value.Domain == "performances").Outcome.Should().Be("unresolved");
    }

    [Test]
    public void UnsupportedRetainedCaptureFailsBothDomainsWithoutInventingAccounts()
    {
        var revision = ProviderInput("unsupported-capture-v1");
        var record = JsonSerializer.SerializeToElement(
            new
            {
                kind = "provider_input",
                row = new { uuid = revision.Uuid, artist_id = 1 },
                input = revision,
            },
            Json
        );
        var results = new ShadowPerformanceAdapter().Process(Input(record)).ToArray();
        results
            .Select(result => result.Domain)
            .Should()
            .BeEquivalentTo(["performances", "setlists"]);
        results
            .Should()
            .OnlyContain(result =>
                result.Outcome == "failed"
                && result.Reason == "unsupported_provider_capture"
                && result.Proposal == null
            );
    }

    private static ShadowPerformanceAdapter Observe(params JsonElement[] records)
    {
        var adapter = new ShadowPerformanceAdapter();
        foreach (var record in records)
        {
            adapter.Observe(record);
        }
        return adapter;
    }

    private static ShadowCatalogInput Input(JsonElement record, JsonElement? extraction = null) =>
        new(record, extraction, Artist, "phish.in");

    private static JsonElement Song(string name) =>
        JsonSerializer.SerializeToElement(
            new
            {
                kind = "song_sample",
                row = new
                {
                    id = 8,
                    uuid = Guid.NewGuid(),
                    artist_id = 1,
                    name,
                },
            }
        );

    private static JsonElement Track(
        long sourceId,
        Guid source,
        long showId,
        Guid show,
        string performanceType,
        long setId,
        string setName,
        string title,
        string date = "1970-02-03"
    ) =>
        JsonSerializer.SerializeToElement(
            new
            {
                kind = "track_sample",
                row = new
                {
                    id = setId,
                    uuid = Guid.NewGuid(),
                    artist_id = 1,
                    source_id = sourceId,
                    title,
                    mp3_url = "https://example.test/live.mp3",
                },
                source_context = new
                {
                    id = sourceId,
                    uuid = source,
                    artist_id = 1,
                    show_id = showId,
                    display_date = date,
                },
                show_context = new
                {
                    id = showId,
                    uuid = show,
                    artist_id = 1,
                    display_date = date,
                    performance_type = performanceType,
                },
                set_context = new
                {
                    id = setId,
                    uuid = Guid.NewGuid(),
                    source_id = sourceId,
                    index = setId,
                    name = setName,
                },
            }
        );

    private static JsonElement Extract(JsonElement record)
    {
        var row = record.GetProperty("row");
        var title = row.GetProperty("title").GetString()!;
        return JsonSerializer.SerializeToElement(
            new NormalizationBatchExtraction(
                "track_sample",
                row.GetProperty("id").GetInt64(),
                row.GetProperty("uuid").GetGuid(),
                Artist.Id,
                Artist.Uuid,
                null,
                new Dictionary<string, string?> { ["title"] = title },
                null,
                null,
                new TrackTitleExtractor().Extract(Artist.Uuid, title)
            ),
            Json
        );
    }

    private static NormalizationInputRevision ProviderInput(string version = "setlist-fm-http-v1")
    {
        var payload = Encoding.UTF8.GetBytes(
            """
            {"page":1,"itemsPerPage":20,"total":1,"setlist":[{"id":"account-one","eventDate":"03-02-1970",
              "sets":{"set":[{"name":"First","song":[{"name":"A Song"},{"name":"A Song"}]},
                {"encore":1,"song":[{"name":"Another Song"}]}]}}]}
            """
        );
        return new()
        {
            Uuid = Guid.NewGuid(),
            Resource = new("setlist.fm", "setlist-page", "artist:page=1"),
            CaptureVersion = version,
            MediaType = "application/json",
            Payload = payload,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(payload)),
        };
    }
}
