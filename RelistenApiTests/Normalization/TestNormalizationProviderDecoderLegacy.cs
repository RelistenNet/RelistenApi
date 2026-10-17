using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

/// <summary>Catalog fallback observations retain stored attribution without promoting configured providers to origin.</summary>
[TestFixture]
public sealed class TestNormalizationProviderDecoderLegacy
{
    private static readonly Guid SourceUuid = Guid.Parse("42a2ca82-5488-453c-9e11-c355cfe15e00");
    private static readonly Guid TrackUuid = Guid.Parse("96592bd3-23c0-424d-9c8b-5f47f250c8c5");
    private readonly NormalizationProviderDecoder decoder = new();

    [Test]
    public void OriginalProseAndStoredIdentitiesStaySeparateFromConfiguredProviderMappings()
    {
        var input = Input(Snapshot());
        var result = decoder.Decode(input);
        var recording = result.Recordings.Should().ContainSingle().Which;
        recording.Origin.Item.Should().Be(input.Resource);
        recording.Origin.Scope.Should().Be(ProviderObservationScope.LegacySnapshot);
        recording.LiteralDate!.Value.Should().Be("1970-XX-XX");
        recording.Venue!.Names.Single().Value.Should().Be("Original Venue");
        result
            .Venues.Select(venue => venue.Names.Single().Value)
            .Should()
            .Equal("Original Venue", "Later Venue");
        result
            .Venues.Select(venue => venue.Origin.Evidence.JsonPointer)
            .Should()
            .Equal("/source_venue", "/show_venue");

        recording.Performers.Single().Name!.Value.Should().Be("Fixture Artist");
        recording
            .Performers.Single()
            .Identities.Single()
            .Identity.Provider.Should()
            .Be("legacy_catalog");
        var expected = new Dictionary<string, string>
        {
            ["description"] = "<p>Original &amp; prose 🎤</p>",
            ["taper_notes"] = "First directory notes\nSecond directory notes",
            ["source"] = "AUD > DAT",
            ["taper"] = "Zoë Recorder",
            ["transferrer"] = "Pat Transfer",
            ["lineage"] = "DAT > WAV",
            ["catalog_upstream_identifier"] = "gd1970-unknown",
            ["show_date"] = "1970-01-01",
            ["show_display_date"] = "1970-XX-XX Late",
        };
        foreach (var (field, wording) in expected)
        {
            recording.Text.Single(text => text.Field == field).Text.Value.Should().Be(wording);
        }
        var file = recording.Files.Should().ContainSingle().Which;
        file.Identities.Single()
            .Identity.Should()
            .Be(new NormalizationResourceKey("legacy_catalog", "track", TrackUuid.ToString("D")));
        file.Origin.Scope.Should().Be(ProviderObservationScope.LegacySnapshot);
        file.Title!.Value.Should().Be("Song A > Song B");
        file.PlaybackLocator!.Value.Should().Be("https://example.test/file.mp3");
        file.Filename.Should().BeNull();
        file.Position!.Value.Should().Be(1);
        file.SetLabel!.Value.Should().Be("Set 1");
        file.SongIdentities.Should().BeEmpty();
        file.WorkCredits.Should().BeEmpty();
        result.Setlists.Should().BeEmpty();
        result.Repertoire.Should().BeEmpty();
        result
            .Issues.Select(issue => issue.Code)
            .Should()
            .Equal("source_show_date_wording_differs");
        AssertEvidence(input, result);
    }

    [Test]
    public void OrphanedMissingAndForeignContextsProduceIssuesWithoutDiscardingHistoricalFiles()
    {
        var snapshot = Snapshot();
        var entry = snapshot["tracks"]![0]!.AsObject();
        entry["track"]!["source_id"] = null;
        entry["track"]!["artist_id"] = null;
        entry["track"]!["is_orphaned"] = true;
        entry["track"]!["mp3_url"] = null;
        entry["source_context"] = null;
        entry["artist_context"] = null;
        entry["set_context"]!["source_id"] = 999;
        var input = Input(snapshot);
        var result = decoder.Decode(input);
        var file = result.Recordings.Single().Files.Single();
        file.Identities.Single().Identity.ProviderKey.Should().Be(TrackUuid.ToString("D"));
        file.Title!.Value.Should().Be("Song A > Song B");
        file.PlaybackLocator.Should().BeNull();
        result
            .Issues.Select(issue => issue.Code)
            .Should()
            .Contain([
                "orphaned_catalog_track",
                "missing_track_source_context",
                "missing_track_artist_context",
                "track_set_recording_mismatch",
            ]);
        AssertEvidence(input, result);
    }

    [Test]
    public void ShowVenueRemainsSeparateWhenRecordingVenueIsMissing()
    {
        var snapshot = Snapshot();
        snapshot["source_venue"] = null;
        var input = Input(snapshot);
        var result = decoder.Decode(input);
        result.Recordings.Single().Venue.Should().BeNull();
        var venue = result.Venues.Should().ContainSingle().Which;
        venue.Names.Single().Value.Should().Be("Later Venue");
        venue.Origin.Evidence.JsonPointer.Should().Be("/show_venue");
        venue.Identities.Single().Identity.Provider.Should().Be("legacy_catalog");
        venue.CoordinateKind.Should().Be(ObservedCoordinateKind.Unknown);
        AssertEvidence(input, result);
    }

