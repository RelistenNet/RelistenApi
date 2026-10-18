using System.Security.Cryptography;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Runs retained music facts through the actual pure comparators, query builder and optional
/// provider decoder. The complete frozen index and retrieval observations stay separate so later
/// scoring can distinguish missing candidates, new local works and unresolved source evidence.
/// No database, HTTP, model client, acceptance policy or publication service is constructed.
/// </summary>
public sealed class CompositionEvaluationBatch
{
    public const string Version = "composition-evaluation-baseline-v1";
    private const long MaximumOutputBytes = 128L * 1024 * 1024;

    /// <summary>
    /// Authenticate an externally pinned package, then write complete diagnostic outcomes to a
    /// new directory. Failure retains partial files without manifest.json; retry uses a new directory.
    /// Source preparation, independent labels and precision/coverage scoring are separate steps.
    /// </summary>
    public async Task<CompositionEvaluationReport> RunAsync(
        string packageDirectory,
        string newOutputDirectory,
        string expectedManifestSha256,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = Path.GetFullPath(newOutputDirectory);
        if (Directory.Exists(output) || File.Exists(output))
        {
            throw new IOException("Choose a new composition evaluation output directory.");
        }
        var package = await CompositionEvaluationPackage.ReadAsync(
            Path.GetFullPath(packageDirectory),
            expectedManifestSha256,
            cancellationToken
        );
        Directory.CreateDirectory(output);
        await using (var retainedManifest = NewFile(Path.Combine(output, "package-manifest.json")))
        {
            await retainedManifest.WriteAsync(package.ManifestBytes, cancellationToken);
        }

        var decoded = await WriteDecodedInputs(package, output, cancellationToken);
        var comparisons = await WriteComparisons(package, output, cancellationToken);
        await package.RecheckAsync(cancellationToken);

        var outputs = new Dictionary<string, CompositionEvaluationFile>(StringComparer.Ordinal)
        {
            ["outcomes.jsonl"] = await Fingerprint(
                Path.Combine(output, "outcomes.partial.jsonl"),
                package.Tasks.Count,
                cancellationToken
            ),
            ["decoded.jsonl"] = await Fingerprint(
                Path.Combine(output, "decoded.partial.jsonl"),
                decoded,
                cancellationToken
            ),
            ["package-manifest.json"] = await Fingerprint(
                Path.Combine(output, "package-manifest.json"),
                1,
                cancellationToken
            ),
        };
        var report = new CompositionEvaluationReport(
            "completed",
            Version,
            package.ManifestHash,
            package.Manifest.Files,
            outputs,
            package.Tasks.Count,
            comparisons,
            decoded,
            NormalizationProviderDecoder.Version,
            MusicBrainzCandidateQueryBuilder.Version,
            await HashFile(typeof(IdentityCandidateMatcher).Assembly.Location, cancellationToken),
            await HashFile(typeof(CompositionEvaluationBatch).Assembly.Location, cancellationToken),
            false
        );
        await PublishManifest(output, report, cancellationToken);
        return report;
    }

    private static async Task<int> WriteDecodedInputs(
        CompositionEvaluationPackage package,
        string output,
        CancellationToken ct
    )
    {
        var decoder = new NormalizationProviderDecoder();
        var count = 0;
        await using var stream = NewFile(Path.Combine(output, "decoded.partial.jsonl"));
        foreach (var document in package.Documents.Values.Where(document => document.Decode))
        {
            ct.ThrowIfCancellationRequested();
            ProviderDecodeResult result;
            try
            {
                result = decoder.Decode(document.Input, ct);
            }
            catch (NotSupportedException error)
            {
                throw new InvalidDataException(
                    "A retained document requests an unsupported decode profile.",
                    error
                );
            }
            await WriteLine(
                stream,
                new CompositionEvaluationDecodedInput(document.Input.Uuid, document.Origin, result),
                ct
            );
            count++;
        }
        await stream.FlushAsync(ct);
        return count;
    }

