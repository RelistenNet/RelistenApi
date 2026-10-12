using System.Data;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Relisten.Api.Models;
using Relisten.Ingestion;
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
        var request = new ArtistImportRequest("artist", "request", Provider: provider);
        var apiJson = JsonConvert.SerializeObject(request);
        JObject.Parse(apiJson)["Provider"]!.Value<string>().Should().Be(storedName);
        JsonConvert.DeserializeObject<ArtistImportRequest>(apiJson).Should().Be(request);

        var converter = new DefaultPayloadConverter();
        var payload = converter.ToPayload(request);
        using var temporalJson = JsonDocument.Parse(payload.Data.ToStringUtf8());
        temporalJson.RootElement.GetProperty("Provider").GetString().Should().Be(storedName);
        converter.ToValue<ArtistImportRequest>(payload).Should().Be(request);
        var checkpoint = new ImportCheckpoint(Providers: [provider]);
        converter
            .ToValue<ImportCheckpoint>(converter.ToPayload(checkpoint))
            .Should()
            .BeEquivalentTo(checkpoint);

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
}
