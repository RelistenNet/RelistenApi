using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Exports complete source-actor membership and direct shared-policy support once
/// per fact bucket. Reads a completed observation run; opens no database, provider
/// or model client. A completed manifest is the publication boundary. Interrupted
/// output remains private and retries require a new directory.
/// </summary>
public sealed class SharedPeopleExport
{
    public const string Version = "shared-people-export-v1";
    private readonly Dictionary<string, Bucket> buckets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> routes = new(StringComparer.Ordinal);
    private readonly long minimumFreeBytes;
    private readonly long maximumOutputBytes;

    public SharedPeopleExport(
        long minimumFreeBytes = 5905580032,
        long maximumOutputBytes = 536870912
    )
    {
        this.minimumFreeBytes = minimumFreeBytes;
        this.maximumOutputBytes = maximumOutputBytes;
    }

    /// <summary>
    /// Consumes original typed source observations and pins every selected chunk.
    /// Existing targets are refused. No output is complete until input hashes are
    /// rechecked and the final manifest is published.
    /// </summary>
    public async Task RunAsync(
        string sourceRun,
        string outputDirectory,
        CancellationToken ct = default
    )
    {
        sourceRun = Path.GetFullPath(sourceRun);
        outputDirectory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
        {
            throw new IOException("Shared people export requires a new output directory.");
        }
        buckets.Clear();
        routes.Clear();
        var manifestPath = Path.Combine(sourceRun, "manifest.json");
        var manifestSha = await HashAsync(manifestPath, ct);
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath, ct));
        RequireSourceRun(manifest.RootElement);
        var method = manifest
            .RootElement.GetProperty("methods")
            .GetProperty("ShadowPeopleAdapter")
            .GetString()!;
        var selection = await IndexSourcesAsync(sourceRun, method, ct);
        VerifyPopulation(manifest.RootElement, selection.Latest);
        Directory.CreateDirectory(outputDirectory);
        CheckResources(outputDirectory);
        await ManifestAsync(
            outputDirectory,
            new
            {
                version = Version,
                state = "building",
                sourceRun,
                manifestSha,
            },
            ct
        );
        try
        {
            await WriteOccurrencesAsync(sourceRun, outputDirectory, selection, ct);
            await WriteBucketsAsync(outputDirectory, ct);
            var comparisonCount = await WriteSupportAsync(outputDirectory, ct);
            CheckResources(outputDirectory);
            if (await HashAsync(manifestPath, ct) != manifestSha)
            {
                throw new InvalidDataException(
                    "Source observation manifest changed during export."
                );
            }
            foreach (var chunk in selection.Chunks)
            {
                if (await HashAsync(Path.Combine(sourceRun, chunk.Chunk), ct) != chunk.Sha256)
                {
                    throw new InvalidDataException(
                        "Source observation chunk changed during export."
                    );
                }
            }
            var outputs = new List<object>();
            foreach (
                var filename in new[]
                {
                    "occurrences.jsonl.gz",
                    "buckets.jsonl.gz",
                    "hypotheses.jsonl.gz",
                }
            )
            {
                var path = Path.Combine(outputDirectory, filename);
                outputs.Add(
                    new
                    {
                        path = filename,
                        bytes = new FileInfo(path).Length,
                        sha256 = await HashAsync(path, ct),
                    }
                );
            }
            await ManifestAsync(
                outputDirectory,
                new
                {
                    version = Version,
                    state = "completed",
                    sourceRun,
                    manifestSha,
                    sourceMethod = method,
                    actorProjection = PeopleActorProjection.Version,
                    policy = "direct_name_or_explicit_alias_and_attributed_account",
                    processingScope = "complete_source_actor_membership_and_direct_bucket_support",
                    candidateScope = "shared_domain_exact_name_or_explicit_alias_and_account_intersection",
                    sourceCount = selection.Latest.Count,
                    occurrences = buckets.Values.Sum(bucket => bucket.Occurrences),
                    bucketCount = buckets.Count,
                    comparisons = comparisonCount,
                    inputs = selection.Chunks,
                    outputs,
                    acceptedIdentity = false,
                    qualifiedForAutomaticAcceptance = false,
                },
                ct
            );
        }
        catch
        {
            await ManifestAsync(
                outputDirectory,
                new
                {
                    version = Version,
                    state = "interrupted",
                    sourceRun,
                    manifestSha,
                },
                CancellationToken.None
            );
            throw;
        }
    }

    private static void RequireSourceRun(JsonElement manifest)
    {
        var methods = manifest.GetProperty("methods");
        // This export consumes source actor facts, not track titles. Pin the
        // producer components that establish those facts; title-only upgrades
        // must not require another source-observation backfill.
        var actorProducerPrefix = string.Join(
            "/",
            "catalog-row-inline-v1",
            RecordingCreditExtractor.Version,
            SourceActorAliasExtractor.Version,
            ""
        );
        var producer = methods.GetProperty("CatalogRowExtraction").GetString()!;
        if (
            manifest.GetProperty("state").GetString() != "completed"
            || manifest.GetProperty("population").GetProperty("processingScope").GetString()
                != "source_actor_observations"
            || !producer.StartsWith(actorProducerPrefix, StringComparison.Ordinal)
            || producer.Length == actorProducerPrefix.Length
            || !methods
                .GetProperty("ShadowPeopleAdapter")
                .GetString()!
                .StartsWith(
                    "shadow-people-source-observations-v1/actor=" + PeopleActorProjection.Version,
                    StringComparison.Ordinal
                )
        )
        {
            throw new InvalidDataException(
                "Shared people require a completed compatible source-observations run."
            );
        }
    }

    private static async Task<Selection> IndexSourcesAsync(
        string run,
        string method,
        CancellationToken ct
    )
    {
        var chunks = new List<ChunkPin>();
        var latest = new Dictionary<Guid, SourcePosition>();
        foreach (
            var path in Directory
                .GetFiles(Path.Combine(run, "people"), "*.jsonl.gz")
                .Order(StringComparer.Ordinal)
        )
        {
            var pin = new ChunkPin(Path.GetRelativePath(run, path), await HashAsync(path, ct));
            chunks.Add(pin);
            long line = 0;
            await foreach (var record in ShadowCatalogInputs.ReadJsonLinesAsync(path, ct))
            {
                line++;
                if (
                    record.GetProperty("inputKind").GetString() != "source_sample"
                    || record.GetProperty("domain").GetString() != "people"
                    || record.GetProperty("method").GetString() != method
                )
                {
                    throw new InvalidDataException(
                        "Shared people input changed domain, scope or method."
                    );
                }
                latest[record.GetProperty("inputUuid").GetGuid()] = new(
                    pin.Chunk,
                    line,
                    record.GetProperty("provider").GetString()!,
                    record.GetProperty("outcome").GetString()!
                );
            }
        }
        if (latest.Count == 0 || latest.Values.Any(position => position.Outcome == "failed"))
        {
            throw new InvalidDataException(
                "Shared people require a nonempty complete source population."
            );
        }
        return new(chunks, latest);
    }

    private static void VerifyPopulation(
        JsonElement manifest,
        Dictionary<Guid, SourcePosition> latest
    )
    {
        var expected = manifest
            .GetProperty("counts")
            .EnumerateArray()
            .Where(count => count.GetProperty("domain").GetString() == "people")
            .ToDictionary(
                count => count.GetProperty("provider").GetString()!,
                count => count.GetProperty("processed").GetInt64()
            );
        var actual = latest
            .Values.GroupBy(position => position.Provider)
            .ToDictionary(group => group.Key, group => group.LongCount());
        if (!expected.OrderBy(pair => pair.Key).SequenceEqual(actual.OrderBy(pair => pair.Key)))
        {
            throw new InvalidDataException(
                "Shared people logical source population differs from the completed manifest."
            );
        }
    }

    private async Task WriteOccurrencesAsync(
        string run,
        string output,
        Selection selection,
        CancellationToken ct
    )
    {
        await using var writer = Output(output, "occurrences");
        foreach (var pin in selection.Chunks)
        {
            CheckResources(output);
            long line = 0;
            await foreach (
                var record in ShadowCatalogInputs.ReadJsonLinesAsync(
                    Path.Combine(run, pin.Chunk),
                    ct
                )
            )
            {
                line++;
                var source = record.GetProperty("inputUuid").GetGuid();
                var selected = selection.Latest[source];
                if (selected.Chunk != pin.Chunk || selected.Line != line)
                {
                    continue;
                }
                if (
                    record.GetProperty("proposal").GetProperty("matchingState").GetString()
                    != "not_run"
                )
                {
                    throw new InvalidDataException(
                        "Original source output must retain its not-run matching contract."
                    );
                }
                foreach (var actor in ShadowPeopleAdapter.ReadSharedSource(record))
                {
                    var signature = JsonSerializer.Serialize(
                        new
                        {
                            facts = actor.Facts with
                            {
                                Uuid = null,
                                ArtistUuids = new HashSet<Guid>(),
                            },
                            actor.Complete,
                            actor.Person.InterpretationBasis,
                        },
                        ShadowCatalogRun.Json
                    );
                    var bucketId = ShadowCatalogIdentity.Key("people_facts", signature);
                    var occurrence = ShadowCatalogIdentity.Key(
                        "people_occurrence",
                        source + ":" + actor.Person.EntityKey
                    );
                    if (!buckets.TryGetValue(bucketId, out var bucket))
                    {
                        bucket = new(bucketId, occurrence, actor);
                        buckets.Add(bucketId, bucket);
                        AddRoutes(bucket);
                    }
                    bucket.Occurrences++;
                    bucket.Sources.Add(source);
                    bucket.Artists.Add(actor.Person.ArtistUuid);
                    await WriteAsync(
                        writer,
                        new SharedPeopleOccurrence(
                            occurrence,
                            bucketId,
                            actor.Person,
                            actor.CreditKeys,
                            new(run, pin.Chunk, line, pin.Sha256)
                        ),
                        ct
                    );
                    if (bucket.Occurrences % 1000 == 0)
                    {
                        CheckResources(output);
                    }
                }
            }
        }
    }

    private void AddRoutes(Bucket bucket)
    {
        var names = bucket
            .Actor.Facts.Names.Where(name => !name.IsHandle)
            .Select(name => IdentityCandidateMatcher.PeopleNameCandidateKey(name.Wording))
            .Where(key => key.Length > 0)
            .Distinct();
        var accounts = ShadowPeopleAdapter.SharedAccountKeys(bucket.Actor.Facts);
        // Both keys are necessary for support. The shared domain's exact name
        // key keeps broad/shared account pools from producing quadratic work.
        foreach (var name in names)
        {
            foreach (var account in accounts)
            {
                var route = JsonSerializer.Serialize(new[] { name, account });
                if (!routes.TryGetValue(route, out var members))
                {
                    members = [];
                    routes.Add(route, members);
                }
                members.Add(bucket.Id);
                bucket.Routes.Add(route);
            }
        }
    }

    private async Task WriteBucketsAsync(string output, CancellationToken ct)
    {
        await using var writer = Output(output, "buckets");
        long written = 0;
        foreach (var bucket in buckets.Values.OrderBy(bucket => bucket.Id, StringComparer.Ordinal))
        {
            await WriteAsync(
                writer,
                new SharedPeopleBucket(
                    bucket.Id,
                    bucket.Representative,
                    bucket.Actor.Facts with
                    {
                        ArtistUuids = new HashSet<Guid>(),
                    },
                    bucket.Actor.Complete,
                    bucket.Actor.Evidence,
                    bucket.Occurrences,
                    bucket.Sources.Count,
                    bucket.Artists.Count
                ),
                ct
            );
            if (++written % 1000 == 0)
            {
                CheckResources(output);
            }
        }
    }

    private async Task<long> WriteSupportAsync(string output, CancellationToken ct)
    {
        await using var writer = Output(output, "hypotheses");
        long comparisons = 0;
        foreach (var anchor in buckets.Values.OrderBy(bucket => bucket.Id, StringComparer.Ordinal))
        {
            CheckResources(output);
            foreach (
                var candidateId in anchor
                    .Routes.SelectMany(route => routes[route])
                    .Distinct()
                    .Order(StringComparer.Ordinal)
            )
            {
                var candidate = buckets[candidateId];
                // A singleton bucket cannot corroborate its own only recording.
                // Equivalent facts across sources retain their full self-membership.
                if (
                    anchor.Sources.Count == 1
                    && candidate.Sources.Count == 1
                    && anchor.Sources.SetEquals(candidate.Sources)
                )
                {
                    continue;
                }
                await WriteAsync(
                    writer,
                    ShadowPeopleAdapter.CompareSharedBuckets(
                        anchor.Id,
                        anchor.Actor,
                        candidateId,
                        candidate.Actor
                    ),
                    ct
                );
                comparisons++;
                if (comparisons % 1000 == 0)
                {
                    CheckResources(output);
                }
            }
        }
        return comparisons;
    }

    private void CheckResources(string output)
    {
        if (
            new DriveInfo(Path.GetPathRoot(output)!).AvailableFreeSpace < minimumFreeBytes
            || Directory.EnumerateFiles(output).Sum(path => new FileInfo(path).Length)
                > maximumOutputBytes
        )
        {
            throw new IOException(
                "Shared people export stopped at its output or host reserve bound."
            );
        }
    }

    private static StreamWriter Output(string directory, string name) =>
        new(
            new GZipStream(
                File.Create(Path.Combine(directory, name + ".jsonl.gz")),
                CompressionLevel.Optimal
            )
        );

    private static Task WriteAsync<T>(StreamWriter writer, T value, CancellationToken ct) =>
        writer.WriteLineAsync(
            JsonSerializer.Serialize(value, ShadowCatalogRun.Json).AsMemory(),
            ct
        );

    private static async Task ManifestAsync(string output, object value, CancellationToken ct)
    {
        var path = Path.Combine(output, "manifest.json");
        await File.WriteAllTextAsync(
            path + ".writing",
            JsonSerializer.Serialize(value, ShadowCatalogRun.Json),
            ct
        );
        File.Move(path + ".writing", path, overwrite: true);
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
    }

    private sealed class Bucket(string id, string representative, SharedPeopleActor actor)
    {
        internal string Id { get; } = id;
        internal string Representative { get; } = representative;
        internal SharedPeopleActor Actor { get; } = actor;
        internal long Occurrences { get; set; }
        internal HashSet<Guid> Sources { get; } = [];
        internal HashSet<Guid> Artists { get; } = [];
        internal HashSet<string> Routes { get; } = [];
    }

    private sealed record ChunkPin(string Chunk, string Sha256);

    private sealed record SourcePosition(string Chunk, long Line, string Provider, string Outcome);

    private sealed record Selection(List<ChunkPin> Chunks, Dictionary<Guid, SourcePosition> Latest);
}