    private static async Task<int> WriteComparisons(
        CompositionEvaluationPackage package,
        string output,
        CancellationToken ct
    )
    {
        var matcher = new IdentityCandidateMatcher();
        var comparisons = 0;
        await using var stream = NewFile(Path.Combine(output, "outcomes.partial.jsonl"));
        foreach (var task in package.Tasks)
        {
            ct.ThrowIfCancellationRequested();
            var claim = package.Records[task.ClaimId];
            var universe = package.Universes[task.UniverseId];
            var retrieved = task.Retrieval.CandidateIds.ToHashSet(StringComparer.Ordinal);
            var candidates = new List<CompositionEvaluationCandidateOutcome>();
            foreach (var id in universe.RecordIds)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = package.Records[id];
                var comparison =
                    task.Domain == IdentityMatchingDomain.Compositions
                        ? matcher.CompareCompositions(Composition(claim), Composition(candidate))
                        : matcher.CompareRepertoire(Repertoire(claim), Repertoire(candidate));
                candidates.Add(new(id, retrieved.Contains(id), comparison));
                comparisons++;
            }
            var plan = task.SearchSeed is { } seed
                ? MusicBrainzCandidateQueryBuilder.BuildVariants(seed)
                : null;
            await WriteLine(
                stream,
                new CompositionEvaluationOutcome(
                    task.Id,
                    task.Domain,
                    task.ClaimId,
                    task.UniverseId,
                    universe.Complete,
                    universe.Issues,
                    task.Retrieval,
                    plan,
                    candidates
                ),
                ct
            );
        }
        await stream.FlushAsync(ct);
        return comparisons;
    }

    private static CompositionMatchRecord Composition(CompositionEvaluationRecord record) =>
        new(
            record.CatalogUuid,
            record.Titles.Select(title => new IdentityMatchingName(title.Value)).ToArray(),
            record.ExternalIdentities.Select(identity => identity.Identity).ToArray(),
            record.AuthorUuids.ToHashSet(),
            record.CompleteAuthorship
        );

    private static RepertoireMatchRecord Repertoire(CompositionEvaluationRecord record) =>
        new(
            record.CatalogUuid,
            record.ArtistUuid!.Value,
            record.Titles.Select(title => new IdentityMatchingName(title.Value)).ToArray(),
            record.ExternalIdentities.Select(identity => identity.Identity).ToArray(),
            record.CompositionUuid
        );

    private static async Task WriteLine<T>(FileStream stream, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, CompositionEvaluationPackage.Json);
        CompositionEvaluationPackage.Require(
            bytes.Length <= 32 * 1024 * 1024
                && stream.Position + bytes.Length + 1 <= MaximumOutputBytes,
            "Evaluation output exceeds its 32 MiB row or 128 MiB file limit."
        );
        await stream.WriteAsync(bytes, ct);
        await stream.WriteAsync(new byte[] { (byte)'\n' }, ct);
    }

    private static async Task PublishManifest(
        string output,
        CompositionEvaluationReport report,
        CancellationToken ct
    )
    {
        await using (var stream = NewFile(Path.Combine(output, "manifest.partial.json")))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                report,
                CompositionEvaluationPackage.Json,
                ct
            );
            await stream.FlushAsync(ct);
        }
        ct.ThrowIfCancellationRequested();
        foreach (var name in new[] { "decoded", "outcomes" })
        {
            File.Move(
                Path.Combine(output, name + ".partial.jsonl"),
                Path.Combine(output, name + ".jsonl")
            );
        }
        File.Move(
            Path.Combine(output, "manifest.partial.json"),
            Path.Combine(output, "manifest.json")
        );
    }

    private static FileStream NewFile(string path) =>
        new(path, FileMode.CreateNew, FileAccess.Write);

    private static async Task<string> HashFile(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    private static async Task<CompositionEvaluationFile> Fingerprint(
        string path,
        int rows,
        CancellationToken ct
    ) => new(new FileInfo(path).Length, await HashFile(path, ct), rows);
}
