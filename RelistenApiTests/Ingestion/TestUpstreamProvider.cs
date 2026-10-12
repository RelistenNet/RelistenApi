using System.Data;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Relisten.Api.Models;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.Archive;
using Temporalio.Converters;

namespace RelistenApiTests.Ingestion;

[TestFixture]
public class TestUpstreamProvider
{
    [TestCase(UpstreamProvider.ArchiveOrg, "archive.org")]
    [TestCase(UpstreamProvider.Phishin, "phish.in")]
    [TestCase(UpstreamProvider.PhishNet, "phish.net")]
    [TestCase(UpstreamProvider.SetlistFm, "setlist.fm")]
    [TestCase(UpstreamProvider.PhantasyTour, "phantasytour.com")]
    [TestCase(UpstreamProvider.JerryGarcia, "jerrygarcia.com")]
    [TestCase(UpstreamProvider.Local, "local")]
    [TestCase(UpstreamProvider.PanicStream, "panicstream.com")]
    public void ProviderRoundTripsThroughApiTemporalAndDatabaseBoundaries(
        UpstreamProvider provider,
        string storedName
    )
    {
        var apiJson = JsonConvert.SerializeObject(provider);
        JToken.Parse(apiJson).Value<string>().Should().Be(storedName);
        JsonConvert.DeserializeObject<UpstreamProvider>(apiJson).Should().Be(provider);

        var converter = new DefaultPayloadConverter();
        var payload = converter.ToPayload(provider);
        using var temporalJson = JsonDocument.Parse(payload.Data.ToStringUtf8());
        temporalJson.RootElement.GetString().Should().Be(storedName);
        converter.ToValue<UpstreamProvider>(payload).Should().Be(provider);

        using var rows = new DataTable();
        rows.Columns.Add("name", typeof(string));
        rows.Rows.Add(storedName);
        using var reader = rows.CreateDataReader();
        var source = reader.Parse<UpstreamSource>().Single();
        source.Provider.Should().Be(provider);
        var sourceJson = JObject.Parse(JsonConvert.SerializeObject(source));
        sourceJson["name"]!.Value<string>().Should().Be(storedName);
        sourceJson.Property("Provider").Should().BeNull();

        UpstreamProviders.Parse(storedName).Should().Be(provider);
        provider.ToValue().Should().Be(storedName);
    }

    [TestCase("unknown-provider")]
    [TestCase("0")]
    [TestCase("ArchiveOrg")]
    public void CommandLineProviderMustUseAKnownStoredName(string value)
    {
        var parse = () => UpstreamProviders.Parse(value);
        parse.Should().Throw<ArgumentException>();
    }

    [TestCase("0")]
    [TestCase("\"unknown-provider\"")]
    public void ApiAndTemporalRejectInvalidProviderPayloads(string json)
    {
        var api = () => JsonConvert.DeserializeObject<UpstreamProvider>(json);
        var temporal = () => System.Text.Json.JsonSerializer.Deserialize<UpstreamProvider>(json);
        api.Should().Throw<Newtonsoft.Json.JsonException>();
        temporal.Should().Throw<System.Text.Json.JsonException>();
    }

    [Test]
    public void UnknownCatalogSourceRemainsReadableWithoutSelectingAProvider()
    {
        var source = JsonConvert.DeserializeObject<UpstreamSource>(
            "{\"name\":\"historical-source\"}"
        )!;
        source.Provider.Should().BeNull();
        JObject.Parse(JsonConvert.SerializeObject(source))["name"]!
            .Value<string>()
            .Should()
            .Be("historical-source");
    }

    [TestCase(null)]
    [TestCase("2000")]
    [TestCase("2000-XX-XX")]
    [TestCase("2000-01-01")]
    public void ArchiveSelectionRoundTripPreservesRawDateWithoutEvaluatingDerivedDates(
        string? rawDate
    )
    {
        // Discovery accepts incomplete dates. Temporal serialization must not evaluate
        // computed timestamps that require metadata absent from an upstream listing.
        var document = new Relisten.Vendor.ArchiveOrg.SearchDoc
        {
            identifier = "fixture-date",
            raw_display_date = rawDate!,
        };
        var converter = new DefaultPayloadConverter();
        var payload = converter.ToPayload(document);
        var restored = converter.ToValue<Relisten.Vendor.ArchiveOrg.SearchDoc>(payload);

        restored.identifier.Should().Be(document.identifier);
        restored.raw_display_date.Should().Be(rawDate);
        restored.addeddate.Should().BeNull();
        restored.publicdate.Should().BeNull();
        restored.updatedate.Should().BeNull();
    }

    [TestCaseSource(nameof(RefreshCommands))]
    public void NamedRefreshCommandsRoundTripThroughTheWorkflowInputUnion(
        ArtistRefreshCommand command
    )
    {
        var converter = new DefaultPayloadConverter();
        var payload = converter.ToPayload(command);
        var restored = converter.ToValue<ArtistRefreshCommand>(payload);

        restored.Should().Be(command);
        using var json = JsonDocument.Parse(payload.Data.ToStringUtf8());
        json.RootElement.GetProperty("kind").GetString().Should().NotBeNullOrEmpty();
        json.RootElement.GetProperty("Artist")
            .GetProperty("Value")
            .GetGuid()
            .Should()
            .Be(command.Artist.Value);
    }

