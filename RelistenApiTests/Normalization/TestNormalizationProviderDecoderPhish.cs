using System.Collections;
using System.Text;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

/// <summary>Synthetic provider responses exercise decoding and exact retained citations; they are not identity labels.</summary>
[TestFixture]
public sealed class TestNormalizationProviderDecoderPhish
{
    private readonly NormalizationProviderDecoder decoder = new();

    [Test]
    public void CatalogAliasLinksKeepEachProviderIdentityWithoutInventingWorks()
    {
        var input = Catalog(
            "songs",
            """
            {"success":true,"data":[
              {"id":3,"title":"Run Like an Antelope","alias_for":null},
              {"id":4,"title":"Antelope","alias_for":3}
            ]}
            """
        );

        var result = decoder.Decode(input);

        result.Issues.Should().BeEmpty();
        result
            .Repertoire.Select(song => song.Title.Value)
            .Should()
            .Equal("Run Like an Antelope", "Antelope");
        var original = result.Repertoire[0];
        var alias = result.Repertoire[1];
        original
            .Identities.Single()
            .Identity.Should()
            .Be(new NormalizationResourceKey("phish.in", "song", "3"));
        alias.Identities.Single().Identity.ProviderKey.Should().Be("4");
        alias.AliasOf!.Identity.Should().Be(original.Identities.Single().Identity);
        alias.AliasOf.Evidence.JsonPointer.Should().Be("/data/1/alias_for");
        alias.Origin.Scope.Should().Be(ProviderObservationScope.CatalogMember);
        result
            .Repertoire.Should()
            .OnlyContain(song =>
                !song.CompleteAuthorship
                && song.WorkCredits.Count == 0
                && song.WorkIdentities.Count == 0
            );
        ValidateEveryCitation(input, result);
    }

    [Test]
    public void VenueHistoryRemainsProseAndCoordinatesKeepTheirUnknownSubject()
    {
        var input = Catalog(
            "venues",
            """
            {"success":true,"data":[{"id":9,"name":"Current Hall",
             "past_names":"Old Hall (1980–1995); formerly part of the theatre",
             "location":"Philadelphia, PA","latitude":39.9,"longitude":-75.1}]}
            """
        );

        var result = decoder.Decode(input);

        result.Issues.Should().BeEmpty();
        var venue = result.Venues.Single();
        venue.Names.Select(name => name.Value).Should().Equal("Current Hall");
        venue.Text.Single().Field.Should().Be("historical_names");
        venue
            .Text.Single()
            .Text.Value.Should()
            .Be("Old Hall (1980–1995); formerly part of the theatre");
        venue.Text.Single().Text.Evidence.JsonPointer.Should().Be("/data/0/past_names");
        venue.OriginalLocality!.Value.Should().Be("Philadelphia, PA");
        venue.City.Should().BeNull();
        venue.Region.Should().BeNull();
        venue.Latitude!.Value.Should().Be(39.9);
        venue.Longitude!.Value.Should().Be(-75.1);
        venue.CoordinateKind.Should().Be(ObservedCoordinateKind.Unknown);
        venue.Origin.Item.Should().Be(new NormalizationResourceKey("phish.in", "venue", "9"));
        ValidateEveryCitation(input, result);
    }

