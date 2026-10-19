using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Relisten;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>Exercises the operator boundary with a complete, pinned synthetic hypothesis image.</summary>
[TestFixture]
public sealed class TestCompositionSelectionBatch
{
    private string directory = null!;

    [SetUp]
    public void Start() =>
        directory = Directory.CreateTempSubdirectory("composition-selection-").FullName;

    [TearDown]
    public void Stop() => Directory.Delete(directory, true);

    [Test]
    public async Task PurePreflightCountsTheFullUniverseAndKeepsAnExplicitMissingSelection()
    {
        var fixture = await SelectionFixture.Create(directory, includeMissing: true);
        var output = Path.Combine(directory, "preflight");
        var report = await new CompositionSelectionBatch().RunAsync(
            fixture.Plan,
            output,
            fixture.Sha,
            preflightOnly: true
        );

        report.State.Should().Be("preflight_completed");
        report.Preflight.HypothesisSongs.Should().Be(3);
        report.Preflight.CandidateBearingSongs.Should().Be(2);
        report.Preflight.SelectedInputs.Should().Be(3);
        report.Preflight.EligibleInputs.Should().Be(2);
        report.Preflight.RemainingCandidateBearingSongs.Should().Be(0);
        report.Preflight.SkipReasons["missing_candidate_bearing_hypothesis"].Should().Be(1);
        report
            .Preflight.Rows.Where(row => row.Request != null)
            .Should()
            .OnlyContain(row => row.Request!.SuppliedCandidates == 1);
        report.ProcessedInputs.Should().Be(0);
        report.JobAccountedUsd.Should().BeNull();
        report.PublicationPermitted.Should().BeFalse();
        Directory.Exists(Path.Combine(output, "composition_selections")).Should().BeFalse();
    }

    [Test]
    public async Task EnrichedPreflightUsesEveryExactSelectedContextWithoutAdmittingPaidWork()
    {
        var fixture = await SelectionContextFixture.Create(directory);
        var output = Path.Combine(directory, "enriched-preflight");
        var report = await new CompositionSelectionBatch().RunAsync(
            fixture.Plan,
            output,
            fixture.Sha,
            preflightOnly: true
        );

        report.State.Should().Be("preflight_completed");
        report.Method.Should().Be("luna-composition-hypothesis-v4");
        report.Preflight.SelectedInputs.Should().Be(3);
        report.Preflight.EligibleInputs.Should().Be(2);
        report.Preflight.SkipReasons["missing_candidate_bearing_hypothesis"].Should().Be(1);
        report
            .Preflight.Rows.Where(row => row.Request != null)
            .Should()
            .OnlyContain(row =>
                row.Request!.Method == "luna-composition-hypothesis-v4"
                && row.Request.WithinRequestLimit
            );
        report.ProcessedInputs.Should().Be(0);
        report.JobAccountedUsd.Should().BeNull();
        report.PublicationPermitted.Should().BeFalse();
        Directory.Exists(Path.Combine(output, "composition_selections")).Should().BeFalse();
    }

    [TestCase("source_plan_keys")]
    [TestCase("hypothesis_generation")]
    [TestCase("music_generation")]
    [TestCase("missing_target")]
    [TestCase("wrong_target_key")]
    [TestCase("missing_text")]
    [TestCase("origin_outside_chunk")]
    public async Task AuthenticatedButMismatchedContextCannotProducePreflightOrAdmission(
        string mutation
    )
    {
        var fixture = await SelectionContextFixture.Create(directory);
        fixture = await fixture.ChangeContext(mutation);
        var output = Path.Combine(directory, "mismatched-context");
        var run = () =>
            new CompositionSelectionBatch().RunAsync(
                fixture.Plan,
                output,
                fixture.Sha,
                preflightOnly: true
            );

        await run.Should().ThrowAsync<InvalidDataException>();
        Directory.Exists(output).Should().BeFalse();
    }

