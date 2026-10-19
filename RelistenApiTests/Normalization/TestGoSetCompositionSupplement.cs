using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestGoSetCompositionSupplement
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string root = null!;

    [SetUp]
    public void SetUp() => root = Directory.CreateTempSubdirectory("go-set-supplement-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(root, true);

    [Test]
    public async Task NewGenerationRetainsEveryOriginalWitnessAndSeparatesProposedAttachmentFromProviderAttribution()
    {
        var fixture = await Prepare();
        var original = await new CompositionSelectionBatch().ReadProgramTargetsAsync(
            fixture.Original.Plan,
            fixture.Original.Sha
        );
        var originalManifest = await File.ReadAllBytesAsync(fixture.Original.Manifest);
        var result = await new GoSetCompositionSupplement().RunAsync(
            fixture.Request.Path,
            Path.Combine(root, "new"),
            fixture.Request.Sha256
        );
        var plan = JsonSerializer.Deserialize<CompositionSelectionPlan>(
            await File.ReadAllBytesAsync(result.PlanPath),
            Json
        )!;
        var after = await new CompositionSelectionBatch().ReadProgramTargetsAsync(
            result.PlanPath,
            Hash(await File.ReadAllBytesAsync(result.PlanPath))
        );
        after.Count.Should().Be(original.Count);
        plan.JobUuid.Should()
            .NotBe(
                JsonSerializer
                    .Deserialize<CompositionSelectionPlan>(
                        await File.ReadAllBytesAsync(fixture.Original.Plan),
                        Json
                    )!
                    .JobUuid
            );
        foreach (var target in original)
        {
            var next = after.Single(row => row.Context.Key == target.Context.Key).Context;
            JsonElement
                .DeepEquals(
                    JsonSerializer.SerializeToElement(
                        next.Witnesses.Take(target.Context.Witnesses.Count),
                        Json
                    ),
                    JsonSerializer.SerializeToElement(target.Context.Witnesses, Json)
                )
                .Should()
                .BeTrue(
                    "all original witness fields, including claim observations, must survive in order"
                );
        }
        var witness = after
            .Single(row => row.Context.Key == fixture.Proposal.Target)
            .Context.Witnesses.Last();
        witness.Kind.Should().Be("native_passage");
        witness.SourceUuid.Should().BeNull();
        witness.ShowUuid.Should().BeNull();
        witness.SetlistKey.Should().Be("go-set.net/show/1673482876");
        witness
            .Claims.Single(claim => claim.Kind == "performance_annotation")
            .Role.Should()
            .Be("cover_artist");
        var correspondence = witness
            .Claims.Single(claim => claim.Kind == "target_source_correspondence")
            .Observation;
        correspondence.GetProperty("acceptedIdentity").GetBoolean().Should().BeFalse();
        correspondence.GetProperty("reviewAuthority").GetBoolean().Should().BeFalse();
        correspondence
            .GetProperty("proposal")
            .GetProperty("conflicts")[0]
            .GetString()
            .Should()
            .Contain("Apr5/Apr12");
        (await File.ReadAllBytesAsync(fixture.Original.Manifest)).Should().Equal(originalManifest);
        result.AddedWitnesses.Should().Be(1);
    }

    [TestCase("source_span")]
    [TestCase("source_origin")]
    [TestCase("source_hash")]
    [TestCase("provider_span")]
    [TestCase("wrong_occurrence")]
    public async Task ChangedOrUnownedCorrespondenceCannotPublishAContext(string mutation)
    {
        var fixture = await Prepare();
        var proposal = fixture.Proposal;
        var proof = proposal.SourceEvidence.Single();
        proposal = mutation switch
        {
            "source_span" => proposal with
            {
                SourceEvidence = [proof with { EndCodePoint = proof.EndCodePoint - 1 }],
            },
            "source_origin" => proposal with
            {
                SourceEvidence = [proof with { Origin = proof.Origin with { Line = 999 } }],
            },
            "source_hash" => proposal with
            {
                SourceEvidence = [proof with { TextSha256 = new string('0', 64) }],
            },
            "provider_span" => proposal with
            {
                ProviderEvidence =
                [
                    proposal.ProviderEvidence.Single() with
                    {
                        Quote = "borrowed wording",
                    },
                ],
            },
            _ => proposal with { OccurrenceKeys = ["not-an-upstream-occurrence"] },
        };
        var request = fixture.Value with { Correspondences = [proposal] };
        var pin = await Write("changed.json", request);
        var output = Path.Combine(root, "invalid");
        var run = () => new GoSetCompositionSupplement().RunAsync(pin.Path, output, pin.Sha256);
        await run.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeFalse();
    }

    [Test]
    public async Task ChangedCapturedInputCannotReplaceItsRetainedDecodedEvidence()
    {
        var fixture = await Prepare();
        await File.AppendAllTextAsync(Path.Combine(root, "acquired", "000.input.json"), " ");
        var run = () =>
            new GoSetCompositionSupplement().RunAsync(
                fixture.Request.Path,
                Path.Combine(root, "invalid"),
                fixture.Request.Sha256
            );
        await run.Should().ThrowAsync<InvalidDataException>();
        Directory.Exists(Path.Combine(root, "invalid")).Should().BeFalse();
    }

    private async Task<Fixture> Prepare()
    {
        var original = await SelectionContextFixture.Create(root);
        var target = (
            await new CompositionSelectionBatch().ReadProgramTargetsAsync(
                original.Plan,
                original.Sha
            )
        ).First();
        var text = target.Context.Witnesses.Single().Texts.Single();
        var body = await File.ReadAllBytesAsync(
            Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "Fixtures",
                "normalization",
                "go-set",
                "song-such-a-night.html"
            )
        );
        using var client = new HttpClient(new ResponseHandler(body));
        var acquisitionRequest = await Write(
            "acquisition-request.json",
            new GoSetCompositionSourcesRequest(["HTTPS://GO-SET.NET/song/such-a-night"], 1)
        );
        var acquired = Path.Combine(root, "acquired");
        await new GoSetCompositionSources(client).RunAsync(
            acquisitionRequest.Path,
            acquired,
            acquisitionRequest.Sha256
        );
        var html = Encoding.UTF8.GetString(body);
        const string quote = "was originally performed by Dr. John.";
        var start = html[..html.IndexOf(quote, StringComparison.Ordinal)].EnumerateRunes().Count();
        var proposal = new GoSetTargetCorrespondenceProposal(
            target.Context.Key,
            new("go-set.net", "song", "34"),
            "such-a-night",
            ["18216"],
            [new(text.Origin, text.TextSha256, 0, text.Value.EnumerateRunes().Count(), text.Value)],
            [new("https://go-set.net/song/such-a-night", start, start + quote.Length, quote)],
            "Synthetic development correspondence for the public preservation contract; this is not accepted identity.",
            ["Apr5/Apr12 discrepancy remains unresolved"]
        );
        var request = new GoSetCompositionSupplementRequest(
            await Pin(original.Plan),
            await Pin(Path.Combine(acquired, "manifest.json")),
            [proposal],
            1
        );
        return new(original, request, proposal, await Write("supplement-request.json", request));
    }

    private async Task<MusicConsolidationArtifact> Write<T>(string name, T value)
    {
        var path = Path.Combine(root, name);
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));
        return await Pin(path);
    }

    private static async Task<MusicConsolidationArtifact> Pin(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        return new(path, bytes.LongLength, Hash(bytes), 1);
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record Fixture(
        SelectionContextFixture Original,
        GoSetCompositionSupplementRequest Value,
        GoSetTargetCorrespondenceProposal Proposal,
        MusicConsolidationArtifact Request
    );

    private sealed class ResponseHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            var result = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
                RequestMessage = request,
            };
            result.Content.Headers.ContentType = new("text/html") { CharSet = "UTF-8" };
            return Task.FromResult(result);
        }
    }
}