    [Test]
    public void ShowFilesPreserveProviderOrderRepeatsAndSongMembershipWithoutOccurrences()
    {
        var input = Show(
            """
            {"id":42,"date":"1990-xx-xx","taper_notes":"Taped by Jane; transferred by Sam.",
             "venue":{"id":9,"name":"A room","location":"Somewhere"},
             "tags":[{"name":"AUD","notes":"Original tape"}],
             "tracks":[
              {"id":201,"show_id":42,"title":"Weekapaug Groove","position":2,"set":"2","set_name":"Set 2","mp3":"https://audio.example/201.mp3","song_ids":[4,4,7]},
              {"id":202,"show_id":42,"title":"Weekapaug Groove","position":1,"set":"E","set_name":"Encore","song_ids":[4]}
             ]}
            """
        );

        var result = decoder.Decode(input);

        result.Issues.Should().BeEmpty();
        var recording = result.Recordings.Single();
        recording.Origin.Item.Should().Be(input.Resource);
        recording.LiteralDate!.Value.Should().Be("1990-xx-xx");
        recording.Venue!.Origin.Item.Should().Be(input.Resource);
        recording.Venue.Origin.Evidence.JsonPointer.Should().Be("/venue");
        recording.Venue.Identities.Single().Identity.ProviderKey.Should().Be("9");
        recording
            .Files.Select(file => file.Identities.Single().Identity.ProviderKey)
            .Should()
            .Equal("201", "202");
        recording.Files.Select(file => file.Position!.Value).Should().Equal(2, 1);
        recording
            .Files.Select(file => file.Title!.Value)
            .Should()
            .Equal("Weekapaug Groove", "Weekapaug Groove");
        var first = recording.Files[0];
        first.Origin.Item.Should().Be(input.Resource);
        first
            .SongIdentities.Select(song => song.Identity.ProviderKey)
            .Should()
            .Equal("4", "4", "7");
        first
            .SongIdentities.Select(song => song.Evidence.JsonPointer)
            .Should()
            .Equal("/tracks/0/song_ids/0", "/tracks/0/song_ids/1", "/tracks/0/song_ids/2");
        first.PlaybackLocator!.Value.Should().Be("https://audio.example/201.mp3");
        first.SetLabel!.Value.Should().Be("2");
        first.Text.Single().Text.Value.Should().Be("Set 2");
        recording
            .Text.Select(text => (text.Field, text.Text.Value))
            .Should()
            .Equal(
                ("taper_notes", "Taped by Jane; transferred by Sam."),
                ("tag", "AUD"),
                ("tag_notes", "Original tape")
            );
        result.Setlists.Should().BeEmpty();
        result.Repertoire.Should().BeEmpty();
        ValidateEveryCitation(input, result);
    }

    [Test]
    public void UnexpectedFieldShapesProduceCitedIssuesAndRetainOtherAssertions()
    {
        var input = Show(
            """
            {"id":42,"date":["1990-01-01"],"venue":false,
             "tags":[{"name":"AUD","notes":{"description":"structured"}},false],
             "tracks":[false,{"id":0,"title":["not scalar"],"position":"first","set":{},"song_ids":["7",null,-2,4]}]}
            """
        );

        var result = decoder.Decode(input);

        var recording = result.Recordings.Single();
        recording.LiteralDate.Should().BeNull();
        recording.Venue.Should().BeNull();
        recording.Files.Should().HaveCount(1);
        recording.Files.Single().SongIdentities.Single().Identity.ProviderKey.Should().Be("4");
        result
            .Issues.Select(issue => issue.Evidence.JsonPointer)
            .Should()
            .Contain(
                new[]
                {
                    "/date",
                    "/venue",
                    "/tags/0/notes",
                    "/tags/1",
                    "/tracks/0",
                    "/tracks/1/id",
                    "/tracks/1/title",
                    "/tracks/1/position",
                    "/tracks/1/set",
                    "/tracks/1/song_ids/0",
                    "/tracks/1/song_ids/1",
                    "/tracks/1/song_ids/2",
                }
            );
        ValidateEveryCitation(input, result);
    }

    [Test]
    public void ConflictingTrackOwnerIsVisibleWithoutDiscardingTheFileAssertion()
    {
        var input = Show(
            """
            {"id":42,"tracks":[{"id":201,"show_id":43,"title":"A song","song_ids":[4]}]}
            """
        );

        var result = decoder.Decode(input);

        var file = result.Recordings.Single().Files.Single();
        file.Origin.Item.Should().Be(input.Resource);
        file.Identities.Single().Identity.ProviderKey.Should().Be("201");
        file.RecordingIdentity!.Identity.Should()
            .Be(new NormalizationResourceKey("phish.in", "show", "43"));
        file.RecordingIdentity.Evidence.JsonPointer.Should().Be("/tracks/0/show_id");
        file.Title!.Value.Should().Be("A song");
        result.Issues.Single().Code.Should().Be("track_show_identity_mismatch");
        result.Issues.Single().Evidence.JsonPointer.Should().Be("/tracks/0/show_id");
        ValidateEveryCitation(input, result);
    }

