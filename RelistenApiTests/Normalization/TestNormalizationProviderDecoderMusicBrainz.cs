using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestNormalizationProviderDecoderMusicBrainz
{
    private const string WorkId = "6671bffb-f9cc-3ea9-96e4-91cdc8d746a6";
    private readonly NormalizationProviderDecoder decoder = new();

    [Test]
    public void CapturedWorkPreservesSeparateComposerAndLyricistAssertionsAndOriginalWorkFields()
    {
        var input = Captured(3);
        var result = decoder.Decode(input);
        var work = result.MusicBrainzWorks.Should().ContainSingle().Subject;

        result.DecoderVersion.Should().Be("provider-observations-v4");
        work.Origin.Item.Should().Be(input.Resource);
        work.Origin.Scope.Should().Be(ProviderObservationScope.ResourceItem);
        work.Work.Identity.Identity.Should().Be(input.Resource);
        work.Work.Identity.Evidence.JsonPointer.Should().Be("/id");
        work.Work.Name!.Value.Should().Be("Johnny B. Goode");
        work.Work.Name.Evidence.JsonPointer.Should().Be("/title");
        work.Work.Type!.Value.Should().Be("Song");
        work.Work.TypeIdentity!.Identity.Should()
            .Be(
                new NormalizationResourceKey(
                    "musicbrainz.org",
                    "work_type",
                    "f061270a-2fd6-32f1-a641-f0f8676d14e6"
                )
            );
        work.Work.Disambiguation!.Value.Should().BeEmpty();
        var iswc = work.Iswcs.Should().ContainSingle().Subject;
        iswc.Original.Value.Should().Be("T-070.939.624-6");
        iswc.Identity.Identity.Should()
            .Be(new NormalizationResourceKey("iswc", "work", "T0709396246"));
        var alias = work.Aliases.Should().ContainSingle().Subject;
        alias.Name!.Value.Should().Be("Johnny B. Goode (live)");
        alias.SortName!.Value.Should().Be(alias.Name.Value);
        alias.Primary.Should().BeNull();
        alias.Locale.Should().BeNull();
        var attributes = work.Attributes!;
        var jasrac = attributes.Single(attribute => attribute.Type!.Value == "JASRAC ID");
        jasrac.Value!.Value.Should().Be("0J0-1320-3");
        jasrac
            .TypeIdentity!.Identity.Should()
            .Be(
                new NormalizationResourceKey(
                    "musicbrainz.org",
                    "work_attribute_type",
                    "31048fcc-3dbb-3979-8f85-805afb933e0c"
                )
            );
        attributes.Count(attribute => attribute.Type!.Value == "COMPASS ID").Should().Be(6);
        var relationships = work.Relationships!;
        relationships
            .Select(relation => relation.Type!.Value)
            .Should()
            .Equal("composer", "lyricist");
        relationships
            .Select(relation => relation.TypeIdentity!.Identity.ResourceType)
            .Should()
            .OnlyContain(type => type == "relationship_type");
        relationships
            .Should()
            .OnlyContain(relation =>
                relation.Direction!.Value == "backward"
                && relation.TargetType!.Value == "artist"
                && relation.Target!.Identity.Identity
                    == new NormalizationResourceKey(
                        "musicbrainz.org",
                        "artist",
                        "592a3b6d-c42b-4567-99c9-ecf63bd66499"
                    )
                && relation.Target.Type!.Value == "Person"
                && relation.Begin!.Value == "1955"
                && relation.End!.Value == "1955"
                && relation.Ended!.Value
            );
        work.CompleteAuthorship.Should().BeNull();
        result.Issues.Should().BeEmpty();
        AssertExternalOnly(result);
        AssertCitations(input, result);
    }

    [Test]
    public void CapturedGenericWriterRolesKeepCreditedNamesWithoutInventingComposerOrLyricistRoles()
    {
        var input = Captured(5);
        var result = decoder.Decode(input);
        var work = result.MusicBrainzWorks.Should().ContainSingle().Subject;

        var relationships = work.Relationships!;
        relationships.Select(relation => relation.Type!.Value).Should().Equal("writer", "writer");
        relationships
            .Select(relation => relation.Target!.Name!.Value)
            .Should()
            .Equal("Buddy Holly", "Norman Petty");
        var holly = relationships[0];
        holly.TargetCredit!.Value.Should().Be("Charles Hardin Holley");
        holly.SourceCredit!.Value.Should().BeEmpty();
        holly.Begin.Should().BeNull();
        holly.End.Should().BeNull();
        holly.Ended!.Value.Should().BeFalse();
        work.Iswcs.Should()
            .ContainSingle()
            .Which.Identity.Identity.Should()
            .Be(new NormalizationResourceKey("iswc", "work", "T0701530235"));
        work.Attributes!.Count(attribute => attribute.Type!.Value == "PRS tune code")
            .Should()
            .Be(2);
        work.Aliases.Should().BeEmpty();
        work.CompleteAuthorship.Should().BeNull();
        AssertExternalOnly(result);
        AssertCitations(input, result);
    }

    [Test]
    public void SameTitleLookupRecordsRemainUnresolvedAlternativesWithNoExhaustiveAuthorSet()
    {
        var inputs = new[] { Captured(5), Captured(6), Captured(7) };
        var results = inputs.Select(input => decoder.Decode(input)).ToArray();
        var works = results.Select(result => result.MusicBrainzWorks!.Single()).ToArray();

        works
            .Select(work => work.Work.Name!.Value)
            .Should()
            .OnlyContain(title => title == "Not Fade Away");
        works.Select(work => work.Work.Identity.Identity).Should().OnlyHaveUniqueItems();
        works.Should().OnlyContain(work => work.CompleteAuthorship == null);
        foreach (var alternative in works.Skip(1))
        {
            alternative.Relationships.Should().BeEmpty();
            alternative.Iswcs.Should().BeEmpty();
            alternative.Attributes.Should().BeEmpty();
        }
        for (var index = 0; index < results.Length; index++)
        {
            AssertExternalOnly(results[index]);
            AssertCitations(inputs[index], results[index]);
        }
    }

    [TestCase(2, "Person", "Johnny B. Goode", WorkId)]
    [TestCase(9, "Group", "Not Fade Away", "6394bda2-57a6-3578-badc-f3043d4f0ba8")]
    public void CapturedRecordingsExposeAnExternalPerformanceBridgeWithoutAssigningCatalogMusic(
        int ordinal,
        string artistType,
        string title,
        string workId
    )
    {
        var input = Captured(ordinal);
        var result = decoder.Decode(input);
        var recording = result.MusicBrainzRecordings.Should().ContainSingle().Subject;

        recording.Recording.Identity.Identity.Should().Be(input.Resource);
        recording.Recording.Name!.Value.Should().Be(title);
        var credit = recording.ArtistCredits.Should().ContainSingle().Subject;
        credit.Artist!.Type!.Value.Should().Be(artistType);
        credit.Artist.Identity.Identity.ResourceType.Should().Be("artist");
        credit.Name!.Value.Should().Be(credit.Artist.Name!.Value);
        credit.JoinPhrase!.Value.Should().BeEmpty();
        var bridge = recording.Relationships.Should().ContainSingle().Subject;
        bridge.Type!.Value.Should().Be("performance");
        bridge.Direction!.Value.Should().Be("forward");
        bridge.TargetType!.Value.Should().Be("work");
        bridge
            .Target!.Identity.Identity.Should()
            .Be(new NormalizationResourceKey("musicbrainz.org", "work", workId));
        if (ordinal == 9)
        {
            bridge.Attributes!.Select(attribute => attribute.Value).Should().Equal("cover", "live");
            bridge
                .AttributeIdentities!["live"]
                .Identity.Should()
                .Be(
                    new NormalizationResourceKey(
                        "musicbrainz.org",
                        "relationship_attribute",
                        "70007db6-a8bc-46d7-a770-80e6a0bb551a"
                    )
                );
            bridge
                .AttributeIdentities["cover"]
                .Evidence.JsonPointer.Should()
                .Be("/relations/0/attribute-ids/cover");
            bridge.Begin!.Value.Should().Be("1990-10-27");
            bridge.End!.Value.Should().Be("1990-10-27");
            bridge.Ended!.Value.Should().BeTrue();
        }
        result.MusicBrainzWorks.Should().BeNull();
        result.MusicBrainzArtists.Should().BeNull();
        AssertExternalOnly(result);
        AssertCitations(input, result);
    }

    [Test]
    public void CapturedGroupArtistAndAliasesStayExternalArtistAssertions()
    {
        var input = Captured(11);
        var result = decoder.Decode(input);
        var artist = result.MusicBrainzArtists.Should().ContainSingle().Subject;

        artist.Artist.Identity.Identity.Should().Be(input.Resource);
        artist.Artist.Type!.Value.Should().Be("Group");
        artist
            .Artist.TypeIdentity!.Identity.Should()
            .Be(
                new NormalizationResourceKey(
                    "musicbrainz.org",
                    "artist_type",
                    "e431f5f6-b5d2-343d-8b36-72607fffb74b"
                )
            );
        artist
            .Aliases!.Select(alias => alias.Name!.Value)
            .Should()
            .Equal("Greatful Dead", "Greatfull Dead", "The Grateful Dead");
        artist.Aliases.Should().OnlyContain(alias => alias.Primary == null && alias.Locale == null);
        result.MusicBrainzWorks.Should().BeNull();
        AssertExternalOnly(result);
        AssertCitations(input, result);
    }

    [Test]
    public void CapturedBusyResponseCannotBecomeEvidenceOfAnAbsentArtist()
    {
        var input = Captured(10);
        var result = decoder.Decode(input);

        AssertNoMusicBrainzObservations(result);
        result
            .Issues.Should()
            .ContainSingle(issue =>
                issue.Code == "unsuccessful_provider_response"
                && issue.Evidence.JsonPointer == "/error"
            );
        using var document = NormalizationRetainedDocument.Open(input);
        document.Resolve("/error").GetString().Should().Contain("currently busy");
        AssertCitations(input, result);
    }

    [Test]
    public void IswcFormattingPreservesOriginalCitationsAndQualifiesOneCanonicalIdentity()
    {
        var forms = new[] { "T0709396246", "T-070.939.624-6", "T-070939624-6" };
        var input = WorkInput(
            new
            {
                id = WorkId,
                title = "Work",
                iswcs = forms,
            }
        );
        var result = decoder.Decode(input);
        var identifiers = result.MusicBrainzWorks!.Single().Iswcs!;

        identifiers.Select(identifier => identifier.Original.Value).Should().Equal(forms);
        identifiers
            .Select(identifier => identifier.Identity.Identity)
            .Should()
            .OnlyContain(identity =>
                identity == new NormalizationResourceKey("iswc", "work", "T0709396246")
            );
        identifiers
            .Select(identifier => identifier.Original.Evidence.JsonPointer)
            .Should()
            .Equal("/iswcs/0", "/iswcs/1", "/iswcs/2");
        identifiers
            .Should()
            .OnlyContain(identifier =>
                identifier.Original.Evidence == identifier.Identity.Evidence
            );
        result.Issues.Should().BeEmpty();
        AssertCitations(input, result);
    }

    [Test]
    public void InvalidIswcsProduceCitedIssuesWithoutDiscardingValidPeers()
    {
        var input = WorkInput(
            new
            {
                id = WorkId,
                title = "Work",
                iswcs = new object?[]
                {
                    "T-070.939.624-6",
                    "T-070.939624-6",
                    "T-070.939.624-7",
                    "T-000.000.000-9",
                    null,
                    7,
                },
            }
        );
        var result = decoder.Decode(input);

        result
            .MusicBrainzWorks!.Single()
            .Iswcs.Should()
            .ContainSingle()
            .Which.Original.Value.Should()
            .Be("T-070.939.624-6");
        result
            .Issues.Select(issue => issue.Evidence.JsonPointer)
            .Should()
            .BeEquivalentTo("/iswcs/1", "/iswcs/2", "/iswcs/3", "/iswcs/4", "/iswcs/5");
        AssertCitations(input, result);
    }

    [Test]
    public void UnknownRelationshipTargetsRetainTheirObjectAndOriginalAttributeAssertions()
    {
        var input = WorkInput(
            new Dictionary<string, object?>
            {
                ["id"] = WorkId,
                ["title"] = "Work",
                ["relations"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "unrecognized role",
                        ["type-id"] = "d59d99ea-23d4-4a80-b066-edca32ee158f",
                        ["direction"] = "backward",
                        ["target-type"] = "place",
                        ["place"] = new
                        {
                            id = "6faa7ca7-0d99-4a5e-bfa6-1fd5037520c6",
                            name = "Uninterpreted place",
                        },
                        ["source-credit"] = "Source wording",
                        ["target-credit"] = "Target wording",
                        ["attributes"] = new object?[] { null, "instrument/role~name" },
                        ["attribute-ids"] = new Dictionary<string, string>
                        {
                            ["instrument/role~name"] = "70007db6-a8bc-46d7-a770-80e6a0bb551a",
                        },
                        ["attribute-values"] = new Dictionary<string, string>
                        {
                            ["instrument/role~name"] = "Unchanged source value",
                        },
                        ["begin"] = "19??",
                        ["end"] = "",
                        ["ended"] = false,
                    },
                },
            }
        );
        var result = decoder.Decode(input);
        var relation = result
            .MusicBrainzWorks!.Single()
            .Relationships.Should()
            .ContainSingle()
            .Subject;

        relation.Type!.Value.Should().Be("unrecognized role");
        relation.Direction!.Value.Should().Be("backward");
        relation.TargetType!.Value.Should().Be("place");
        relation.Target.Should().BeNull();
        relation.Evidence.JsonPointer.Should().Be("/relations/0");
        relation.SourceCredit!.Value.Should().Be("Source wording");
        relation.TargetCredit!.Value.Should().Be("Target wording");
        relation.Begin!.Value.Should().Be("19??");
        relation.End!.Value.Should().BeEmpty();
        relation.Ended!.Value.Should().BeFalse();
        relation
            .Attributes.Should()
            .ContainSingle()
            .Which.Value.Should()
            .Be("instrument/role~name");
        relation
            .AttributeIdentities!["instrument/role~name"]
            .Identity.ResourceType.Should()
            .Be("relationship_attribute");
        relation
            .AttributeIdentities["instrument/role~name"]
            .Evidence.JsonPointer.Should()
            .Be("/relations/0/attribute-ids/instrument~1role~0name");
        relation
            .AttributeValues!["instrument/role~name"]
            .Value.Should()
            .Be("Unchanged source value");
        relation
            .AttributeValues["instrument/role~name"]
            .Evidence.JsonPointer.Should()
            .Be("/relations/0/attribute-values/instrument~1role~0name");
        using var document = NormalizationRetainedDocument.Open(input);
        document
            .Resolve(relation.Evidence.JsonPointer)
            .GetProperty("place")
            .GetProperty("name")
            .GetString()
            .Should()
            .Be("Uninterpreted place");
        result
            .Issues.Should()
            .ContainSingle(issue => issue.Evidence.JsonPointer == "/relations/0/attributes/0");
        AssertCitations(input, result);
    }

    [Test]
    public void AbsentEmptyAndNullContainersDoNotCollapseIntoACleanEmptyAuthorList()
    {
        var missingInput = WorkInput(new { id = WorkId, title = "Work" });
        var missing = decoder.Decode(missingInput);
        var absent = missing.MusicBrainzWorks!.Single();
        absent.Iswcs.Should().BeNull();
        absent.Aliases.Should().BeNull();
        absent.Attributes.Should().BeNull();
        absent.Relationships.Should().BeNull();
        missing.Issues.Should().BeEmpty();
        var emptyInput = WorkInput(
            new
            {
                id = WorkId,
                title = "Work",
                iswcs = Array.Empty<string>(),
                aliases = Array.Empty<object>(),
                attributes = Array.Empty<object>(),
                relations = Array.Empty<object>(),
            }
        );
        var empty = decoder.Decode(emptyInput);
        var explicitEmpty = empty.MusicBrainzWorks!.Single();
        explicitEmpty.Iswcs.Should().BeEmpty();
        explicitEmpty.Aliases.Should().BeEmpty();
        explicitEmpty.Attributes.Should().BeEmpty();
        explicitEmpty.Relationships.Should().BeEmpty();
        explicitEmpty.CompleteAuthorship.Should().BeNull();
        empty.Issues.Should().BeEmpty();
        var nullInput = WorkInput(
            new
            {
                id = WorkId,
                title = "Work",
                iswcs = (object?)null,
                aliases = (object?)null,
                attributes = (object?)null,
                relations = (object?)null,
            }
        );
        var invalid = decoder.Decode(nullInput);
        var explicitNull = invalid.MusicBrainzWorks!.Single();
        explicitNull.Iswcs.Should().BeNull();
        explicitNull.Aliases.Should().BeNull();
        explicitNull.Attributes.Should().BeNull();
        explicitNull.Relationships.Should().BeNull();
        invalid
            .Issues.Select(issue => issue.Evidence.JsonPointer)
            .Should()
            .BeEquivalentTo("/iswcs", "/aliases", "/attributes", "/relations");
        AssertCitations(missingInput, missing);
        AssertCitations(emptyInput, empty);
        AssertCitations(nullInput, invalid);
    }

    [Test]
    public void MalformedOptionalFieldsAreCitedWhileValidPeersAndUnresolvedRelationshipsSurvive()
    {
        var input = JsonInput(
            """
            {"id":"6671bffb-f9cc-3ea9-96e4-91cdc8d746a6","title":42,
             "aliases":[{"name":"Alias","primary":false},5],
             "attributes":[{"type":"Registry","value":"007"},false],
             "relations":[{"type":"writer","target-type":"artist","artist":{"id":"invalid","name":"Unknown"},
               "direction":4,"ended":"false","attribute-ids":{"bad":null},"attribute-values":{"bad":null}},false]}
            """
        );
        var result = decoder.Decode(input);
        var work = result.MusicBrainzWorks.Should().ContainSingle().Subject;

        work.Work.Name.Should().BeNull();
        work.Aliases.Should().ContainSingle().Which.Primary!.Value.Should().BeFalse();
        work.Attributes.Should().ContainSingle().Which.Value!.Value.Should().Be("007");
        var relation = work.Relationships.Should().ContainSingle().Subject;
        relation.Type!.Value.Should().Be("writer");
        relation.Target.Should().BeNull();
        relation.Direction.Should().BeNull();
        relation.Ended.Should().BeNull();
        relation.AttributeIdentities.Should().BeEmpty();
        relation.AttributeValues.Should().BeEmpty();
        result
            .Issues.Select(issue => issue.Evidence.JsonPointer)
            .Should()
            .BeEquivalentTo(
                "/title",
                "/aliases/1",
                "/attributes/1",
                "/relations/0/artist/id",
                "/relations/0/direction",
                "/relations/0/ended",
                "/relations/0/attribute-ids/bad",
                "/relations/0/attribute-values/bad",
                "/relations/1"
            );
        AssertCitations(input, result);
    }

    [TestCase("[]", "")]
    [TestCase("null", "")]
    [TestCase("{\"title\":\"Missing identity\"}", "")]
    [TestCase(
        "{\"id\":\"6394bda2-57a6-3578-badc-f3043d4f0ba8\",\"title\":\"Another work\"}",
        "/id"
    )]
    public void InvalidRootOrMismatchedIdentityProducesOnlyCitedIssues(string json, string pointer)
    {
        var input = JsonInput(json);
        var result = decoder.Decode(input);

        AssertNoMusicBrainzObservations(result);
        result.Issues.Should().ContainSingle(issue => issue.Evidence.JsonPointer == pointer);
        AssertCitations(input, result);
    }

    [TestCase("6671BFFB-F9CC-3EA9-96E4-91CDC8D746A6")]
    [TestCase("6671bffbf9cc3ea996e491cdc8d746a6")]
    [TestCase("00000000-0000-0000-0000-000000000000")]
    [TestCase("invalid")]
    public void LookupResourceKeysMustBeCanonicalNonemptyLowercaseMbids(string providerKey)
    {
        var input = Input("work", providerKey, Captured(3).Payload);
        var decode = () => decoder.Decode(input);

        decode.Should().Throw<ArgumentException>();
    }

    [Test]
    public void UnsupportedLookupProfilesAndCancellationFailExplicitly()
    {
        var captured = Captured(3);
        var unsupported = Input("work", WorkId, captured.Payload, "musicbrainz-work-json-v2");
        var decode = () => decoder.Decode(unsupported);
        decode.Should().Throw<NotSupportedException>();
        var searchInput = Input(
            "work-search",
            "work:Not Fade Away",
            captured.Payload,
            "musicbrainz-work-search-json-v1"
        );
        var search = () => decoder.Decode(searchInput);
        search.Should().Throw<NotSupportedException>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = () => decoder.Decode(captured, cancellation.Token);
        cancelled.Should().Throw<OperationCanceledException>();
    }

    [Test]
    public void ExistingProviderSerializationOmitsUncapturedMusicBrainzFamilies()
    {
        var input = new NormalizationInputRevision
        {
            Uuid = Guid.NewGuid(),
            Resource = new("setlist.fm", "setlist-page", "artist:page=1"),
            CaptureVersion = "setlist-fm-http-v1",
            MediaType = "application/json",
            Payload = Encoding.UTF8.GetBytes(
                """{"page":1,"itemsPerPage":20,"total":0,"setlist":[]}"""
            ),
        };
        var result = decoder.Decode(input);
        var serialized = JsonSerializer.SerializeToElement(result);

        serialized.TryGetProperty("MusicBrainzWorks", out _).Should().BeFalse();
        serialized.TryGetProperty("MusicBrainzRecordings", out _).Should().BeFalse();
        serialized.TryGetProperty("MusicBrainzArtists", out _).Should().BeFalse();
        var roundTrip = serialized.Deserialize<ProviderDecodeResult>()!;
        AssertNoMusicBrainzObservations(roundTrip);
        JsonElement
            .DeepEquals(serialized, JsonSerializer.SerializeToElement(roundTrip))
            .Should()
            .BeTrue();
    }

    private static NormalizationInputRevision Captured(int ordinal)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(FixturePath("manifest.json")));
        var response = manifest
            .RootElement.GetProperty("responses")
            .EnumerateArray()
            .Single(item => item.GetProperty("ordinal").GetInt32() == ordinal);
        return Input(
            response.GetProperty("resourceType").GetString()!,
            response.GetProperty("providerKey").GetString()!,
            File.ReadAllBytes(FixturePath(response.GetProperty("file").GetString()!)),
            response.GetProperty("captureVersion").GetString()
        );
    }

    private static string FixturePath(string file) =>
        Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Fixtures",
            "normalization",
            "musicbrainz",
            file
        );

    private static NormalizationInputRevision WorkInput(object value) =>
        Input("work", WorkId, JsonSerializer.SerializeToUtf8Bytes(value));

    private static NormalizationInputRevision JsonInput(string json) =>
        Input("work", WorkId, Encoding.UTF8.GetBytes(json));

    private static NormalizationInputRevision Input(
        string type,
        string key,
        byte[] payload,
        string? captureVersion = null
    ) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Resource = new("musicbrainz.org", type, key),
            CaptureVersion =
                captureVersion
                ?? (
                    type switch
                    {
                        "work" => "musicbrainz-work-json-v1",
                        "recording" => "musicbrainz-recording-work-json-v1",
                        "artist" => "musicbrainz-artist-json-v1",
                        _ => throw new ArgumentOutOfRangeException(nameof(type)),
                    }
                ),
            Payload = payload,
            MediaType = "application/json",
        };

    private static void AssertNoMusicBrainzObservations(ProviderDecodeResult result)
    {
        result.MusicBrainzWorks.Should().BeNull();
        result.MusicBrainzRecordings.Should().BeNull();
        result.MusicBrainzArtists.Should().BeNull();
        AssertExternalOnly(result);
    }

    private static void AssertExternalOnly(ProviderDecodeResult result)
    {
        result.Recordings.Should().BeEmpty();
        result.Venues.Should().BeEmpty();
        result.Repertoire.Should().BeEmpty();
        result.Setlists.Should().BeEmpty();
    }

    // Traverse the public result, including issues and dictionary values. Scalar observations
    // must reproduce their exact source value, and every citation must resolve in retained bytes.
    private static void AssertCitations(
        NormalizationInputRevision input,
        ProviderDecodeResult result
    )
    {
        using var document = NormalizationRetainedDocument.Open(input);
        var citations = 0;
        void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (
                    value.TryGetProperty("InputRevisionUuid", out var revision)
                    && value.TryGetProperty("JsonPointer", out var pointer)
                )
                {
                    document.ValidateEvidence(new(revision.GetGuid(), pointer.GetString()!));
                    citations++;
                }
                if (
                    value.TryGetProperty("Value", out var observed)
                    && (
                        observed.ValueKind
                        is JsonValueKind.String
                            or JsonValueKind.Number
                            or JsonValueKind.True
                            or JsonValueKind.False
                    )
                    && value.TryGetProperty("Evidence", out var evidence)
                )
                {
                    var retained = document.Resolve(
                        evidence.GetProperty("JsonPointer").GetString()!
                    );
                    JsonElement.DeepEquals(observed, retained).Should().BeTrue();
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
        citations.Should().BeGreaterThan(0);
    }
}
