using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestMusicBrainzAcquisitionBatch
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationMusicBrainzInputStore store = null!;
    private string directory = null!;
    private readonly List<HttpClient> clients = [];

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        store = new(fixture.Db);
    }

    [SetUp]
    public async Task Prepare()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "musicbrainz-batch-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        await ClearCooldown();
    }

    [TearDown]
    public void Cleanup()
    {
        foreach (var client in clients)
        {
            client.Dispose();
        }
        clients.Clear();
        Directory.Delete(directory, true);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task SuccessExportsExactRequestResponseAndInputWithVerifiableHashesAndTerminalReplay()
    {
        var request = Request();
        var body = Encoding.UTF8.GetBytes(
            $"{{\"id\":\"{request.Mbid:D}\",\"title\":\"literal café\"}}\n"
        );
        var handler = new Stub((_, _) => Task.FromResult(Response(body)));
        var batch = Batch(handler);
        var input = await WriteRequest(request);
        var command = Guid.NewGuid();

        var result = await batch.RunAsync(input, Output("first"), command);

        result.State.Should().Be("completed");
        handler.Calls.Should().Be(1);
        (await File.ReadAllBytesAsync(Path.Combine(result.OutputDirectory, "request.json")))
            .Should()
            .Equal(await File.ReadAllBytesAsync(input));
        var saved = (await store.GetAsync(command))!;
        var prefix = $"attempts/1-{saved.Attempts.Single().AttemptUuid:D}";
        (await File.ReadAllBytesAsync(Path.Combine(result.OutputDirectory, prefix, "body.bin")))
            .Should()
            .Equal(body);
        (await File.ReadAllBytesAsync(Path.Combine(result.OutputDirectory, prefix, "input.bin")))
            .Should()
            .Equal(body);
        using var response = JsonDocument.Parse(
            await File.ReadAllBytesAsync(
                Path.Combine(result.OutputDirectory, prefix, "response.json")
            )
        );
        response
            .RootElement.GetProperty("response")
            .GetProperty("body")
            .ValueKind.Should()
            .Be(JsonValueKind.Null);
        response.RootElement.GetProperty("sha256").GetString().Should().Be(Hash(body));
        using var decoded = JsonDocument.Parse(
            await File.ReadAllBytesAsync(
                Path.Combine(result.OutputDirectory, prefix, "decoded.json")
            )
        );
        decoded
            .RootElement.GetProperty("musicBrainzWorks")
            .EnumerateArray()
            .Should()
            .ContainSingle();
        await CheckArtifactHashes(result.OutputDirectory);

        var replay = await batch.RunAsync(input, Output("replay"), command);
        replay.State.Should().Be("completed");
        handler.Calls.Should().Be(1);
        await CheckArtifactHashes(replay.OutputDirectory);
    }

    [Test]
    public async Task PendingInvocationReturnsWithoutSleepingThenResumesAndPeerCacheNeedsNoGet()
    {
        var request = Request();
        var body = Encoding.UTF8.GetBytes($"{{\"id\":\"{request.Mbid:D}\"}}");
        var handler = new Stub((_, _) => Task.FromResult(Response(body)));
        var batch = Batch(handler);
        var input = await WriteRequest(request);
        var command = Guid.NewGuid();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE normalization.musicbrainz_gate SET not_before=clock_timestamp()+interval '1 hour' WHERE singleton"
            )
        );

        var pending = await batch.RunAsync(input, Output("pending"), command);
        pending.State.Should().Be("pending");
        pending.NotBefore.Should().NotBeNull();
        handler.Calls.Should().Be(0);
        File.Exists(Path.Combine(pending.OutputDirectory, "manifest.json")).Should().BeTrue();
        await ClearCooldown();
        (await batch.RunAsync(input, Output("resumed"), command)).State.Should().Be("completed");
        handler.Calls.Should().Be(1);

        var cachedInput = await WriteRequest(
            request with
            {
                CacheMode = MusicBrainzCacheMode.ReuseFresh,
                MaximumCacheAge = TimeSpan.FromHours(1),
            },
            "cache.json"
        );
        var cached = await batch.RunAsync(cachedInput, Output("cached"), Guid.NewGuid());
        cached.State.Should().Be("completed");
        handler.Calls.Should().Be(1);
        (await File.ReadAllBytesAsync(Path.Combine(cached.OutputDirectory, "cached", "input.bin")))
            .Should()
            .Equal(body);
        await CheckArtifactHashes(cached.OutputDirectory);
    }

    [Test]
    public async Task ChangedCommandRequestConflictsWithoutAnotherGetOrCompletionMarker()
    {
        var request = Request();
        var handler = new Stub(
            (_, _) =>
                Task.FromResult(
                    Response(Encoding.UTF8.GetBytes($"{{\"id\":\"{request.Mbid:D}\"}}"))
                )
        );
        var batch = Batch(handler);
        var command = Guid.NewGuid();
        await batch.RunAsync(await WriteRequest(request), Output("original"), command);
        var changed = await WriteRequest(Request(), "changed.json");

        var run = () => batch.RunAsync(changed, Output("conflict"), command);
        await run.Should().ThrowAsync<NormalizationConflictException>();
        handler.Calls.Should().Be(1);
        File.Exists(Path.Combine(Output("conflict"), "manifest.json")).Should().BeFalse();
        File.Exists(Path.Combine(Output("conflict"), "interruption.json")).Should().BeTrue();
        (await store.GetAsync(command))!.Request.Should().Be(request);
    }

    [TestCase("unknown-kind")]
    [TestCase("missing-age")]
    [TestCase("oversized")]
    [TestCase("duplicate")]
    [TestCase("nonobject")]
    [TestCase("numeric-enum")]
    public async Task InvalidLocalRequestIsRejectedBeforeAdmissionOrNetwork(string malformed)
    {
        var request = Request();
        var input = await WriteRequest(request);
        var bytes = malformed switch
        {
            "unknown-kind" => "{\"kind\":\"search\",\"mbid\":\""
                + request.Mbid
                + "\",\"cacheMode\":\"refresh\"}",
            "missing-age" => "{\"kind\":\"work\",\"mbid\":\""
                + request.Mbid
                + "\",\"cacheMode\":\"reuseFresh\"}",
            "nonobject" => "[]",
            "numeric-enum" => "{\"kind\":0,\"mbid\":\""
                + request.Mbid
                + "\",\"cacheMode\":\"refresh\"}",
            "oversized" => new string(' ', 65537),
            _ => "{\"kind\":\"work\",\"kind\":\"artist\",\"mbid\":\""
                + request.Mbid
                + "\",\"cacheMode\":\"refresh\"}",
        };
        await File.WriteAllTextAsync(input, bytes);
        var handler = new Stub(
            (_, _) => throw new AssertionException("Invalid request reached HTTP.")
        );
        var command = Guid.NewGuid();
        var run = () => Batch(handler).RunAsync(input, Output("invalid"), command);

        await run.Should().ThrowAsync<InvalidDataException>();
        handler.Calls.Should().Be(0);
        (await store.GetAsync(command)).Should().BeNull();
        Directory.Exists(Output("invalid")).Should().BeFalse();
    }

    [Test]
    public async Task ExistingOutputIsNeverChangedAndCannotAdmitACommand()
    {
        var input = await WriteRequest(Request());
        var output = Output("occupied");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "keep.txt"), "original");
        var handler = new Stub(
            (_, _) => throw new AssertionException("Output collision reached HTTP.")
        );
        var command = Guid.NewGuid();
        var run = () => Batch(handler).RunAsync(input, output, command);

        await run.Should().ThrowAsync<IOException>();
        handler.Calls.Should().Be(0);
        (await store.GetAsync(command)).Should().BeNull();
        (await File.ReadAllTextAsync(Path.Combine(output, "keep.txt"))).Should().Be("original");
        Directory.GetFiles(output).Should().ContainSingle();
    }

    [Test]
    public async Task FailedHttpReceiptRetainsExactBodyWithoutReplacingCurrentInput()
    {
        var request = Request();
        var oldBody = Encoding.UTF8.GetBytes(
            $"{{\"id\":\"{request.Mbid:D}\",\"title\":\"prior\"}}"
        );
        var resource = new NormalizationResourceKey(
            "musicbrainz.org",
            "work",
            request.Mbid.ToString("D")
        );
        var before = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                oldBody,
                "application/json",
                "musicbrainz-work-json-v1",
                0,
                DateTimeOffset.UtcNow
            )
        );
        var failure = Encoding.UTF8.GetBytes("{\"error\":\"not found\"}\n");
        var handler = new Stub(
            (_, _) => Task.FromResult(Response(failure, HttpStatusCode.NotFound))
        );

        var report = await Batch(handler)
            .RunAsync(await WriteRequest(request), Output("failed"), Guid.NewGuid());

        report.State.Should().Be("failed");
        var bodyFile = Directory
            .GetFiles(report.OutputDirectory, "body.bin", SearchOption.AllDirectories)
            .Single();
        (await File.ReadAllBytesAsync(bodyFile)).Should().Equal(failure);
        Directory
            .GetFiles(report.OutputDirectory, "input.bin", SearchOption.AllDirectories)
            .Should()
            .BeEmpty();
        var after = (await evidence.GetCurrentInputAsync(resource))!;
        after.Generation.Should().Be(before.Fetch.ResourceGeneration);
        after.Revision.Uuid.Should().Be(before.Revision.Uuid);
        await CheckArtifactHashes(report.OutputDirectory);
    }

    [Test]
    public async Task CancellationAfterDispatchRetainsRecoverableAuditWithoutFinalManifest()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new Stub(
            (_, _) =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
        );
        var command = Guid.NewGuid();
        var input = await WriteRequest(Request());
        var run = () =>
            Batch(handler).RunAsync(input, Output("cancelled"), command, cancellation.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        handler.Calls.Should().Be(1);
        File.Exists(Path.Combine(Output("cancelled"), "manifest.json")).Should().BeFalse();
        File.Exists(Path.Combine(Output("cancelled"), "interruption.json")).Should().BeTrue();
        var saved = (await store.GetAsync(command))!;
        saved.State.Should().Be(MusicBrainzAcquisitionState.Pending);
        (await store.GetResponseAsync(saved.Attempts.Single().AttemptUuid)).Should().NotBeNull();
        Directory
            .GetFiles(
                Path.Combine(Output("cancelled"), "recovery"),
                "response.json",
                SearchOption.AllDirectories
            )
            .Should()
            .ContainSingle();
    }

    [Test]
    public async Task UnexpectedTransportFailureLeavesDurableIntentAndAuditWithoutCompletionMarker()
    {
        var handler = new Stub(
            (_, _) => throw new InvalidOperationException("controlled transport interruption")
        );
        var command = Guid.NewGuid();
        var input = await WriteRequest(Request());
        var run = () => Batch(handler).RunAsync(input, Output("interrupted"), command);

        await run.Should().ThrowAsync<InvalidOperationException>();
        handler.Calls.Should().Be(1);
        File.Exists(Path.Combine(Output("interrupted"), "manifest.json")).Should().BeFalse();
        File.Exists(Path.Combine(Output("interrupted"), "interruption.json")).Should().BeTrue();
        var saved = (await store.GetAsync(command))!;
        saved.State.Should().Be(MusicBrainzAcquisitionState.Pending);
        saved.Attempts.Should().ContainSingle().Which.Phase.Should().Be("DispatchStarted");
        (await store.GetResponseAsync(saved.Attempts.Single().AttemptUuid)).Should().BeNull();

        var resumed = await Batch(handler).RunAsync(input, Output("still-pending"), command);
        resumed.State.Should().Be("pending");
        handler.Calls.Should().Be(1);
    }

    private MusicBrainzAcquisitionBatch Batch(Stub handler)
    {
        var client = new HttpClient(handler);
        clients.Add(client);
        return new(new(client, store, evidence, new()), store, evidence);
    }

    private Task ClearCooldown() =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE normalization.musicbrainz_gate SET attempt_uuid=NULL,token=NULL,lease_until=NULL,not_before=clock_timestamp()-interval '10 seconds' WHERE singleton"
            )
        );

    private string Output(string name) => Path.Combine(directory, name);

    private static MusicBrainzLookupRequest Request() =>
        new(MusicBrainzLookupKind.Work, Guid.NewGuid(), MusicBrainzCacheMode.Refresh);

    private async Task<string> WriteRequest(
        MusicBrainzLookupRequest request,
        string name = "lookup.json"
    )
    {
        var path = Path.Combine(directory, name);
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(request, Json));
        return path;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task CheckArtifactHashes(string directory)
    {
        using var manifest = JsonDocument.Parse(
            await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json"))
        );
        foreach (var artifact in manifest.RootElement.GetProperty("artifacts").EnumerateObject())
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, artifact.Name));
            bytes.LongLength.Should().Be(artifact.Value.GetProperty("bytes").GetInt64());
            Hash(bytes).Should().Be(artifact.Value.GetProperty("sha256").GetString());
        }
    }

    private static HttpResponseMessage Response(
        byte[] bytes,
        HttpStatusCode code = HttpStatusCode.OK
    )
    {
        var response = new HttpResponseMessage(code) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    private sealed class Stub(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send
    ) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            Calls++;
            var response = await send(request, ct);
            response.RequestMessage = request;
            return response;
        }
    }
}
