using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Relisten;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
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
        var fixture = await SelectionFixture.Create(directory);
        var output = Path.Combine(directory, "execution");
        var selector = new Selecting { FailOnCall = 2 };
        var batch = new CompositionSelectionBatch(cache, selector);
        var interrupted = () => batch.RunAsync(fixture.Plan, output, fixture.Sha);
        await interrupted.Should().ThrowAsync<IOException>();
        selector.Calls.Should().Be(2);
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeFalse();

        selector.FailOnCall = null;
        var completion = await batch.RunAsync(fixture.Plan, output, fixture.Sha, resume: true);
        selector.Calls.Should().Be(3);
        completion.ProcessedInputs.Should().Be(2);
        completion.Outcomes["selected"].Should().Be(2);
        var again = await batch.RunAsync(fixture.Plan, output, fixture.Sha, resume: true);
        selector.Calls.Should().Be(3);
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
        selector.Calls.Should().Be(3);
    }

    private sealed class Selecting : ICompositionHypothesisSelector
    {
        public int Calls { get; private set; }
        public int? FailOnCall { get; set; }

        public Task<CompositionHypothesisSelection> SelectAsync(
            CompositionHypothesisSong song,
            IReadOnlyList<RetainedCompositionWork> suppliedCandidates,
            CancellationToken ct = default
        )
        {
            Calls++;
            if (Calls == FailOnCall)
            {
                throw new IOException("Synthetic interruption before a provider result.");
            }
            return Task.FromResult(
                new CompositionHypothesisSelection(
                    Guid.Parse(suppliedCandidates.Single().Identity.Identity.ProviderKey),
                    [],
                    "selected",
                    "synthetic-selector",
                    "Supplied retained candidate"
                )
            );
        }
    }
}

internal sealed record SelectionFixture(string Plan, string Sha, string Chunk)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    internal static async Task<SelectionFixture> Create(
        string directory,
        bool includeMissing = false,
        long maximumOutputBytes = 64 * 1024 * 1024,
        int selectedCount = 2
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
            keys.Add(missing);
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
            0
        );
        var bytes = JsonSerializer.SerializeToUtf8Bytes(plan, Json);
        var planPath = Path.Combine(directory, "plan.json");
        await File.WriteAllBytesAsync(planPath, bytes);
        return new(planPath, Hash(bytes), chunk);
    }

    private static async Task<MusicConsolidationArtifact> Pin(string path, long rows) =>
        new(path, new FileInfo(path).Length, Hash(await File.ReadAllBytesAsync(path)), rows);

    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
}