    [Test]
    public void DuplicateAndMissingTrackIdentitiesRemainUnresolvedWithoutOrdinalIdentifiers()
    {
        var snapshot = Snapshot();
        var tracks = snapshot["tracks"]!.AsArray();
        tracks.Add(tracks[0]!.DeepClone());
        var missing = tracks[0]!.DeepClone();
        missing!["track"]!.AsObject().Remove("uuid");
        tracks.Add(missing);
        tracks.Add(false);
        var input = Input(snapshot);
        var result = decoder.Decode(input);
        var files = result.Recordings.Single().Files;
        files.Should().HaveCount(3);
        files[0].Identities.Single().Identity.Should().Be(files[1].Identities.Single().Identity);
        files[2].Identities.Should().BeEmpty();
        files[2].Origin.Item.Should().Be(input.Resource);
        result
            .Issues.Select(issue => issue.Code)
            .Should()
            .Contain([
                "duplicate_file_identity",
                "invalid_catalog_identity",
                "expected_file_object",
            ]);
        AssertEvidence(input, result);
    }

    [Test]
    public void MissingOptionalContextIsCitedAtExistingContainers()
    {
        var snapshot = Snapshot();
        snapshot.Remove("show");
        snapshot.Remove("artist");
        snapshot["tracks"]![0]!.AsObject().Remove("set_context");
        var input = Input(snapshot);
        var result = decoder.Decode(input);
        result.Recordings.Single().Performers.Should().BeEmpty();
        result
            .Issues.Select(issue => issue.Code)
            .Should()
            .Contain([
                "missing_recording_show_context",
                "missing_recording_artist_context",
                "missing_track_set_context",
            ]);
        AssertEvidence(input, result);
    }

    [TestCase("source")]
    [TestCase("representation")]
    [TestCase("source_uuid")]
    public void InvalidEnvelopeCannotAcquireTheRequestedRecordingIdentity(string malformed)
    {
        var snapshot = Snapshot();
        switch (malformed)
        {
            case "source":
                snapshot["source"] = null;
                break;
            case "representation":
                snapshot["representation"] = "provider_response";
                break;
            default:
                snapshot["source"]!["uuid"] = Guid.NewGuid().ToString();
                break;
        }
        var decode = () => decoder.Decode(Input(snapshot));
        decode.Should().Throw<ArgumentException>();
    }

    private static JsonObject Snapshot() =>
        JsonNode
            .Parse(
                """
                {
                  "representation":"legacy_catalog","snapshot_at":"2026-10-17T00:00:00Z",
                  "source":{"id":101,"uuid":"42a2ca82-5488-453c-9e11-c355cfe15e00","artist_id":1,"show_id":201,
                    "display_date":"1970-XX-XX","upstream_identifier":"gd1970-unknown",
                    "description":"<p>Original &amp; prose 🎤</p>","taper_notes":"First directory notes\nSecond directory notes",
                    "source":"AUD > DAT","taper":"Zoë Recorder","transferrer":"Pat Transfer","lineage":"DAT > WAV"},
                  "artist":{"id":1,"uuid":"78fed3c1-e7b0-4532-aaeb-82c2cb512a3c","name":"Fixture Artist","slug":"fixture"},
                  "show":{"id":201,"uuid":"a9680d7b-8d68-4ddc-8645-3bcbbdc5937f","date":"1970-01-01","display_date":"1970-XX-XX Late"},
                  "source_venue":{"id":301,"uuid":"ae605b29-2932-4089-a793-0ae27dd494c2","name":"Original Venue","location":"City, State"},
                  "show_venue":{"id":302,"uuid":"6b56ede1-11b1-4a8b-b0bc-eafc2c4bfd80","name":"Later Venue","location":"Other City","latitude":1.0,"longitude":2.0},
                  "configured_artist_mappings":[{"upstream_source_id":1,"upstream_provider":"archive.org","upstream_identifier":"FixtureCollection"}],
                  "sets":[{"id":401,"uuid":"1e788169-200b-439d-ab3a-3bf9f3df8e0e","source_id":101,"index":0,"name":"Set 1"}],
                  "tracks":[{
                    "track":{"id":501,"uuid":"96592bd3-23c0-424d-9c8b-5f47f250c8c5","source_id":101,"source_set_id":401,"artist_id":1,
                      "title":"Song A > Song B","track_position":1,"mp3_url":"https://example.test/file.mp3","flac_url":"https://example.test/file.flac","is_orphaned":false},
                    "source_context":{"id":101,"uuid":"42a2ca82-5488-453c-9e11-c355cfe15e00","artist_id":1,"show_id":201},
                    "set_context":{"id":401,"uuid":"1e788169-200b-439d-ab3a-3bf9f3df8e0e","source_id":101,"index":0,"name":"Set 1"},
                    "artist_context":{"id":1,"uuid":"78fed3c1-e7b0-4532-aaeb-82c2cb512a3c","name":"Fixture Artist","slug":"fixture"}
                  }]
                }
                """
            )!
            .AsObject();

    private static NormalizationInputRevision Input(JsonObject snapshot) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Resource = new("legacy_catalog", "recording", SourceUuid.ToString("D")),
            CaptureVersion = "legacy-recording-snapshot-v1",
            MediaType = "application/json; charset=utf-8",
            Payload = Encoding.UTF8.GetBytes(snapshot.ToJsonString()),
        };

    private static void AssertEvidence(
        NormalizationInputRevision input,
        ProviderDecodeResult result
    )
    {
        using var document = NormalizationRetainedDocument.Open(input);
        Visit(JsonSerializer.SerializeToElement(result));
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
                foreach (var item in value.EnumerateArray())
                {
                    Visit(item);
                }
            }
        }
    }
}