    [Test]
    public void MismatchedShowIdentityCannotDescribeTheRetainedResource()
    {
        var input = Show("""{"id":43,"tracks":[]}""");

        var result = decoder.Decode(input);

        result.Recordings.Should().BeEmpty();
        result.Issues.Single().Code.Should().Be("resource_identity_mismatch");
        result.Issues.Single().Evidence.JsonPointer.Should().Be("/id");
        ValidateEveryCitation(input, result);
    }

    [TestCase("[]", "expected_object")]
    [TestCase("{\"success\":false,\"data\":[]}", "unsuccessful_provider_response")]
    [TestCase("{\"success\":true}", "missing_catalog_data")]
    [TestCase("{\"success\":true,\"data\":{}}", "expected_array")]
    public void InvalidCatalogEnvelopeIsExplicit(string json, string expectedIssue)
    {
        var input = Catalog("songs", json);

        var result = decoder.Decode(input);

        result.Repertoire.Should().BeEmpty();
        result.Issues.Single().Code.Should().Be(expectedIssue);
        ValidateEveryCitation(input, result);
    }

    [Test]
    public void DuplicateCatalogEntriesRemainSeparateConflictingAssertions()
    {
        var input = Catalog(
            "songs",
            """
            {"success":true,"data":[{"id":3,"title":"Title A"},{"id":3,"title":"Title B"}]}
            """
        );

        var result = decoder.Decode(input);

        result.Repertoire.Select(song => song.Title.Value).Should().Equal("Title A", "Title B");
        result.Issues.Single().Code.Should().Be("duplicate_provider_identity");
        ValidateEveryCitation(input, result);
    }

    [TestCase("eras", "{\"1.0\":[\"1990\"]}")]
    [TestCase("tours", "[]")]
    public void UnrelatedCatalogsHaveNoDomainObservations(string route, string data)
    {
        var result = decoder.Decode(Catalog(route, "{\"success\":true,\"data\":" + data + "}"));

        result.Issues.Should().BeEmpty();
        result.Recordings.Should().BeEmpty();
        result.Repertoire.Should().BeEmpty();
        result.Venues.Should().BeEmpty();
        result.Setlists.Should().BeEmpty();
    }

    [Test]
    public void UnknownCatalogRouteIsNotSilentlyDecodedAsSongs()
    {
        var input = Catalog("future-catalog", """{"success":true,"data":[]}""");

        var result = decoder.Decode(input);

        result.Issues.Single().Code.Should().Be("unsupported_catalog_route");
        result.Repertoire.Should().BeEmpty();
        ValidateEveryCitation(input, result);
    }

    private static NormalizationInputRevision Catalog(string route, string json) =>
        Input("catalog", route, "raw-http:1", json);

    private static NormalizationInputRevision Show(string json) =>
        Input("show", "42", "phishin-show-object-v1", json);

    private static NormalizationInputRevision Input(
        string type,
        string key,
        string captureVersion,
        string json
    ) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Resource = new("phish.in", type, key),
            Payload = Encoding.UTF8.GetBytes(json),
            MediaType = "application/json",
            CaptureVersion = captureVersion,
        };

    private static void ValidateEveryCitation(
        NormalizationInputRevision input,
        ProviderDecodeResult result
    )
    {
        using var document = NormalizationRetainedDocument.Open(input);
        var count = 0;
        Visit(result);
        count.Should().BeGreaterThan(0);

        void Visit(object? value)
        {
            if (value is NormalizedEvidenceReference evidence)
            {
                document.ValidateEvidence(evidence);
                count++;
            }
            else if (value is IEnumerable collection and not string)
            {
                foreach (var item in collection)
                {
                    Visit(item);
                }
            }
            else if (
                value != null
                && value.GetType().Namespace == typeof(ProviderDecodeResult).Namespace
            )
            {
                foreach (var property in value.GetType().GetProperties())
                {
                    Visit(property.GetValue(value));
                }
            }
        }
    }
}