    private static IEnumerable<ArtistRefreshCommand> RefreshCommands()
    {
        var artist = new ArtistId(Guid.Parse("7666a954-98a3-4e06-88a0-c5f37e726180"));
        yield return new RefreshArtist(artist);
        yield return new RefreshArchiveArtist(artist, 2000);
        yield return new RefreshPhishInArtist(artist, 1999);
        yield return new RefreshPhishNetArtist(artist, 2001);
        yield return new RefreshSetlistFmArtist(artist);
        yield return new RefreshPhantasyTourArtist(artist);
        yield return new RefreshJerryArtist(artist);
        yield return new RefreshLocalArtist(artist);
        yield return new RefreshFilteredArtist(artist, 2000);
    }

    [Test]
    public void UnknownRefreshCommandFailsAtTheTemporalPayloadBoundary()
    {
        var converter = new DefaultPayloadConverter();
        var payload = converter.ToPayload(new RefreshArtist(new ArtistId(Guid.NewGuid())));
        var json = System.Text.Json.Nodes.JsonNode.Parse(payload.Data.ToStringUtf8())!;
        json["kind"] = "unexpected";
        payload.Data = Google.Protobuf.ByteString.CopyFromUtf8(json.ToJsonString());

        var decode = () => converter.ToValue<ArtistRefreshCommand>(payload);

        decode.Should().Throw<System.Text.Json.JsonException>();
    }

    [Test]
    public void ArchiveItemVariantsAndSkipResultsRoundTripThroughTheTemporalConverter()
    {
        var id = new ArchiveItemId("fixture-2000-XX-XX");
        ArchiveItemInput[] variants =
        [
            new RequestedArchiveItem(id),
            new DiscoveredArchiveItem(
                new DiscoveredItem<ArchiveItemId, ArchiveSelectedItem>(
                    id,
                    new ArchiveSelectedItem(
                        new Relisten.Vendor.ArchiveOrg.SearchDoc
                        {
                            identifier = id.Value,
                            raw_display_date = "2000-XX-XX",
                        },
                        "new_source"
                    )
                )
            ),
        ];
        var converter = new DefaultPayloadConverter();
        foreach (var input in variants)
        {
            var restored = converter.ToValue<ArchiveItemInput>(converter.ToPayload(input));
            restored.GetType().Should().Be(input.GetType());
            if (restored is RequestedArchiveItem requested)
                requested.Item.Should().Be(id);
            else
            {
                var selected = ((DiscoveredArchiveItem)restored).Item;
                selected.Id.Should().Be(id);
                selected.Input.Document.identifier.Should().Be(id.Value);
                selected.Input.Document.raw_display_date.Should().Be("2000-XX-XX");
                selected.Input.Document.addeddate.Should().BeNull();
                selected.Input.Reason.Should().Be("new_source");
            }
        }
        var result = new ArchiveItemResult(
            id,
            ItemDisposition.Skipped,
            null,
            ArchiveSkipReason.UnrepairedDate,
            "invalid_date"
        );
        converter.ToValue<ArchiveItemResult>(converter.ToPayload(result)).Should().Be(result);
    }

    [Test]
    public void UpstreamKeyWireTypesPreserveNumbersAndLocalCalendarKeys()
    {
        var converter = new DefaultPayloadConverter();
        var show = new PhishInShowId(123);
        var local = new LocalShowDate("2000-XX-XX");
        var showPayload = converter.ToPayload(show);
        var localPayload = converter.ToPayload(local);

        converter.ToValue<PhishInShowId>(showPayload).Should().Be(show);
        converter.ToValue<LocalShowDate>(localPayload).Should().Be(local);
        using var showJson = JsonDocument.Parse(showPayload.Data.ToStringUtf8());
        using var localJson = JsonDocument.Parse(localPayload.Data.ToStringUtf8());
        showJson.RootElement.GetProperty("Value").GetInt32().Should().Be(123);
        localJson.RootElement.GetProperty("Value").GetString().Should().Be("2000-XX-XX");
    }

    [Test]
    public void LocalDiscoveryKeepsConsumedFieldsAndExcludesUnusedArbitraryTagNotes()
    {
        // Provider HTTP parsing retains Newtonsoft tokens. Only the directory and
        // catalog metadata consumed by Local import belong in durable activity inputs.
        var show = JsonConvert.DeserializeObject<Relisten.Vendor.Local.LocalShow>(
            """
            {"date":"2000-01-01","dir":"part-one","venue":"Fixture Venue",
             "city":"City","state":"State","updated_at":"2026-01-01T00:00:00Z",
             "tags":[{"name":"historical-note","notes":{"nested":[1,"value",null]}}]}
            """
        )!;
        show.tags.Should().ContainSingle().Which.notes.Should().BeOfType<JObject>();
        var converter = new DefaultPayloadConverter();
        var input = new DiscoveredItem<LocalShowDate, Relisten.Vendor.Local.LocalShow[]>(
            new LocalShowDate(show.date),
            [show]
        );

        var restored = converter.ToValue<
            DiscoveredItem<LocalShowDate, Relisten.Vendor.Local.LocalShow[]>
        >(converter.ToPayload(input));

        restored.Id.Should().Be(input.Id);
        var directory = restored.Input.Should().ContainSingle().Subject;
        directory.date.Should().Be(show.date);
        directory.dir.Should().Be(show.dir);
        directory.venue.Should().Be(show.venue);
        directory.city.Should().Be(show.city);
        directory.state.Should().Be(show.state);
        directory.updated_at.Should().Be(show.updated_at);
        directory.tags.Should().BeNull();
    }
}