    [TestCase("targets")]
    [TestCase("texts")]
    [TestCase("witness")]
    [TestCase("source_manifest")]
    [TestCase("catalogue")]
    [TestCase("normalization")]
    public async Task ChangedContextSourcePinsRefuseBeforeAnyOutput(string artifact)
    {
        var fixture = await SelectionContextFixture.Create(directory);
        await File.AppendAllTextAsync(fixture.Artifacts[artifact], "changed");
        var output = Path.Combine(directory, "changed-context");
        var run = () =>
            new CompositionSelectionBatch().RunAsync(
                fixture.Plan,
                output,
                fixture.Sha,
                preflightOnly: true
            );

        await run.Should().ThrowAsync<InvalidDataException>();
        Directory.Exists(output).Should().BeFalse();
    }

    [Test]
    public async Task EnrichedPlanCannotSilentlyFallBackWhenItsContextManifestIsMissing()
    {
        var fixture = await SelectionContextFixture.Create(directory);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var plan = JsonSerializer.Deserialize<CompositionSelectionPlan>(
            await File.ReadAllBytesAsync(fixture.Plan),
            json
        )!;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            plan with
            {
                TargetContextManifest = null,
            },
            json
        );
        await File.WriteAllBytesAsync(fixture.Plan, bytes);
        var output = Path.Combine(directory, "missing-context");
        var run = () =>
            new CompositionSelectionBatch().RunAsync(
                fixture.Plan,
                output,
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                preflightOnly: true
            );

