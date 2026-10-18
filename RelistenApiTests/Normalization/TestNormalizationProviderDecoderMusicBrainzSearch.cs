using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestNormalizationProviderDecoderMusicBrainzSearch
{
    private const string FirstId = "6671bffb-f9cc-3ea9-96e4-91cdc8d746a6";
    private const string SecondId = "6394bda2-57a6-3578-badc-f3043d4f0ba8";
    private const string ArtistId = "592a3b6d-c42b-4567-99c9-ecf63bd66499";
    private readonly NormalizationProviderDecoder decoder = new();

    [Test]
    public void SameTitleAlternativesAndDuplicateHitsRetainOrderAndExactPageCitations()
    {
        var input = Synthetic(
            "work",
            $$"""
            {"created":"2026-10-17T12:34:56.000Z","count":53,"offset":50,"works":[
              {"id":"{{FirstId}}","title":"Same title","disambiguation":"First work","type":"Song","score":100},
              {"id":"{{SecondId}}","title":"Same title","disambiguation":"Unrelated work","score":100},
              {"id":"{{FirstId}}","title":"Same title","score":80}]}
            """
        );
        var result = decoder.Decode(input);
        var page = result.MusicBrainzSearchPages.Should().ContainSingle().Subject;

        result.DecoderVersion.Should().Be("provider-observations-v5");
        page.Origin.Item.Should().Be(input.Resource);
        page.Origin.Evidence.Should().Be(new NormalizedEvidenceReference(input.Uuid, ""));
        page.Origin.Scope.Should().Be(ProviderObservationScope.ResourceItem);
        page.Created!.Value.Should().Be("2026-10-17T12:34:56.000Z");
        page.Count!.Value.Should().Be(53);
        page.Offset!.Value.Should().Be(50);
        page.ExternalUniverseComplete.Should().BeFalse();
        var hits = page.Hits!;
        hits.Select(hit => hit.SourceIndex).Should().Equal(0, 1, 2);
        hits.Select(hit => hit.Entity.Identity.Identity.ProviderKey)
            .Should()
            .Equal(FirstId, SecondId, FirstId);
        hits.Select(hit => hit.Entity.Name!.Value)
            .Should()
            .Equal("Same title", "Same title", "Same title");
        hits.Select(hit => hit.Score!.Value).Should().Equal(100, 100, 80);
        hits.Select(hit => hit.Evidence.JsonPointer)
            .Should()
            .Equal("/works/0", "/works/1", "/works/2");
        hits[0].Entity.Disambiguation!.Value.Should().Be("First work");
        hits[1].Entity.Disambiguation!.Value.Should().Be("Unrelated work");
        hits[0].Entity.Type!.Value.Should().Be("Song");
        hits.Should().OnlyContain(hit => hit.CompleteAuthorship == null);
        page.Issues.Should().BeEmpty();
        AssertSearchOnly(result);
        AssertCitations(input, result);
    }

    [TestCase("work", "works")]
    [TestCase("recording", "recordings")]
    [TestCase("artist", "artists")]
    public void ZeroResultsAreRetainedAsExplicitEmptyPages(string kind, string array)
    {
        var input = Synthetic(kind, "{\"count\":0,\"offset\":0,\"" + array + "\":[]}");
        var result = decoder.Decode(input);
        var page = result.MusicBrainzSearchPages!.Single();

        page.Count!.Value.Should().Be(0);
        page.Offset!.Value.Should().Be(0);
        page.Created.Should().BeNull();
        page.Hits.Should().BeEmpty();
        page.Issues.Should().BeEmpty();
        page.ExternalUniverseComplete.Should().BeFalse();
        AssertSearchOnly(result);
        AssertCitations(input, result);
    }

    [TestCase("{\"count\":0,\"offset\":0}", "missing_search_results", "")]
    [TestCase("{\"count\":0,\"offset\":0,\"works\":null}", "expected_array", "/works")]
    [TestCase("{\"count\":0,\"offset\":0,\"works\":{}}", "expected_array", "/works")]
    [TestCase("{\"count\":0,\"offset\":0,\"works\":\"empty\"}", "expected_array", "/works")]
    public void MissingOrMalformedResultContainersCannotBecomeCleanEmptyPages(
        string json,
        string code,
        string pointer
    )
    {
        var input = Synthetic("work", json);
        var result = decoder.Decode(input);
        var page = result.MusicBrainzSearchPages!.Single();

        page.Hits.Should().BeNull();
        page.Issues.Should()
            .ContainSingle()
            .Which.Should()
            .Be(new ProviderDecodeIssue(code, new(input.Uuid, pointer)));
        page.Issues.Should().Equal(result.Issues);
        AssertCitations(input, result);
    }

    [Test]
    public void MissingAndInvalidPaginationRetainDiagnosticsAlongsideObservedHits()
    {
        var missingInput = Synthetic("work", "{\"works\":[]}");
        var missing = decoder.Decode(missingInput);
        var missingPage = missing.MusicBrainzSearchPages!.Single();
        missingPage.Count.Should().BeNull();
        missingPage.Offset.Should().BeNull();
        missingPage
            .Issues.Select(issue => issue.Code)
            .Should()
            .Equal("missing_search_count", "missing_search_offset");
        missingPage.Hits.Should().BeEmpty();
        AssertCitations(missingInput, missing);

        var invalidInput = Synthetic("work", "{\"count\":-1,\"offset\":\"0\",\"works\":[]}");
        var invalid = decoder.Decode(invalidInput);
        invalid.MusicBrainzSearchPages!.Single().Count.Should().BeNull();
        invalid.MusicBrainzSearchPages!.Single().Offset.Should().BeNull();
        invalid
            .Issues.Select(issue => issue.Evidence.JsonPointer)
            .Should()
            .Equal("/count", "/offset");
        invalid.Issues.Should().OnlyContain(issue => issue.Code == "expected_nonnegative_integer");
        AssertCitations(invalidInput, invalid);
    }

    [TestCase("null")]
    [TestCase("0.5")]
    [TestCase("9223372036854775808")]
    public void NonIntegerOrOutOfRangeCountsAreNotCoerced(string count)
    {
        var input = Synthetic("work", "{\"count\":" + count + ",\"offset\":0,\"works\":[]}");
        var result = decoder.Decode(input);

        result.MusicBrainzSearchPages!.Single().Count.Should().BeNull();
        result
            .Issues.Should()
            .ContainSingle()
            .Which.Code.Should()
            .Be("expected_nonnegative_integer");
        AssertCitations(input, result);
    }

    [Test]
    public void BadMembersAndIdsLeavePositionedPeersWithCitedIssues()
    {
        var input = Synthetic(
            "work",
            $$"""
            {"count":7,"offset":0,"works":[
              {"id":"{{FirstId}}","title":"First"},null,{},
              {"id":"not-an-mbid","title":"Invalid"},
              {"id":"00000000-0000-0000-0000-000000000000"},
              {"id":"{{SecondId}}","title":42,"score":"invalid"},false]}
            """
        );
        var result = decoder.Decode(input);
        var page = result.MusicBrainzSearchPages!.Single();
        var hits = page.Hits!;

        hits.Select(hit => hit.SourceIndex).Should().Equal(0, 5);
        hits[1].Entity.Name.Should().BeNull();
        hits[1].Score.Should().BeNull();
        result
            .Issues.Select(issue => issue.Evidence.JsonPointer)
            .Should()
            .Equal(
                "/works/1",
                "/works/2",
                "/works/3/id",
                "/works/4/id",
                "/works/5/title",
                "/works/5/score",
                "/works/6"
            );
        AssertSearchOnly(result);
        AssertCitations(input, result);
    }

    [Test]
    public void NumericAndDecimalStringScoresHaveEquivalentValuesAndOriginalScalarCitations()
    {
        var input = Synthetic(
            "work",
            $$"""
            {"count":2,"offset":0,"works":[{"id":"{{FirstId}}","score":100},{"id":"{{FirstId}}","score":"100"}]}
            """
        );
        var result = decoder.Decode(input);
        var hits = result.MusicBrainzSearchPages!.Single().Hits!;

        hits.Select(hit => hit.Score!.Value).Should().Equal(100, 100);
        hits.Select(hit => hit.Score!.Evidence.JsonPointer)
            .Should()
            .Equal("/works/0/score", "/works/1/score");
        using var retained = NormalizationRetainedDocument.Open(input);
        retained
            .Resolve(hits[0].Score!.Evidence.JsonPointer)
            .ValueKind.Should()
            .Be(JsonValueKind.Number);
        retained.Resolve(hits[1].Score!.Evidence.JsonPointer).GetString().Should().Be("100");
        result.Issues.Should().BeEmpty();
        AssertSearchOnly(result);
        AssertCitations(input, result);
    }

    [TestCase("\"+100\"")]
    [TestCase("\"100.0\"")]
    [TestCase("-1")]
    [TestCase("null")]
    [TestCase("\"2147483648\"")]
    public void InvalidScoreRepresentationsProduceCitedIssues(string score)
    {
        var input = Synthetic(
            "work",
            "{\"count\":1,\"offset\":0,\"works\":[{\"id\":\""
                + FirstId
                + "\",\"score\":"
                + score
                + "}]}"
        );
        var result = decoder.Decode(input);

        result.MusicBrainzSearchPages!.Single().Hits!.Single().Score.Should().BeNull();
        result
            .Issues.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new ProviderDecodeIssue(
                    "expected_nonnegative_integer",
                    new(input.Uuid, "/works/0/score")
                )
            );
        AssertCitations(input, result);
    }

    [Test]
    public void WorkSummaryRetainsOnlySuppliedAliasesIswcsAndArtistRelationWording()
    {
        var input = Synthetic(
            "work",
            $$"""
            {"count":1,"offset":0,"works":[{"id":"{{FirstId}}","title":"Été (original)",
              "aliases":[{"name":"Summer","sort-name":"Summer","locale":"en","primary":true}],
              "iswcs":["T-070.939.624-6","invalid"],
              "relations":[{"type":"unrecognized role","direction":"backward","target-type":"artist",
                "artist":{"id":"{{ArtistId}}","name":"Original artist","type":"Group"},"unfamiliar":"retained"},5]}]}
            """
        );
        var result = decoder.Decode(input);
        var hit = result.MusicBrainzSearchPages!.Single().Hits!.Single();

        var alias = hit.Aliases.Should().ContainSingle().Subject;
        alias.Name!.Value.Should().Be("Summer");
        alias.Name.Evidence.JsonPointer.Should().Be("/works/0/aliases/0/name");
        alias.Primary!.Value.Should().BeTrue();
        hit.Iswcs.Should()
            .ContainSingle()
            .Which.Identity.Identity.Should()
            .Be(new NormalizationResourceKey("iswc", "work", "T0709396246"));
        hit.Iswcs![0].Original.Evidence.JsonPointer.Should().Be("/works/0/iswcs/0");
        var relation = hit.WorkArtists.Should().ContainSingle().Subject;
        relation.Type!.Value.Should().Be("unrecognized role");
        relation.Direction!.Value.Should().Be("backward");
        relation.TargetType!.Value.Should().Be("artist");
        relation
            .Artist!.Identity.Identity.Should()
            .Be(new NormalizationResourceKey("musicbrainz.org", "artist", ArtistId));
        relation.Artist.Type!.Value.Should().Be("Group");
        using var retained = NormalizationRetainedDocument.Open(input);
        retained
            .Resolve(relation.Evidence.JsonPointer)
            .GetProperty("unfamiliar")
            .GetString()
            .Should()
            .Be("retained");
        result
            .Issues.Select(issue => issue.Evidence.JsonPointer)
            .Should()
            .Equal("/works/0/iswcs/1", "/works/0/relations/1");
        hit.CompleteAuthorship.Should().BeNull();
        AssertSearchOnly(result);
        AssertCitations(input, result);
    }

    [Test]
    public void InvalidAndUnknownWorkRelationTargetsRemainUnresolvedWithTheirOriginalCitations()
    {
        var input = Synthetic(
            "work",
            $$$"""
            {"count":1,"offset":0,"works":[{"id":"{{{FirstId}}}","relations":[
              {"type":"composer","target-type":"artist"},
              {"type":"writer","artist":{"id":"invalid","name":"Unresolved"}},
              {"type":"unknown","target-type":"place","place":{"name":"Uninterpreted"}}]}]}
            """
        );
        var result = decoder.Decode(input);
        var relations = result.MusicBrainzSearchPages!.Single().Hits!.Single().WorkArtists!;

        relations.Should().HaveCount(3).And.OnlyContain(relation => relation.Artist == null);
        relations
            .Select(relation => relation.Type!.Value)
            .Should()
            .Equal("composer", "writer", "unknown");
        result
            .Issues.Select(issue => issue.Evidence.JsonPointer)
            .Should()
            .Equal("/works/0/relations/0", "/works/0/relations/1/artist/id");
        using var retained = NormalizationRetainedDocument.Open(input);
        retained
            .Resolve(relations[2].Evidence.JsonPointer)
            .GetProperty("place")
            .GetProperty("name")
            .GetString()
            .Should()
            .Be("Uninterpreted");
        AssertSearchOnly(result);
        AssertCitations(input, result);
    }

    [Test]
    public void ConflictingExplicitTargetKindCannotQualifyAnEmbeddedArtist()
    {
        var input = Synthetic(
            "work",
            $$$"""
            {"count":1,"offset":0,"works":[{"id":"{{{FirstId}}}","relations":[
              {"type":"composer","target-type":"work","artist":{"id":"{{{ArtistId}}}","name":"Conflicting artist"}}]}]}
            """
        );
        var result = decoder.Decode(input);
        var relation = result.MusicBrainzSearchPages!.Single().Hits!.Single().WorkArtists!.Single();

        relation.Artist.Should().BeNull();
        relation.TargetType!.Value.Should().Be("work");
        relation.Evidence.JsonPointer.Should().Be("/works/0/relations/0");
        result
            .Issues.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new ProviderDecodeIssue(
                    "relationship_target_type_mismatch",
                    new(input.Uuid, "/works/0/relations/0/target-type")
                )
            );
        using var retained = NormalizationRetainedDocument.Open(input);
        retained
            .Resolve(relation.Evidence.JsonPointer)
            .GetProperty("artist")
            .GetProperty("id")
            .GetString()
            .Should()
            .Be(ArtistId);
        AssertSearchOnly(result);
        AssertCitations(input, result);
    }

    [Test]
    public void RecordingCreditsRemainExternalPerformersWithExactNestedSupport()
    {
        var input = Synthetic(
            "recording",
            $$$"""
            {"count":1,"offset":0,"recordings":[{"id":"{{{FirstId}}}","title":"Live performance",
              "artist-credit":[{"name":"Credited wording","joinphrase":" & ",
                "artist":{"id":"{{{ArtistId}}}","name":"A group","sort-name":"Group, A","type":"Group"}},
                {"name":"Missing artist"},false]}]}
            """
        );
        var result = decoder.Decode(input);
        var hit = result.MusicBrainzSearchPages!.Single().Hits!.Single();
        var credits = hit.ArtistCredits!;

        credits.Should().HaveCount(2);
        credits[0].Name!.Value.Should().Be("Credited wording");
        credits[0].JoinPhrase!.Value.Should().Be(" & ");
        credits[0].Artist!.SortName!.Value.Should().Be("Group, A");
        credits[0]
            .Artist!.Identity.Evidence.JsonPointer.Should()
            .Be("/recordings/0/artist-credit/0/artist/id");
        credits[1].Artist.Should().BeNull();
        hit.WorkArtists.Should().BeNull();
        hit.Iswcs.Should().BeNull();
        result
            .Issues.Select(issue => issue.Evidence.JsonPointer)
            .Should()
            .Equal("/recordings/0/artist-credit/1", "/recordings/0/artist-credit/2");
        AssertSearchOnly(result);
        AssertCitations(input, result);
    }

    [TestCase("Person")]
    [TestCase("Group")]
    public void ArtistTypesNeverBecomeLocalPeopleEvenWhenUuidMatchesACatalogIdentity(string type)
    {
        var input = Synthetic(
            "artist",
            $$"""
            {"count":1,"offset":0,"artists":[{"id":"{{ArtistId}}","name":"A name","type":"{{type}}",
              "aliases":[{"name":"An alias"}]}]}
            """
        );
        var result = decoder.Decode(input);
        var hit = result.MusicBrainzSearchPages!.Single().Hits!.Single();

        Guid.Parse(hit.Entity.Identity.Identity.ProviderKey).Should().Be(Guid.Parse(ArtistId));
        hit.Entity.Identity.Identity.Should()
            .Be(new NormalizationResourceKey("musicbrainz.org", "artist", ArtistId));
        hit.Entity.Type!.Value.Should().Be(type);
        hit.Aliases.Should()
            .ContainSingle()
            .Which.Name!.Evidence.JsonPointer.Should()
            .Be("/artists/0/aliases/0/name");
        hit.ArtistCredits.Should().BeNull();
        hit.WorkArtists.Should().BeNull();
        AssertSearchOnly(result);
        AssertCitations(input, result);
    }

    [Test]
    public void MissingEmptyAndInvalidSummaryCollectionsStayDistinct()
    {
        var input = Synthetic(
            "work",
            $$"""
            {"count":3,"offset":0,"works":[{"id":"{{FirstId}}"},
              {"id":"{{FirstId}}","aliases":[],"iswcs":[],"relations":[]},
              {"id":"{{FirstId}}","aliases":null,"iswcs":{},"relations":false}]}
            """
        );
        var result = decoder.Decode(input);
        var hits = result.MusicBrainzSearchPages!.Single().Hits!;

        hits[0].Aliases.Should().BeNull();
        hits[0].Iswcs.Should().BeNull();
        hits[0].WorkArtists.Should().BeNull();
        hits[1].Aliases.Should().BeEmpty();
        hits[1].Iswcs.Should().BeEmpty();
        hits[1].WorkArtists.Should().BeEmpty();
        hits[2].Aliases.Should().BeNull();
        hits[2].Iswcs.Should().BeNull();
        hits[2].WorkArtists.Should().BeNull();
        result
            .Issues.Select(issue => issue.Evidence.JsonPointer)
            .Should()
            .Equal("/works/2/aliases", "/works/2/iswcs", "/works/2/relations");
        AssertCitations(input, result);
    }

    [TestCase("[]", "expected_object", "")]
    [TestCase("null", "expected_object", "")]
    [TestCase("{\"error\":\"busy\"}", "unsuccessful_provider_response", "/error")]
    public void InvalidOrFailedRootCannotBecomeAnObservedEmptyPage(
        string json,
        string code,
        string pointer
    )
    {
        var input = Synthetic("work", json);
        var result = decoder.Decode(input);

        result.MusicBrainzSearchPages.Should().BeNull();
        result
            .Issues.Should()
            .ContainSingle()
            .Which.Should()
            .Be(new ProviderDecodeIssue(code, new(input.Uuid, pointer)));
        AssertSearchOnly(result);
        AssertCitations(input, result);
    }

    [TestCase("query text")]
    [TestCase("6671bffb-f9cc-3ea9-96e4-91cdc8d746a6")]
    [TestCase("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void OpaqueResourceKeyMustBeCanonicalSha256(string key)
    {
        var input = Synthetic("work", "{\"count\":0,\"offset\":0,\"works\":[]}", key);
        var decode = () => decoder.Decode(input);

        decode.Should().Throw<ArgumentException>();
    }

    [TestCase("musicbrainz.org", "work-search", "musicbrainz-work-search-json-v2")]
    [TestCase("musicbrainz.org", "work-search", "musicbrainz-recording-search-json-v1")]
    [TestCase("musicbrainz.org", "work", "musicbrainz-work-search-json-v1")]
    [TestCase("another-provider", "work-search", "musicbrainz-work-search-json-v1")]
    public void UnsupportedOrCrossKindProfilesRejectBeforeInterpretation(
        string provider,
        string type,
        string capture
    )
    {
        var input = Input(provider, type, new string('a', 64), capture, "{}");
        var decode = () => decoder.Decode(input);

        decode.Should().Throw<NotSupportedException>();
    }

    [Test]
    public void SearchFamilyRoundTripsAndIsOmittedFromLookupResults()
    {
        var input = Synthetic("artist", "{\"count\":0,\"offset\":0,\"artists\":[]}");
        var result = decoder.Decode(input);
        var json = JsonSerializer.SerializeToElement(result);
        var roundTrip = json.Deserialize<ProviderDecodeResult>()!;

        JsonElement
            .DeepEquals(json, JsonSerializer.SerializeToElement(roundTrip))
            .Should()
            .BeTrue();
        roundTrip.MusicBrainzSearchPages!.Single().ExternalUniverseComplete.Should().BeFalse();
        var lookup = decoder.Decode(
            Input(
                "musicbrainz.org",
                "artist",
                ArtistId,
                "musicbrainz-artist-json-v1",
                $$"""{"id":"{{ArtistId}}","name":"Artist"}"""
            )
        );
        JsonSerializer
            .SerializeToElement(lookup)
            .TryGetProperty("MusicBrainzSearchPages", out _)
            .Should()
            .BeFalse();
    }

    private static NormalizationInputRevision Synthetic(
        string kind,
        string json,
        string? key = null
    ) =>
        Input(
            "musicbrainz.org",
            kind + "-search",
            key ?? new string('a', 64),
            "musicbrainz-" + kind + "-search-json-v1",
            json
        );

    private static NormalizationInputRevision Input(
        string provider,
        string type,
        string key,
        string captureVersion,
        string json
    ) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Resource = new(provider, type, key),
            CaptureVersion = captureVersion,
            MediaType = "application/json",
            Payload = Encoding.UTF8.GetBytes(json),
        };

    private static void AssertSearchOnly(ProviderDecodeResult result)
    {
        result.Recordings.Should().BeEmpty();
        result.Repertoire.Should().BeEmpty();
        result.Setlists.Should().BeEmpty();
        result.Venues.Should().BeEmpty();
        result.MusicBrainzWorks.Should().BeNull();
        result.MusicBrainzRecordings.Should().BeNull();
        result.MusicBrainzArtists.Should().BeNull();
    }

    private static void AssertCitations(
        NormalizationInputRevision input,
        ProviderDecodeResult result
    )
    {
        using var retained = NormalizationRetainedDocument.Open(input);
        void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (
                    value.TryGetProperty("InputRevisionUuid", out var revision)
                    && value.TryGetProperty("JsonPointer", out var pointer)
                )
                {
                    retained.ValidateEvidence(new(revision.GetGuid(), pointer.GetString()!));
                }
                if (
                    value.TryGetProperty("Value", out var observed)
                    && value.TryGetProperty("Evidence", out var evidence)
                )
                {
                    var location = evidence.GetProperty("JsonPointer").GetString()!;
                    var original = retained.Resolve(location);
                    if (
                        location.EndsWith("/score", StringComparison.Ordinal)
                        && original.ValueKind == JsonValueKind.String
                    )
                    {
                        int.Parse(
                                original.GetString()!,
                                NumberStyles.None,
                                CultureInfo.InvariantCulture
                            )
                            .Should()
                            .Be(observed.GetInt32());
                    }
                    else
                    {
                        JsonElement.DeepEquals(observed, original).Should().BeTrue();
                    }
                }
                foreach (var property in value.EnumerateObject())
                {
                    Visit(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    Visit(item);
                }
            }
        }
        Visit(JsonSerializer.SerializeToElement(result));
    }
}
