using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestNormalizationProviderDecoderEnrichment
{
    private readonly NormalizationProviderDecoder decoder = new();

    [Test]
    public void SetlistFmPreservesRepeatedPassagesEmptySetsAndPerformerAnnotations()
    {
        var input = Input(
            "setlist.fm",
            "setlist-page",
            "artist-id:page=1",
            "setlist-fm-http-v1",
            """
            {"page":1,"itemsPerPage":20,"total":1,"setlist":[{
              "id":"setlist-one","eventDate":"03-02-1970",
              "artist":{"mbid":"artist-id","name":"Band"},
              "venue":{"id":"venue-id","name":"Hall","city":{"name":"Town","state":"Region","country":{"name":"Country"},"coords":{"lat":42.1,"long":-72.3}}},
              "sets":{"set":[{"name":"First","song":[
                {"name":"Song","cover":{"mbid":"cover-id","name":"Cover performer"},"with":{"mbid":"guest-id","name":"Guest"},"info":"tease"},
                {"name":"Song"}]},{"encore":1,"song":[]},{"song":[{"name":"Reprise"}]}]},"info":"Show notes"
            }]}
            """
        );
        var result = decoder.Decode(input);
        var setlist = result.Setlists.Should().ContainSingle().Subject;
        setlist.Origin.Scope.Should().Be(ProviderObservationScope.PageMember);
        setlist
            .Origin.Item.Should()
            .Be(new NormalizationResourceKey("setlist.fm", "setlist", "setlist-one"));
        setlist.LiteralDate!.Value.Should().Be("03-02-1970");
        setlist
            .Performers.Single()
            .Identities.Single()
            .Identity.Should()
            .Be(new NormalizationResourceKey("musicbrainz.org", "artist", "artist-id"));
        setlist.Sets.Select(s => s.Ordinal).Should().Equal(1, 2, 3);
        setlist.Sets[1].EncoreNumber!.Value.Should().Be(1);
        setlist.Sets[2].Label.Should().BeNull();
        setlist.Passages.Select(p => p.Title.Value).Should().Equal("Song", "Song", "Reprise");
        setlist.Passages.Select(p => p.SetOrdinal).Should().Equal(1, 1, 3);
        setlist.Passages.Select(p => p.Ordinal).Should().Equal(1, 2, 3);
        var annotations = setlist.Passages[0].PerformerAnnotations;
        annotations.Select(a => a.Kind).Should().Equal("cover_artist", "guest_artist");
        annotations[0].Name!.Value.Should().Be("Cover performer");
        annotations[0].Identities.Single().Identity.ProviderKey.Should().Be("cover-id");
        setlist.Passages[0].Annotations.Single().Text.Value.Should().Be("tease");
        result.Repertoire.Should().BeEmpty();
        setlist.Venue!.CoordinateKind.Should().Be(ObservedCoordinateKind.City);
        setlist.Venue.Latitude!.Value.Should().Be(42.1);
        setlist.Venue.City!.Value.Should().Be("Town");
        AssertEvidence(input, result);
    }

    [Test]
    public void SetlistFmSingletonObjectsKeepTheirExactPointers()
    {
        var input = Input(
            "setlist.fm",
            "setlist-page",
            "artist:page=2",
            "setlist-fm-http-v1",
            """
            {"page":2,"itemsPerPage":1,"total":2,"setlist":{"id":"one","sets":{"set":{"song":{"name":"Song"}}}}}
            """
        );
        var result = decoder.Decode(input);
        result
            .Setlists.Single()
            .Passages.Single()
            .Title.Evidence.JsonPointer.Should()
            .Be("/setlist/sets/set/song/name");
        AssertEvidence(input, result);
    }

    [TestCase("{\"page\":2,\"itemsPerPage\":20,\"total\":0,\"setlist\":[]}")]
    [TestCase("{\"page\":1,\"itemsPerPage\":0,\"total\":0,\"setlist\":[]}")]
    [TestCase("{\"error\":\"Unavailable\"}")]
    public void SetlistFmRejectsInvalidOrMismatchedPages(string json)
    {
        var input = Input(
            "setlist.fm",
            "setlist-page",
            "artist:page=1",
            "setlist-fm-http-v1",
            json
        );
        var decode = () => decoder.Decode(input);
        decode.Should().Throw<InvalidDataException>();
    }

    [Test]
    public void PhantasyShowPreservesScopedSongsSegueFlagsFootnotesAndCreditProse()
    {
        var input = PhantasyShow("4");
        var result = decoder.Decode(input);
        var show = result.Setlists.Should().ContainSingle().Subject;
        show.Origin.Scope.Should().Be(ProviderObservationScope.ResourceItem);
        show.LiteralDate!.Value.Should().Be("1970-02-03T20:00:00-05:00");
        show.Sets.Should().HaveCount(2);
        show.Sets[0]
            .Identity!.Identity.Should()
            .Be(new NormalizationResourceKey("phantasytour.com", "show_set", "[\"4\",\"1\"]"));
        show.Passages.Select(p => p.Title.Value).Should().Equal("Song", "Song");
        show.Passages.Select(p => p.Segue!.Value).Should().Equal(true, false);
        show.Passages[0]
            .SongIdentities.Single()
            .Identity.Should()
            .Be(new NormalizationResourceKey("phantasytour.com", "band_song", "[\"7\",\"9\"]"));
        show.Passages.Should().OnlyContain(p => p.OccurrenceIdentity == null);
        show.Passages[0].FootnoteNumbers.Select(n => n.Value).Should().Equal(2, 99);
        show.Passages[0].Annotations.Single().Text.Value.Should().Be("Guest guitarist");
        result.Issues.Should().ContainSingle(i => i.Code == "unresolved_footnote");
        show.Text.Where(t => t.Field == "credits")
            .Select(t => t.Text.Value)
            .Should()
            .Equal("Compiled by Someone", "Editorial credit");
        show.Text.Should()
            .Contain(t => t.Field == "footnote" && t.Text.Value == "Unreferenced note");
        result.Repertoire.Should().BeEmpty();
        AssertEvidence(input, result);
    }

    [Test]
    public void PhantasyShowCannotBeDecodedUnderAnotherResourceIdentity()
    {
        var decode = () => decoder.Decode(PhantasyShow("5"));
        decode.Should().Throw<InvalidDataException>();
    }

    [TestCase("0004")]
    [TestCase("+4")]
    [TestCase(" 4 ")]
    public void PhantasyStringFormIdsMatchTheWritersTypedIdentityAndRetainTheirWording(
        string providerId
    )
    {
        var original = PhantasyShow("4");
        var json = Encoding
            .UTF8.GetString(original.Payload)
            .Replace(
                "\"id\":4,",
                "\"id\":" + JsonSerializer.Serialize(providerId) + ",",
                StringComparison.Ordinal
            );
        var input = Input("phantasytour.com", "show", "4", "phantasy-tour-http-v1", json);
        Newtonsoft
            .Json.JsonConvert.DeserializeObject<Relisten.Vendor.PhantasyTour.PhantasyTourEnvelope>(
                json
            )!
            .data.id.Should()
            .Be(4);

        var result = decoder.Decode(input);

        result.Setlists.Single().Origin.Item.ProviderKey.Should().Be(providerId);
        using var document = NormalizationRetainedDocument.Open(input);
        document.Resolve("/data/id").GetString().Should().Be(providerId);
        AssertEvidence(input, result);
    }

    [Test]
    public void MissingPhantasyBandPreservesTitleWithoutInventingSongIdentity()
    {
        var input = Input(
            "phantasytour.com",
            "show",
            "4",
            "phantasy-tour-http-v1",
            """
            {"data":{"id":4,"dateTime":"1970-XX-XX","sets":[{"songs":[{"id":9,"name":"Song","segue":false}]}]}}
            """
        );
        var result = decoder.Decode(input);
        var passage = result.Setlists.Single().Passages.Single();
        passage.Title.Value.Should().Be("Song");
        passage.SongIdentities.Should().BeEmpty();
        passage.Segue!.Value.Should().BeFalse();
        result.Issues.Should().ContainSingle(i => i.Code == "song_identity_requires_performer");
        result.Setlists.Single().LiteralDate!.Value.Should().Be("1970-XX-XX");
        AssertEvidence(input, result);
    }

    [Test]
    public void PhantasyListingIsPageScopedAndDoesNotInventAnOrderedSetlist()
    {
        var input = Input(
            "phantasytour.com",
            "show-list",
            "7:page=1:page_size=100",
            "phantasy-tour-http-v1",
            """
            [{"id":4,"dateTime":"1970-02-03T20:00:00-05:00","band":{"id":7,"name":"Band"},"venue":{"id":8,"name":"Hall","city":"Town","state":"Region","country":"Country"}},{}]
            """
        );
        var result = decoder.Decode(input);
        var show = result.Setlists.Should().ContainSingle().Subject;
        show.Origin.Scope.Should().Be(ProviderObservationScope.PageMember);
        show.Ordered.Should().BeFalse();
        show.Passages.Should().BeEmpty();
        show.Venue!.City!.Evidence.JsonPointer.Should().Be("/0/venue/city");
        result.Issues.Should().ContainSingle(i => i.Code == "missing_show_identity");
        AssertEvidence(input, result);
    }

    [TestCase("setlist.fm")]
    [TestCase("phantasytour.com")]
    public void MalformedPassageRetainsTheOriginalOrdinalGapAcrossSets(string provider)
    {
        var input =
            provider == "setlist.fm"
                ? Input(
                    provider,
                    "setlist-page",
                    "artist:page=1",
                    "setlist-fm-http-v1",
                    """
                    {"page":1,"itemsPerPage":20,"total":1,"setlist":[{"id":"one","sets":{"set":[{"song":[{"name":"First"},{}]},{"song":[{"name":"Third"}]}]}}]}
                    """
                )
                : Input(
                    provider,
                    "show",
                    "4",
                    "phantasy-tour-http-v1",
                    """
                    {"data":{"id":4,"sets":[{"songs":[{"name":"First"},{}]},{"songs":[{"name":"Third"}]}]}}
                    """
                );
        var result = decoder.Decode(input);
        var setlist = result.Setlists.Single();
        setlist.Passages.Select(p => p.Ordinal).Should().Equal(1, 3);
        setlist.Passages.Select(p => p.SetOrdinal).Should().Equal(1, 2);
        result.Issues.Should().ContainSingle(i => i.Code == "missing_passage_title");
        AssertEvidence(input, result);
    }

    [TestCase("{\"set\":5}", "invalid_sets", "/setlist/0/sets/set")]
    [TestCase("{\"set\":{\"song\":5}}", "invalid_set_songs", "/setlist/0/sets/set/song")]
    [TestCase("{\"set\":null}", "invalid_sets", "/setlist/0/sets/set")]
    public void SetlistFmMalformedContainersAreCitedInsteadOfBecomingCleanEmptyAccounts(
        string sets,
        string issue,
        string pointer
    )
    {
        var input = Input(
            "setlist.fm",
            "setlist-page",
            "artist:page=1",
            "setlist-fm-http-v1",
            $$"""
            {"page":1,"itemsPerPage":20,"total":1,"setlist":[{"id":"one","sets":{{sets}}}]}
            """
        );
        var result = decoder.Decode(input);
        result.Setlists.Single().Passages.Should().BeEmpty();
        result
            .Issues.Should()
            .ContainSingle(i => i.Code == issue && i.Evidence.JsonPointer == pointer);
        AssertEvidence(input, result);
    }

    [TestCase("{\"id\":4}", "missing_sets", "/data")]
    [TestCase("{\"id\":4,\"sets\":null}", "invalid_sets", "/data/sets")]
    [TestCase("{\"id\":4,\"sets\":5}", "invalid_sets", "/data/sets")]
    public void PhantasyMalformedContainersAreCitedInsteadOfBecomingCleanEmptyAccounts(
        string show,
        string issue,
        string pointer
    )
    {
        var input = Input(
            "phantasytour.com",
            "show",
            "4",
            "phantasy-tour-http-v1",
            "{\"data\":" + show + "}"
        );
        var result = decoder.Decode(input);
        result.Setlists.Single().Passages.Should().BeEmpty();
        result
            .Issues.Should()
            .ContainSingle(i => i.Code == issue && i.Evidence.JsonPointer == pointer);
        AssertEvidence(input, result);
    }

    [TestCase("setlist.fm")]
    [TestCase("phantasytour.com")]
    public void ExplicitEmptySetsRemainValidEmptyAccounts(string provider)
    {
        var input =
            provider == "setlist.fm"
                ? Input(
                    provider,
                    "setlist-page",
                    "artist:page=1",
                    "setlist-fm-http-v1",
                    """
                    {"page":1,"itemsPerPage":20,"total":1,"setlist":[{"id":"one","sets":{"set":[]}}]}
                    """
                )
                : Input(
                    provider,
                    "show",
                    "4",
                    "phantasy-tour-http-v1",
                    """
                    {"data":{"id":4,"sets":[]}}
                    """
                );
        var result = decoder.Decode(input);
        result.Setlists.Single().Passages.Should().BeEmpty();
        result.Issues.Should().BeEmpty();
        AssertEvidence(input, result);
    }

    [Test]
    public void SetlistFmKeepsTapedAndLiveEntriesDistinctWithoutDroppingEither()
    {
        var input = Input(
            "setlist.fm",
            "setlist-page",
            "artist:page=1",
            "setlist-fm-http-v1",
            """
            {"page":1,"itemsPerPage":20,"total":1,"setlist":[{"id":"one","sets":{"set":[{"song":[{"name":"Song","tape":true},{"name":"Song","tape":false}]}]}}]}
            """
        );
        var result = decoder.Decode(input);
        var passages = result.Setlists.Single().Passages;
        passages.Select(p => p.Title.Value).Should().Equal("Song", "Song");
        passages.Select(p => p.Tape!.Value).Should().Equal(true, false);
        passages
            .Select(p => p.Tape!.Evidence.JsonPointer)
            .Should()
            .Equal("/setlist/0/sets/set/0/song/0/tape", "/setlist/0/sets/set/0/song/1/tape");
        result.Repertoire.Should().BeEmpty();
        AssertEvidence(input, result);
    }

    [Test]
    public void PhantasyMalformedAnnotationsAreCitedWithoutDroppingValidNeighbors()
    {
        var input = Input(
            "phantasytour.com",
            "show",
            "4",
            "phantasy-tour-http-v1",
            """
            {"data":{"id":4,"sets":[{"songs":[{"name":"First","segue":42,"footnotes":true},{"name":"Second","segue":false,"footnotes":[]}]}]}}
            """
        );
        var result = decoder.Decode(input);
        result
            .Setlists.Single()
            .Passages.Select(p => p.Title.Value)
            .Should()
            .Equal("First", "Second");
        result.Setlists.Single().Passages[1].Segue!.Value.Should().BeFalse();
        result
            .Issues.Select(i => (i.Code, i.Evidence.JsonPointer))
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    ("invalid_segue", "/data/sets/0/songs/0/segue"),
                    ("invalid_footnotes", "/data/sets/0/songs/0/footnotes"),
                }
            );
        AssertEvidence(input, result);
    }

    private static NormalizationInputRevision PhantasyShow(string resourceId) =>
        Input(
            "phantasytour.com",
            "show",
            resourceId,
            "phantasy-tour-http-v1",
            """
            {"data":{"id":4,"dateTime":"1970-02-03T20:00:00-05:00","band":{"id":7,"name":"Band"},
              "credits":["Compiled by Someone",{"name":"Editorial credit"}],"prenotes":"Opening notes",
              "footnotes":[{"id":2,"note":"Guest guitarist"},{"id":3,"note":"Unreferenced note"}],
              "sets":[{"id":1,"name":"First","songs":[{"id":9,"name":"Song","segue":true,"footnotes":[2,99]},{"id":9,"name":"Song","segue":false}]},{"id":2,"songs":[]}]}}
            """
        );

    private static NormalizationInputRevision Input(
        string provider,
        string type,
        string key,
        string version,
        string json
    ) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Resource = new(provider, type, key),
            CaptureVersion = version,
            Payload = Encoding.UTF8.GetBytes(json),
            MediaType = "application/json",
        };

    // Inspect the public DTO recursively so every nested annotation and issue must
    // point to a value in the retained response, including singleton objects.
    private static void AssertEvidence(
        NormalizationInputRevision input,
        ProviderDecodeResult result
    )
    {
        using var document = NormalizationRetainedDocument.Open(input);
        void Check(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (
                    value.TryGetProperty("InputRevisionUuid", out var revision)
                    && value.TryGetProperty("JsonPointer", out var pointer)
                )
                {
                    document.ValidateEvidence(new(revision.GetGuid(), pointer.GetString()!));
                }
                foreach (var property in value.EnumerateObject())
                {
                    Check(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in value.EnumerateArray())
                {
                    Check(entry);
                }
            }
        }
        Check(JsonSerializer.SerializeToElement(result));
    }
}