        await run.Should().ThrowAsync<ArgumentException>();
        Directory.Exists(output).Should().BeFalse();
    }

    [Test]
    public async Task MeasuredControlMetadataCannotBeWrittenBeyondTheOutputBudget()
    {
        var fixture = await SelectionFixture.Create(
            directory,
            maximumOutputBytes: 2 * 1024 * 1024,
            selectedCount: 9000
        );
        var output = Path.Combine(directory, "bounded");
        var run = () =>
            new CompositionSelectionBatch().RunAsync(
                fixture.Plan,
                output,
                fixture.Sha,
                preflightOnly: true
            );

        await run.Should().ThrowAsync<IOException>();
        Directory.GetFiles(output).Should().BeEmpty();
    }
}

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestCompositionSelectionBatchLocal
{
    private string directory = null!;
    private NormalizationExtractionCacheService cache = null!;
    private string? previousWrite;
    private string? previousRead;

    [OneTimeSetUp]
    public void StartDatabase()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Run the dedicated local database tests.");
        }
        previousWrite = DbService.ConnStr;
        previousRead = DbService.ReadOnlyConnStr;
        cache = new(
            new DbService(
                LocalTestDatabase.DatabaseUrl,
                null,
                null,
                Host.CreateApplicationBuilder().Environment,
                NullLogger<DbService>.Instance
            )
        );
    }

    [OneTimeTearDown]
    public void StopDatabase()
    {
        if (previousWrite != null)
        {
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead!;
        }
    }

    [SetUp]
    public void Start() =>
        directory = Directory.CreateTempSubdirectory("composition-selection-local-").FullName;

    [TearDown]
    public void Stop() => Directory.Delete(directory, true);

    [Test]
    public async Task InterruptedExecutionResumesItsExactPrefixWithoutRepeatingACompletedSelection()
    {
        var fixture = await SelectionFixture.Create(directory, selectedCount: 8, concurrency: 4);
        var output = Path.Combine(directory, "execution");
        using var handler = new ConcurrentResponses(fixture.WorkId);
        using var http = new HttpClient(handler);
        var selector = new Selecting(
            new CachedCompositionHypothesisSelector(
                new LunaCompositionHypothesisSelector(http, cache, () => "local-fixture"),
                fixture.JobUuid
            )
        );
        var batch = new CompositionSelectionBatch(cache, selector);
        var interrupted = () => batch.RunAsync(fixture.Plan, output, fixture.Sha);
        await interrupted.Should().ThrowAsync<IOException>();
        handler.Calls.Should().Be(7);
        handler.MaximumInFlight.Should().Be(4);
        handler.InFlight.Should().Be(0);
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeFalse();

        var completion = await batch.RunAsync(fixture.Plan, output, fixture.Sha, resume: true);
        handler.Calls.Should().Be(8);
        completion.ProcessedInputs.Should().Be(8);
        completion.CacheHits.Should().Be(3);
        completion.Outcomes["selected"].Should().Be(8);
        var publishedKeys = new List<string>();
        foreach (var artifact in completion.Chunks)
        {
            await using var stream = new GZipStream(
                File.OpenRead(Path.Combine(output, artifact.Path)),
                CompressionMode.Decompress
            );
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync() is { } line)
            {
                using var row = JsonDocument.Parse(line);
                publishedKeys.Add(
                    row.RootElement.GetProperty("proposal")
                        .GetProperty("input")
                        .GetProperty("key")
                        .GetProperty("songEntityKey")
                        .GetString()!
                );
            }
        }
        publishedKeys.Should().Equal(Enumerable.Range(0, 8).Select(index => "repertoire:" + index));
        var again = await batch.RunAsync(fixture.Plan, output, fixture.Sha, resume: true);
        handler.Calls.Should().Be(8);
        again.Should().BeEquivalentTo(completion);

        var chunk = Path.Combine(output, completion.Chunks[0].Path);
        string text;
        await using (var stream = new GZipStream(File.OpenRead(chunk), CompressionMode.Decompress))
        using (var reader = new StreamReader(stream))
        {
            text = await reader.ReadToEndAsync();
        }
        await using (var stream = new GZipStream(File.Create(chunk), CompressionLevel.SmallestSize))
        await using (var writer = new StreamWriter(stream))
        {
            await writer.WriteAsync(
                text.Replace("Supplied retained candidate", "Changed rationale")
            );
        }
        var resume = () => batch.RunAsync(fixture.Plan, output, fixture.Sha, resume: true);
        await resume.Should().ThrowAsync<InvalidDataException>();
        handler.Calls.Should().Be(8);
    }

    private sealed class Selecting(ICompositionHypothesisSelector inner)
        : ICompositionHypothesisSelector
    {
        private bool failed;

        public async Task<CompositionHypothesisSelection> SelectAsync(
            CompositionHypothesisSong song,
            IReadOnlyList<RetainedCompositionWork> suppliedCandidates,
            CancellationToken ct = default
        )
        {
            if (song.Song.EntityKey == "repertoire:4" && !failed)
            {
                failed = true;
                throw new IOException("Synthetic interruption before a provider result.");
            }
            return await inner.SelectAsync(song, suppliedCandidates, ct);
        }
    }

    private sealed class ConcurrentResponses(Guid workId) : HttpMessageHandler
    {
        private readonly TaskCompletionSource firstBatch = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly object sync = new();
        private int calls;
        private int inFlight;
        private int maximumInFlight;
        public int Calls => calls;
        public int InFlight => inFlight;
        public int MaximumInFlight => maximumInFlight;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            int call;
            lock (sync)
            {
                call = ++calls;
                inFlight++;
                maximumInFlight = Math.Max(maximumInFlight, inFlight);
            }
            try
            {
                if (call <= 4)
                {
                    if (call == 4)
                    {
                        firstBatch.TrySetResult();
                    }
                    await firstBatch.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                    await Task.Delay((5 - call) * 10, ct);
                }
                var selection = JsonSerializer.Serialize(
                    new
                    {
                        selectedWorkMbid = workId,
                        alternativeWorkMbids = Array.Empty<Guid>(),
                        outcome = "selected",
                        rationale = "Supplied retained candidate",
                    }
                );
                var body = JsonSerializer.Serialize(
                    new
                    {
                        status = "completed",
                        usage = new { input_tokens = 100, output_tokens = 20 },
                        output = new[]
                        {
                            new
                            {
                                type = "message",
                                content = new[] { new { type = "output_text", text = selection } },
                            },
                        },
                    }
                );
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            }
            finally
            {
                lock (sync)
                {
                    inFlight--;
                }
            }
        }
    }
}

