using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Relisten.Search;

namespace RelistenApiTests.Search;

public sealed partial class TestCatalogSearchHttp
{
    [Test]
    public async Task ADisplayMissPreservesTheOtherShowAndReportsPartial()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            SearchPath + "?q=renderable%20probe"
        );
        var response = await ReadSearch(request);

        ((string?)response["availability"]!["catalog"]).Should().Be("partial");
        Results(response)
            .Select(row => (string?)row?["show"]?["id"])
            .Should()
            .Equal(Id("show", 18804));
    }

    [Test]
    public async Task OnlyTheSelectedArtistIsAnAppliedArtistConstraint()
    {
        const string text = "?q=matched%20artist";
        var explore = await GetSearch(text);
        var applied = explore["readings"]!["applied"]!;
        ((string?)applied["id"]).Should().Be("query");
        applied["chips"]!.AsArray().Should().NotContain(chip => (string?)chip!["key"] == "artist");

        var artist = Id("artist", 14);
        var choice = $"artist:{artist}";
        explore["readings"]!["alternatives"]!
            .AsArray()
            .Should()
            .Contain(reading => (string?)reading!["id"] == choice);
        var selected = await GetSearch(text + $"&reading_id={choice}");
        ((string?)selected["readings"]!["applied"]!["id"]).Should().Be(choice);
        var chip = selected["readings"]!["applied"]!["chips"]!
            .AsArray()
            .Single(chip => (string?)chip!["key"] == "artist");
        ((string?)chip!["value"]).Should().Be(artist);
        Results(selected)
            .Should()
            .NotBeEmpty()
            .And.OnlyContain(row => (string?)row!["artist"]!["id"] == artist);
        selected["readings"]!["alternatives"]!
            .AsArray()
            .Should()
            .NotContain(reading => (string?)reading!["id"] == choice);
    }

    [TestCase("1995", "year", "1995")]
    [TestCase("1995-12-31", "date", "1995-12-31")]
    public async Task ScopedCalendarAndImpliedArtistAreAppliedConstraints(
        string calendar,
        string key,
        string value
    )
    {
        var artist = Id("artist", 14);
        var query =
            key == "date"
                ? $"?q={calendar}&scope=shows&artist_id={artist}"
                : $"?q=matched%20artist%20{calendar}&scope=shows";
        var result = await GetSearch(query);
        AppliedChips(result)
            .Should()
            .Contain(chip => (string?)chip!["key"] == key && (string?)chip["value"] == value);
        AppliedChips(result)
            .Should()
            .Contain(chip => (string?)chip!["key"] == "artist" && (string?)chip["value"] == artist);
        result["readings"]!["alternatives"]!
            .AsArray()
            .Should()
            .NotContain(reading => (string?)reading!["id"] == $"artist:{artist}");
        Results(result)
            .Should()
            .NotBeEmpty()
            .And.OnlyContain(row =>
                (string?)row!["artist"]!["id"] == artist
                && ((string)row["show"]!["display_date"]!).StartsWith(value)
            );
        var exchange = await GetCandidates(query);
        ((string?)exchange["constraints"]!["artist_id"]).Should().Be(artist);
        ((int?)exchange["constraints"]!["date"]).Should().Be(key == "date" ? 19951231 : null);
    }

    [Test]
    public async Task CountryChipIsAppliedOnlyWhenBothReadingsFilterByCountry()
    {
        var fixture = Id("show", 19950);
        var scoped = await GetSearch(JapanShowsQuery);
        AppliedChips(scoped)
            .Should()
            .Contain(chip => (string?)chip!["key"] == "country" && (string?)chip["value"] == "JP");
        Results(scoped)
            .Should()
            .NotBeEmpty()
            .And.NotContain(row => (string?)row!["show"]!["id"] == fixture);

        var mixed = await GetSearch("?q=Pagination%20Japan");
        AppliedChips(mixed).Should().NotContain(chip => (string?)chip!["key"] == "country");
        Results(mixed).Should().Contain(row => (string?)row!["show"]!["id"] == fixture);
    }

    private static JsonArray AppliedChips(JsonNode result) =>
        result["readings"]!["applied"]!["chips"]!.AsArray();

    [Test]
    public async Task CapturedCatalogReplyMatchesTheUserServiceFixture()
    {
        var reply = await GetCandidates(
            $"?q=matched%20artist&scope=artists&artist_id={Id("artist", 14)}"
        );
        var candidate = reply["candidates"]!.AsArray().Should().ContainSingle().Subject!;
        var artist = candidate["result"]!["artist"]!.AsObject();
        // Fixture rows are inserted at test time. Keep the presence and shape
        // of both timestamps while normalizing those two clock values.
        foreach (var field in new[] { "created_at", "updated_at" })
        {
            if (artist[field] is JsonValue)
            {
                artist[field] = "2000-01-01T00:00:00Z";
            }
        }

        // Percentile and score depend on the other rows seeded in the index.
        foreach (var field in new[] { "popularity", "score" })
        {
            if (candidate["order"]?[field] is JsonValue)
            {
                candidate["order"]![field] = 0.0;
            }
        }

        if (
            Environment.GetEnvironmentVariable("RELISTEN_SEARCH_FIXTURE_OUTPUT") is
            { Length: > 0 } output
        )
        {
            await File.WriteAllTextAsync(
                output,
                reply.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n"
            );
            Assert.Inconclusive(
                "Catalog reply captured. Run again without the output override to check it."
            );
        }

        var fixture = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Fixtures",
            "catalog-search-artist.json"
        );
        var expected = JsonNode.Parse(await File.ReadAllTextAsync(fixture));
        JsonNode.DeepEquals(reply, expected).Should().BeTrue(reply.ToJsonString());
    }

    [Test]
    public async Task ScopedPlaceReadingPagesOneRecordingSet()
    {
        const string query = "?q=philly&scope=sources";
        var keys = new List<string>();
        int? page = 1;
        int? count = null;
        while (page.HasValue)
        {
            page.Value.Should().BeLessThan(10);
            var result = await GetSearch(query + $"&page={page.Value}");
            var applied = result["readings"]!["applied"]!;
            ((string?)applied["id"]).Should().Be("place:philly");
            applied["chips"]!
                .AsArray()
                .Should()
                .Contain(chip =>
                    (string?)chip!["key"] == "place" && (string?)chip["value"] == "philly"
                );
            var total = (int)result["counts"]!["sources"]!;
            count ??= total;
            total.Should().Be(count.Value);
            Results(result).Should().NotBeEmpty();
            keys.AddRange(Results(result).Select(row => (string)row!["key"]!));
            page = (int?)result["next_page"];
        }

        keys.Should().OnlyHaveUniqueItems();
        keys.Should().HaveCount(count!.Value);
        keys.Should().Contain($"recording:{Id("source", 19000)}");
        keys.Should().NotContain($"recording:{Id("source", 19028)}");
    }

    [Test]
    public async Task ASingleWordPlaceAliasCanMatchTheTextOfAReview()
    {
        var result = await GetSearch("?q=philly&scope=reviews");
        Results(result)
            .Select(row => (string?)row?["review"]?["title"])
            .Should()
            .Contain("Philly Encore");
    }

    [Test]
    public async Task APerformanceWithoutASongUsesThePreviewTrackKey()
    {
        var rows = Results(
            await GetSearch($"?q=unknown%20title&mode=add_tracks&artist_id={Id("artist", 14)}")
        );
        var expectedTrack = Id("track", 46);
        var result = rows.Single(row => (string?)row!["preview_track"]?["id"] == expectedTrack)!;
        ((string?)result["key"]).Should().Be($"performance:track:{expectedTrack}");
        result["performance"].Should().BeNull();
    }

    [Test]
    public async Task SequenceCarriesBothSongPartsAndTheExactTrackOnce()
    {
        var evidenceStore = _app!.Services.GetRequiredService<CatalogQueryEvidenceStore>();
        var previous = evidenceStore.Current;
        var track = Id("track", 44);
        var passage = new SearchPassage(
            "v4-review-4",
            Id("show", 4),
            Id("source", 24),
            "Set 1",
            19951231,
            "Current Arena",
            ["song_00000003", "song_00000004"],
            ["Scarlet Begonias", "Fire on the Mountain"],
            [track],
            [new("song_00000003", 1, 1, track, true), new("song_00000004", 1, 2, track, false)]
        );
        var rule = new SearchReviewedNamedShows.PassageRule(
            passage.ArtistSlug,
            ["scarlet fire"],
            passage.SongTitles
        );
        evidenceStore.Publish(
            new CatalogQueryEvidence(
                previous.Collection,
                previous.Vocabulary,
                new SearchPassages([
                    new(rule, [new(passage, FrozenSet<string>.Empty, "venue_00000001")]),
                ]),
                FrozenDictionary<string, long>.Empty,
                FrozenSet<string>.Empty
            )
        );
        try
        {
            var rows = Results(await GetSearch("?q=scarlet%20fire"));
            var sequence = rows.Single(row => (string?)row!["kind"] == "sequence")!;
            ((string?)sequence["key"]).Should().Be(passage.Key);
            ((string?)sequence["match"]!["reading"]).Should().Be("query");
            ((string?)sequence["passage"]!["source_id"]).Should().Be(passage.SourceId);
            sequence["passage"]!["tracks"]!
                .AsArray()
                .Select(row => (string?)row!["id"])
                .Should()
                .Equal(track);
            sequence["passage"]!["parts"]!
                .AsArray()
                .Select(part => (string?)part!["song"]!["id"])
                .Should()
                .Equal(passage.SongIds);
            var v4Track = JsonNode.Parse(
                await _http.GetStringAsync("/api/v4/source-tracks/" + track)
            )!;
            var v4Parts = v4Track["normalization"]!["parts"]!.AsArray();
            foreach (var part in sequence["passage"]!["parts"]!.AsArray())
            {
                var expected = v4Parts.Single(entry =>
                    (int?)entry!["ordinal"] == (int?)part!["ordinal"]
                );
                JsonNode.DeepEquals(part!["song"], expected!["song"]).Should().BeTrue();
            }

            var pagedFirst = await GetSearch("?q=scarlet%20fire&scope=shows");
            var pagedNext = await GetSearch("?q=scarlet%20fire&scope=shows&page=2");
            Results(pagedFirst).Should().Contain(row => (string?)row!["key"] == passage.Key);
            Results(pagedNext).Should().NotContain(row => (string?)row!["key"] == passage.Key);
        }
        finally
        {
            evidenceStore.Publish(previous);
        }
    }

    [Test]
    public async Task AFileContainingTwoSongsHasOneSearchRowAndBothSongNames()
    {
        var track = Id("track", 44);
        var result = await GetSearch(
            $"?q=medley%20fixture&mode=add_tracks&artist_id={Id("artist", 14)}"
        );
        var row = Results(result)
            .Where(row => (string?)row!["preview_track"]?["id"] == track)
            .Should()
            .ContainSingle()
            .Subject!;
        var v4 = JsonNode.Parse(await _http.GetStringAsync("/api/v4/source-tracks/" + track))!;
        var songs = v4["normalization"]!["parts"]!.AsArray().Select(part => part!["song"]);
        row["songs"]!
            .AsArray()
            .Select(node => (string?)node!["name"])
            .Should()
            .Equal(songs.Select(song => (string?)song!["name"]));
        ((string?)row["key"]).Should().Be($"performance:track:{track}");
    }

    [Test]
    public async Task AShowImportedAfterTheLastRefreshHasNullSearchPopularity()
    {
        var result = await GetSearch("?q=1996-03-15&scope=shows");
        var show = Results(result)
            .Single(row => (string?)row!["show"]?["id"] == Id("show", 19944))!;
        show.AsObject().ContainsKey("popularity").Should().BeTrue();
        show["popularity"].Should().BeNull();
        show["show"]!["popularity"].Should().BeNull();
    }

    [TestCase("matched artist", "artists", "artist", "artists", 14)]
    [TestCase("current arena", "venues", "venue", "venues", 0)]
    [TestCase("winter tour", "shows", "tour", "tours", 84)]
    [TestCase("scarlet begonias", "songs", "song", "songs", 0)]
    [TestCase("synthetic review 4", "sources", "source", "sources", 24)]
    [TestCase("1995-12-31", "shows", "show", "shows", 4)]
    [TestCase("remarkable encore", "reviews", "review", "reviews", 0)]
    [TestCase("unknown title", "all", "preview_track", "source-tracks", 46)]
    public async Task SearchEmbedsTheCorrespondingV4Resource(
        string query,
        string scope,
        string field,
        string endpoint,
        int suffix
    )
    {
        var response = await GetSearch(
            $"?q={Uri.EscapeDataString(query)}&scope={scope}&artist_id={Id("artist", 14)}"
                + (field == "preview_track" ? "&mode=add_tracks" : "")
        );
        var id = field switch
        {
            "song" => "song_00000003",
            "venue" => "venue_00000001",
            "review" => Relisten.Identifiers.UuidTypeId.Format("source_review", _review),
            "preview_track" => Id("track", suffix),
            _ => Id(field, suffix),
        };
        var resource = Results(response)
            .Select(row => row?[field])
            .OfType<JsonObject>()
            .First(row => (string?)row["id"] == id);
        var path =
            endpoint == "reviews"
                ? $"/api/v4/sources/{Id("source", 24)}/reviews"
                : $"/api/v4/{endpoint}/{id}";
        var v4 = JsonNode.Parse(await _http.GetStringAsync(path))!;
        if (field == "review")
        {
            v4 = v4.AsArray().Single(row => (string?)row!["id"] == id)!;
        }

        var detailOnly = field switch
        {
            "venue" => new[] { "shows" },
            "tour" => ["shows"],
            "song" => ["shows"],
            "source" => ["sets", "links"],
            "show" => ["sources"],
            _ => [],
        };
        resource
            .Select(property => property.Key)
            .Should()
            .BeEquivalentTo(v4.AsObject().Select(property => property.Key).Except(detailOnly));

        foreach (var (property, value) in resource)
        {
            // The search fixture pins source ratings in its build snapshot.
            if (field == "source" && property is ("avg_rating" or "num_reviews"))
            {
                continue;
            }
            JsonNode
                .DeepEquals(value, v4[property])
                .Should()
                .BeTrue($"{field}.{property} comes from v4");
        }
    }
}
