using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestGoSetCompositionSources
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string root = null!;

    [SetUp]
    public void SetUp() => root = Directory.CreateTempSubdirectory("go-set-acquisition-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(root, true);

    [Test]
    public async Task OriginalHtmlAndTypedRevisionAreRetainedWithoutCatalogOrWorkBinding()
    {
        var body = await File.ReadAllBytesAsync(
            Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "Fixtures",
                "normalization",
                "go-set",
                "song-such-a-night.html"
            )
        );
        using var handler = new ResponseHandler(_ => Response(HttpStatusCode.OK, body));
        using var client = new HttpClient(handler);
        var (request, sha) = await Request("https://go-set.net/song/such-a-night");
        var output = Path.Combine(root, "output");
        var result = await new GoSetCompositionSources(client).RunAsync(request, output, sha);
        var capture = result.Captures.Should().ContainSingle().Subject;
        capture
            .Resource.Should()
            .Be(new NormalizationResourceKey("go-set.net", "song_page", "such-a-night"));
        var revision = JsonSerializer.Deserialize<NormalizationInputRevision>(
            await File.ReadAllBytesAsync(Path.Combine(output, capture.Input.Path)),
            Json
        )!;
        revision.Payload.Should().Equal(body);
        revision.Sha256.Should().Be(Hash(body));
        revision.Uuid.Should().Be(capture.InputRevisionUuid);
        var decoded = JsonDocument.Parse(
            await File.ReadAllBytesAsync(Path.Combine(output, capture.Decoded.Path))
        );
        decoded.RootElement.GetProperty("inputRevisionUuid").GetGuid().Should().Be(revision.Uuid);
        capture.Body.Sha256.Should().Be(Hash(body));
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeTrue();
        handler.Calls.Should().Be(1);
    }

    [Test]
    public async Task EntireLiteralQueueAndRequestPinAreValidatedBeforeAnyHttpConsultation()
    {
        using var handler = new ResponseHandler(_ =>
            throw new AssertionException("Unexpected HTTP call")
        );
        using var client = new HttpClient(handler);
        var (request, sha) = await Request(
            "https://go-set.net/song/such-a-night",
            "https://go-set.net/setlists/?date=2023-04-12&other=1"
        );
        var run = () =>
            new GoSetCompositionSources(client).RunAsync(
                request,
                Path.Combine(root, "invalid"),
                sha
            );
        await run.Should().ThrowAsync<ArgumentException>();
        var changedPin = () =>
            new GoSetCompositionSources(client).RunAsync(
                request,
                Path.Combine(root, "pin"),
                new string('0', 64)
            );
        await changedPin.Should().ThrowAsync<InvalidDataException>();
        handler.Calls.Should().Be(0);
    }

    [Test]
    public async Task FailureRetainsOriginalResponseButCannotPublishOrOverwriteItsPrefix()
    {
        using var handler = new ResponseHandler(_ =>
            Response(HttpStatusCode.ServiceUnavailable, "original upstream failure"u8.ToArray())
        );
        using var client = new HttpClient(handler);
        var (request, sha) = await Request("https://go-set.net/song/such-a-night");
        var output = Path.Combine(root, "output");
        var run = () => new GoSetCompositionSources(client).RunAsync(request, output, sha);
        await run.Should().ThrowAsync<InvalidDataException>();
        (await File.ReadAllTextAsync(Path.Combine(output, "000.html")))
            .Should()
            .Be("original upstream failure");
        File.Exists(Path.Combine(output, "000.receipt.json")).Should().BeTrue();
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeFalse();
        await run.Should().ThrowAsync<ArgumentException>();
        handler.Calls.Should().Be(1);
    }

    [Test]
    public async Task RedirectAndOversizedBodiesCannotBecomeCompletedProviderRevisions()
    {
        using var handler = new ResponseHandler(_ =>
            Response(HttpStatusCode.Found, "redirect"u8.ToArray())
        );
        using var client = new HttpClient(handler);
        var (request, sha) = await Request("https://go-set.net/song/such-a-night");
        var run = () =>
            new GoSetCompositionSources(client).RunAsync(
                request,
                Path.Combine(root, "redirect"),
                sha
            );
        await run.Should().ThrowAsync<InvalidDataException>();
        using var hugeHandler = new ResponseHandler(_ =>
            Response(HttpStatusCode.OK, new byte[1024 * 1024 + 1])
        );
        using var hugeClient = new HttpClient(hugeHandler);
        var hugeRun = () =>
            new GoSetCompositionSources(hugeClient).RunAsync(
                request,
                Path.Combine(root, "huge"),
                sha
            );
        await hugeRun.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(root, "huge", "manifest.json")).Should().BeFalse();
        File.Exists(Path.Combine(root, "redirect", "000.input.json")).Should().BeFalse();
    }

    private async Task<(string Path, string Sha)> Request(params string[] urls)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new GoSetCompositionSourcesRequest(urls, 1),
            Json
        );
        var path = Path.Combine(root, Guid.NewGuid() + ".json");
        await File.WriteAllBytesAsync(path, bytes);
        return (path, Hash(bytes));
    }

    private static HttpResponseMessage Response(HttpStatusCode code, byte[] body)
    {
        var response = new HttpResponseMessage(code) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new("text/html") { CharSet = "UTF-8" };
        return response;
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Calls++;
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