internal sealed record SelectionFixture(
    string Plan,
    string Sha,
    string Chunk,
    Guid WorkId,
    Guid JobUuid
)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    internal static async Task<SelectionFixture> Create(
        string directory,
        bool includeMissing = false,
        long maximumOutputBytes = 64 * 1024 * 1024,
        int selectedCount = 2,
        int concurrency = 1
    )
    {
        var retained = Path.Combine(directory, "retained");
        Directory.CreateDirectory(retained);
        var id = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, title = "Retained song" });
        var index = new RetainedCompositionCandidateIndex();
        index.Observe(
            new NormalizationInputRevision
            {
                Uuid = Guid.NewGuid(),
                Resource = new("musicbrainz.org", "work", id.ToString()),
                Payload = payload,
                Sha256 = Hash(payload),
                MediaType = "application/json",
                CaptureVersion = "musicbrainz-work-json-v1",
                CapturedAt = DateTime.UtcNow,
            }
        );
        index.CompleteObservation();
        var projector = new CompositionHypothesisProjector(index);
        var hypotheses = Enumerable
            .Range(0, selectedCount)
            .Select(number =>
            {
                var artist = Guid.NewGuid();
                return projector.Project(
                    new CompositionHypothesisSong(
                        "catalogue",
                        artist,
                        new("repertoire:" + number, artist, null, "Retained song", "title_derived"),
                        [],
                        [],
                        new("/synthetic", "source.jsonl.gz", 1, 1, "/title", new string('a', 64)),
                        1
                    )
                );
            })
            .ToArray();
        var rows = index
            .Works.Select(work => new
            {
                domain = "retained_work_candidates",
                inputKey = work.EntityKey,
                proposal = (object)work,
            })
            .Concat(
                hypotheses.Select(hypothesis => new
                {
                    domain = "composition_hypotheses",
                    inputKey = hypothesis.Song.Song.EntityKey,
                    proposal = (object)hypothesis,
                })
            )
            .ToList();
        var missingArtist = Guid.NewGuid();
        var missing = new CompositionSelectionSongKey(
            "catalogue",
            missingArtist,
            "repertoire:missing"
        );
        if (includeMissing)
        {
            var noCandidates = projector.Project(
                new CompositionHypothesisSong(
                    "catalogue",
                    missingArtist,
                    new(missing.SongEntityKey, missingArtist, null, "Other title", "title_derived"),
                    [],
                    [],
                    new("/synthetic", "source.jsonl.gz", 1, 1, "/title", new string('a', 64)),
                    1
                )
            );
            rows.Add(
                new
                {
                    domain = "composition_hypotheses",
                    inputKey = missing.SongEntityKey,
                    proposal = (object)noCandidates,
                }
            );
        }
        var chunk = Path.Combine(retained, "results.jsonl.gz");
        await using (var stream = new GZipStream(File.Create(chunk), CompressionLevel.SmallestSize))
        await using (var writer = new StreamWriter(stream))
        {
            foreach (var row in rows)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(row, Json));
            }
        }
        var manifest = new CompositionHypothesisOutput(
            CompositionHypothesisBatch.Version,
            true,
            new string('b', 64),
            "synthetic-decoder",
            RetainedCompositionCandidateIndex.Version,
            CompositionHypothesisProjector.Version,
            selectedCount + (includeMissing ? 1 : 0),
            1,
            new Dictionary<string, long> { ["hypotheses"] = selectedCount },
            [await Pin(chunk, rows.Count) with { Path = Path.GetFileName(chunk) }],
            null!,
            null!
        );
        var manifestPath = Path.Combine(retained, "manifest.json");
        await File.WriteAllBytesAsync(
            manifestPath,
            JsonSerializer.SerializeToUtf8Bytes(manifest, Json)
        );
        var keys = hypotheses
            .Select(h => new CompositionSelectionSongKey(
                h.Song.Scope,
                h.Song.ArtistUuid,
                h.Song.Song.EntityKey
            ))
            .ToList();
        if (includeMissing)
        {
            keys.Add(missing);
        }
        var plan = new CompositionSelectionPlan(
            CompositionSelectionBatch.PlanVersion,
            await Pin(manifestPath, 1),
            Guid.NewGuid(),
            "initial",
            keys,
            keys.Count,
            1m,
            keys.Count,
            120,
            maximumOutputBytes,
            0,
            concurrency
        );
        var bytes = JsonSerializer.SerializeToUtf8Bytes(plan, Json);
        var planPath = Path.Combine(directory, "plan.json");
        await File.WriteAllBytesAsync(planPath, bytes);
        return new(planPath, Hash(bytes), chunk, id, plan.JobUuid);
    }

    private static async Task<MusicConsolidationArtifact> Pin(string path, long rows) =>
        new(path, new FileInfo(path).Length, Hash(await File.ReadAllBytesAsync(path)), rows);

    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
}

/// <summary>A frozen exporter-shaped dependency chain over the public selection fixture.</summary>
internal sealed record SelectionContextFixture(
    string Plan,
    string Sha,
    string Manifest,
    IReadOnlyDictionary<string, string> Artifacts
)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
    private const string Literal = "<p>Été 🌙 — observed performer, not an inferred composer.</p>";

    internal static async Task<SelectionContextFixture> Create(string directory)
    {
        var fixture = await SelectionFixture.Create(directory, includeMissing: true);
        var basePlan = JsonSerializer.Deserialize<CompositionSelectionPlan>(
            await File.ReadAllBytesAsync(fixture.Plan),
            Json
        )!;
        var root = Directory.CreateDirectory(Path.Combine(directory, "target-context")).FullName;
        var sourceRun = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        var witness = Path.Combine(sourceRun, "observations.jsonl.gz");
        await WriteRows(
            witness,
            basePlan.Songs.Select(song =>
                JsonSerializer.Serialize(
                    new
                    {
                        name = "Observed performing artist",
                        title = song.SongEntityKey == "repertoire:missing"
                            ? "Other title"
                            : "Retained song",
                        description = Literal,
                        role = "performer",
                    },
                    Json
                )
            )
        );
        var witnessPin = await Pin(witness, basePlan.Songs.Count);
        CompositionSelectionTargetOrigin Origin(int line, string pointer) =>
            new(sourceRun, Path.GetFileName(witness), line, witnessPin.Sha256, pointer);
        var contexts = basePlan
            .Songs.Select(
                (song, index) =>
                    new CompositionSelectionTargetContext(
                        new(song.Scope, song.ArtistUuid, song.SongEntityKey),
                        new(
                            "Observed performing artist",
                            null,
                            [
                                new(
                                    "name",
                                    JsonSerializer.SerializeToElement("Observed performing artist"),
                                    Origin(index + 1, "/name")
                                ),
                            ]
                        ),
                        new(0, 1, 0, 0, true, 0, true),
                        [
                            new(
                                "catalog_recording",
                                Origin(index + 1, ""),
                                null,
                                Guid.NewGuid(),
                                null,
                                null,
                                "recording:" + index,
                                null,
                                null,
                                [song.SongEntityKey],
                                [
                                    new(
                                        "description",
                                        Hash(Encoding.UTF8.GetBytes(Literal)),
                                        Literal,
                                        Origin(index + 1, "/description"),
                                        "html"
                                    ),
                                ],
                                [
                                    new(
                                        "recording_credit",
                                        "performer",
                                        "Observed performing artist",
                                        JsonSerializer.SerializeToElement("performer"),
                                        Origin(index + 1, "/role")
                                    ),
                                ],
                                []
                            ),
                        ]
                    )
            )
            .ToArray();
        var targetNodes = contexts
            .Select(context =>
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(
                    JsonSerializer.Serialize(context, Json)
                )!;
                foreach (var view in node["witnesses"]!.AsArray())
                {
                    foreach (var text in view!["texts"]!.AsArray())
                    {
                        text!.AsObject().Remove("value");
                    }
                }
                return node;
            })
            .ToArray();
        var targets = Path.Combine(root, "targets.jsonl.gz");
        var texts = Path.Combine(root, "texts.jsonl.gz");
        await WriteRows(targets, targetNodes.Select(node => node.ToJsonString(Json)));
        await WriteRows(
            texts,
            [
                JsonSerializer.Serialize(
                    new { sha256 = Hash(Encoding.UTF8.GetBytes(Literal)), value = Literal },
                    Json
                ),
            ]
        );
        var catalogue = Path.Combine(root, "catalogue.json");
        await File.WriteAllBytesAsync(
            catalogue,
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    artists = basePlan
                        .Songs.Select(song => new
                        {
                            uuid = song.ArtistUuid,
                            name = "Observed performing artist",
                        })
                        .ToArray(),
                },
                Json
            )
        );
        var cataloguePin = await Pin(catalogue, 1);
        var descriptors = Path.Combine(root, "descriptors.jsonl");
        await File.WriteAllTextAsync(
            descriptors,
            string.Concat(
                basePlan.Songs.Select(
                    (song, index) =>
                        JsonSerializer.Serialize(
                            new MusicRepertoireDescriptor(
                                song.Scope,
                                song.ArtistUuid,
                                song.SongEntityKey,
                                song.SongEntityKey == "repertoire:missing"
                                    ? "Other title"
                                    : "Retained song",
                                "title_derived",
                                "proposed",
                                null,
                                [],
                                new(
                                    sourceRun,
                                    Path.GetFileName(witness),
                                    index + 1,
                                    index + 1,
                                    "/title",
                                    witnessPin.Sha256
                                )
                            ),
                            Json
                        ) + "\n"
                )
            )
        );
        var names = Path.Combine(root, "dictionary.jsonl");
        await File.WriteAllTextAsync(names, "");
        var musicInputPath = Path.Combine(root, "music-input.json");
        var musicInput = new MusicConsolidationInput(
            "music-consolidation-input-v1",
            "completed",
            new(catalogue, cataloguePin.Bytes, cataloguePin.Sha256, 1),
            await Pin(descriptors, basePlan.Songs.Count),
            await Pin(names, 0),
            [new(1, "catalogue", sourceRun)],
            [new(sourceRun, Path.GetFileName(witness), witnessPin.Bytes, witnessPin.Sha256)]
        );
        await File.WriteAllBytesAsync(
            musicInputPath,
            JsonSerializer.SerializeToUtf8Bytes(musicInput, Json)
        );
        var musicOutputPath = Path.Combine(root, "music-output-generation.json");
        await File.WriteAllTextAsync(
            musicOutputPath,
            "{\"fixture\":\"frozen music generation pin\"}"
        );
        var musicInputPin = await Pin(musicInputPath, 1);
        var musicOutputPin = await Pin(musicOutputPath, 1);
        var hypothesisPath = basePlan.HypothesisManifest.Path;
        var hypothesis = JsonSerializer.Deserialize<CompositionHypothesisOutput>(
            await File.ReadAllBytesAsync(hypothesisPath),
            Json
        )!;
        hypothesis = hypothesis with
        {
            Inputs = new("composition-hypothesis-input-v1", musicInputPin, musicOutputPin, []),
        };
        await File.WriteAllBytesAsync(
            hypothesisPath,
            JsonSerializer.SerializeToUtf8Bytes(hypothesis, Json)
        );
        basePlan = basePlan with { HypothesisManifest = await Pin(hypothesisPath, 1) };
        var basePlanPath = Path.Combine(root, "base-plan.json");
        await File.WriteAllBytesAsync(
            basePlanPath,
            JsonSerializer.SerializeToUtf8Bytes(basePlan, Json)
        );
        var normalization = Path.Combine(root, "normalization.json");
        var sourceManifest = Path.Combine(sourceRun, "manifest.json");
        await File.WriteAllTextAsync(
            normalization,
            "{\"fixture\":\"frozen normalization generation\"}"
        );
        await File.WriteAllTextAsync(sourceManifest, "{\"state\":\"completed\",\"fixture\":true}");
        var normalizationPin = await Pin(normalization, 1);
        var sourcePin = await Pin(sourceManifest, 1);
        var manifest = new CompositionSelectionTargetContextManifest(
            "composition-target-context-v1",
            "completed",
            await Pin(basePlanPath, 1),
            basePlan.HypothesisManifest,
            musicInputPin,
            musicOutputPin,
            cataloguePin,
            normalizationPin,
            (await Pin(targets, contexts.Length)) with
            {
                Path = Path.GetFileName(targets),
            },
            (await Pin(texts, 1)) with
            {
                Path = Path.GetFileName(texts),
            },
            [new(sourceManifest, sourcePin.Bytes, sourcePin.Sha256, 1, sourceRun)],
            [new(witness, witnessPin.Bytes, witnessPin.Sha256, contexts.Length, sourceRun)],
            contexts.Length,
            true
        );
        var manifestPath = Path.Combine(root, "manifest.json");
        await File.WriteAllBytesAsync(
            manifestPath,
            JsonSerializer.SerializeToUtf8Bytes(manifest, Json)
        );
        var plan = basePlan with
        {
            Version = "composition-selection-plan-v2",
            JobUuid = Guid.NewGuid(),
            TargetContextManifest = await Pin(manifestPath, 1),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(plan, Json);
        await File.WriteAllBytesAsync(fixture.Plan, bytes);
        return new(
            fixture.Plan,
            Hash(bytes),
            manifestPath,
            new Dictionary<string, string>
            {
                ["targets"] = targets,
                ["texts"] = texts,
                ["witness"] = witness,
                ["source_manifest"] = sourceManifest,
                ["catalogue"] = catalogue,
                ["normalization"] = normalization,
            }
        );
    }

    internal async Task<SelectionContextFixture> ChangeContext(string mutation)
    {
        var manifest = JsonSerializer.Deserialize<CompositionSelectionTargetContextManifest>(
            await File.ReadAllBytesAsync(Manifest),
            Json
        )!;
        switch (mutation)
        {
            case "source_plan_keys":
                var original = JsonSerializer.Deserialize<CompositionSelectionPlan>(
                    await File.ReadAllBytesAsync(manifest.SelectionPlan.Path),
                    Json
                )!;
                original = original with
                {
                    Songs = original
                        .Songs.Select(
                            (song, index) =>
                                index == 0
                                    ? song with
                                    {
                                        SongEntityKey = song.SongEntityKey + ":other",
                                    }
                                    : song
                        )
                        .ToArray(),
                };
                await File.WriteAllBytesAsync(
                    manifest.SelectionPlan.Path,
                    JsonSerializer.SerializeToUtf8Bytes(original, Json)
                );
                manifest = manifest with
                {
                    SelectionPlan = await Pin(manifest.SelectionPlan.Path, 1),
                };
                break;
            case "hypothesis_generation":
                manifest = manifest with
                {
                    HypothesisManifest = manifest.HypothesisManifest with
                    {
                        Sha256 = new string('0', 64),
                    },
                };
                break;
            case "music_generation":
                manifest = manifest with
                {
                    MusicOutputManifest = manifest.MusicOutputManifest with
                    {
                        Sha256 = new string('0', 64),
                    },
                };
                break;
            case "missing_text":
                await WriteRows(Artifacts["texts"], []);
                manifest = manifest with
                {
                    Texts = (await Pin(Artifacts["texts"], 0)) with { Path = manifest.Texts.Path },
                };
                break;
            default:
                var rows = await ReadRows(Artifacts["targets"]);
                if (mutation == "missing_target")
                {
                    rows.RemoveAt(rows.Count - 1);
                }
                else if (mutation == "wrong_target_key")
                {
                    rows[0]["key"]!["songEntityKey"] = "repertoire:foreign";
                }
                else
                {
                    rows[0]["witnesses"]![0]!["origin"]!["line"] = 999;
                }
                await WriteRows(Artifacts["targets"], rows.Select(row => row.ToJsonString(Json)));
                manifest = manifest with
                {
                    Targets = (await Pin(Artifacts["targets"], rows.Count)) with
                    {
                        Path = manifest.Targets.Path,
                    },
                };
                break;
        }
        await File.WriteAllBytesAsync(
            Manifest,
            JsonSerializer.SerializeToUtf8Bytes(manifest, Json)
        );
        var plan = JsonSerializer.Deserialize<CompositionSelectionPlan>(
            await File.ReadAllBytesAsync(Plan),
            Json
        )!;
        plan = plan with { TargetContextManifest = await Pin(Manifest, 1) };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(plan, Json);
        await File.WriteAllBytesAsync(Plan, bytes);
        return this with { Sha = Hash(bytes) };
    }

    private static async Task WriteRows(string path, IEnumerable<string> rows)
    {
        await using var stream = new GZipStream(File.Create(path), CompressionLevel.SmallestSize);
        await using var writer = new StreamWriter(stream);
        foreach (var row in rows)
        {
            await writer.WriteLineAsync(row);
        }
    }

    private static async Task<List<System.Text.Json.Nodes.JsonNode>> ReadRows(string path)
    {
        await using var stream = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var reader = new StreamReader(stream);
        var rows = new List<System.Text.Json.Nodes.JsonNode>();
        while (await reader.ReadLineAsync() is { } line)
        {
            rows.Add(System.Text.Json.Nodes.JsonNode.Parse(line)!);
        }
        return rows;
    }

    private static async Task<MusicConsolidationArtifact> Pin(string path, long rows) =>
        new(path, new FileInfo(path).Length, Hash(await File.ReadAllBytesAsync(path)), rows);

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
