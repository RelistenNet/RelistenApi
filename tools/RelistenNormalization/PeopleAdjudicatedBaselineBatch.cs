using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>Exports actual source-only comparisons without opening the package's separate targets.</summary>
public sealed class PeopleAdjudicatedBaselineBatch
{
    public const string Version = "people-adjudicated-baseline-v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public async Task<PeopleAdjudicatedBaselineReport> RunAsync(
        string packageDirectory,
        string newOutputDirectory,
        string expectedManifestSha256,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = Path.GetFullPath(newOutputDirectory);
        PeoplePairBaselineBundle.Require(
            !Directory.Exists(output) && !File.Exists(output),
            "Choose a new adjudicated people baseline output directory."
        );
        var package = await PeopleAdjudicatedSourcePackage.ReadAsync(
            Path.GetFullPath(packageDirectory),
            expectedManifestSha256,
            cancellationToken
        );
        var endpoints = ProjectEndpoints(package, cancellationToken);
        var bridges = ReadGroupingBridges(package, endpoints, cancellationToken);
        await package.RecheckAsync(cancellationToken);

        Directory.CreateDirectory(output);
        var comparisons = await WriteComparisons(
            Path.Combine(output, "baseline.partial.jsonl"),
            package,
            endpoints,
            cancellationToken
        );
        await WriteLines(
            Path.Combine(output, "endpoints.partial.jsonl"),
            endpoints.Values.Select(endpoint => endpoint.Diagnostic),
            cancellationToken
        );
        await WriteLines(
            Path.Combine(output, "groups.partial.jsonl"),
            package.Groups,
            cancellationToken
        );
        var bridgeCount = await WriteLines(
            Path.Combine(output, "source-bridges.partial.jsonl"),
            bridges,
            cancellationToken
        );
        await package.RecheckAsync(cancellationToken);
        var artifacts = new[]
        {
            await AssemblyArtifact(
                typeof(IdentityCandidateMatcher).Assembly.Location,
                cancellationToken
            ),
            await AssemblyArtifact(
                typeof(PeopleAdjudicatedBaselineBatch).Assembly.Location,
                cancellationToken
            ),
        };
        var report = new PeopleAdjudicatedBaselineReport(
            "completed",
            Version,
            PeopleAdjudicatedEndpointProjection.Version,
            IdentityCandidateMatcher.PeopleActorFeaturesVersion,
            PeoplePairBaselineBatch.FeatureOrder,
            package.ManifestHash,
            package.Files,
            "baseline.jsonl",
            await OutputHash(output, "baseline", cancellationToken),
            "endpoints.jsonl",
            await OutputHash(output, "endpoints", cancellationToken),
            "groups.jsonl",
            await OutputHash(output, "groups", cancellationToken),
            "source-bridges.jsonl",
            await OutputHash(output, "source-bridges", cancellationToken),
            package.Questions.Count(question => question.Kind != "actor_kind"),
            comparisons.Pairs,
            package.Questions.Count,
            package.Endpoints.Count,
            package.Sources.Count,
            package.Groups.Count,
            comparisons.SupportedPairs,
            bridgeCount,
            false,
            artifacts,
            RecordingCreditExtractor.Version,
            RecordingActorEvidenceExtractor.Version,
            PeopleActorProjection.Version,
            PeopleSourceContextProjection.Version,
            PeopleSourceContextProjection.ReferenceVersion
        );
        await using (var stream = NewFile(Path.Combine(output, "manifest.partial.json")))
        {
            await JsonSerializer.SerializeAsync(stream, report, Json, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var name in new[] { "baseline", "endpoints", "groups", "source-bridges" })
        {
            File.Move(
                Path.Combine(output, name + ".partial.jsonl"),
                Path.Combine(output, name + ".jsonl")
            );
        }
        // Only this last marker acknowledges the complete source snapshot. Targets are not
        // consulted or rechecked; changing/missing target bytes cannot affect these features.
        File.Move(
            Path.Combine(output, "manifest.partial.json"),
            Path.Combine(output, "manifest.json")
        );
        return report;
    }

    private static IReadOnlyDictionary<
        string,
        PeopleAdjudicatedEndpointProjection.Result
    > ProjectEndpoints(PeopleAdjudicatedSourcePackage package, CancellationToken ct)
    {
        var projector = new PeopleAdjudicatedEndpointProjection(package, ct);
        var result = new Dictionary<string, PeopleAdjudicatedEndpointProjection.Result>(
            StringComparer.Ordinal
        );
        var variants = 0;
        foreach (var endpoint in package.Endpoints.Values)
        {
            ct.ThrowIfCancellationRequested();
            var projected = projector.Project(endpoint);
            variants += projected.Diagnostic.Variants.Count;
            PeoplePairBaselineBundle.Require(
                variants <= 100_000,
                "The source package exceeds 100,000 actor variants."
            );
            result.Add(endpoint.Id, projected);
        }
        return result;
    }

    private static IEnumerable<PeopleAdjudicatedBaselineRow> ComparePairs(
        PeopleAdjudicatedSourcePackage package,
        IReadOnlyDictionary<string, PeopleAdjudicatedEndpointProjection.Result> endpoints,
        CancellationToken ct
    )
    {
        var matcher = new IdentityCandidateMatcher();
        foreach (var question in package.Questions.Where(question => question.Kind != "actor_kind"))
        {
            var focus = endpoints[question.Focus];
            foreach (var candidateId in question.Candidates)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = endpoints[candidateId];
                var comparison =
                    focus.Projection != null && candidate.Projection != null
                        ? matcher.ComparePeople(
                            focus.Projection.Record,
                            candidate.Projection.Record
                        )
                        : null;
                var issue =
                    focus.Projection == null ? "focus_" + focus.Diagnostic.ProjectionIssue
                    : candidate.Projection == null
                        ? "candidate_" + candidate.Diagnostic.ProjectionIssue
                    : null;
                yield return new(
                    question.Id,
                    question.Kind,
                    question.Focus,
                    candidateId,
                    focus.Diagnostic.EvidenceId,
                    candidate.Diagnostic.EvidenceId,
                    focus.Diagnostic.EvidenceId == candidate.Diagnostic.EvidenceId,
                    question.Candidates.Count,
                    comparison == null ? null : PeoplePairBaselineBatch.Features(comparison),
                    comparison,
                    comparison != null,
                    issue
                );
            }
        }
    }

    private static IEnumerable<PeopleAdjudicatedGroupingBridge> ReadGroupingBridges(
        PeopleAdjudicatedSourcePackage package,
        IReadOnlyDictionary<string, PeopleAdjudicatedEndpointProjection.Result> endpoints,
        CancellationToken ct
    )
    {
        var tasks = package.Endpoints.Keys.ToDictionary(
            id => id,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal
        );
        foreach (var question in package.Questions)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var endpointId in question.Candidates.Prepend(question.Focus))
            {
                tasks[endpointId].Add(question.Id);
            }
        }
        // Grouping is conservative leakage control, not an identity assertion. Even unowned
        // source hypotheses bridge their original tasks; naked UUID/name equality never does.
        var references = endpoints
            .SelectMany(endpoint =>
                endpoint
                    .Value.Diagnostic.Variants.Where(variant => variant.ActorReference != null)
                    .Select(variant => (Reference: variant.ActorReference!, Endpoint: endpoint.Key))
            )
            .GroupBy(value => value.Reference);
        foreach (var group in references)
        {
            ct.ThrowIfCancellationRequested();
            var ids = group
                .Select(value => value.Endpoint)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var taskIds = ids.SelectMany(id => tasks[id])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (ids.Length > 1 || taskIds.Length > 1)
            {
                // Emit each membership expansion directly into the bounded writer. Retaining
                // every bridge could multiply task arrays for one multi-hypothesis endpoint.
                yield return new(group.Key, ids, taskIds);
            }
        }
    }

    private sealed record ComparisonCounts(int Pairs, int SupportedPairs);

    private static async Task<ComparisonCounts> WriteComparisons(
        string path,
        PeopleAdjudicatedSourcePackage package,
        IReadOnlyDictionary<string, PeopleAdjudicatedEndpointProjection.Result> endpoints,
        CancellationToken ct
    )
    {
        var pairs = 0;
        var supported = 0;
        await using var output = NewFile(path);
        // A bounded questions file can still declare many edges. Generate and discard each
        // comparison inside the bounded writer rather than materializing the expanded pool.
        foreach (var row in ComparePairs(package, endpoints, ct))
        {
            await WriteLine(output, row, ct);
            pairs++;
            if (row.ProjectionSupported)
            {
                supported++;
            }
        }
        await output.FlushAsync(ct);
        return new(pairs, supported);
    }

    private static async Task<int> WriteLines<T>(
        string path,
        IEnumerable<T> rows,
        CancellationToken ct
    )
    {
        var count = 0;
        await using var output = NewFile(path);
        foreach (var row in rows)
        {
            await WriteLine(output, row, ct);
            count++;
        }
        await output.FlushAsync(ct);
        return count;
    }

    private static async Task WriteLine<T>(FileStream output, T row, CancellationToken ct)
    {
        await JsonSerializer.SerializeAsync(output, row, Json, ct);
        await output.WriteAsync(new byte[] { (byte)'\n' }, ct);
        PeoplePairBaselineBundle.Require(
            output.Position <= 128L * 1024 * 1024,
            "A baseline output exceeds 128 MiB; partial output is retained."
        );
    }

    private static FileStream NewFile(string path) =>
        new(path, FileMode.CreateNew, FileAccess.Write);

    private static Task<string> OutputHash(string output, string name, CancellationToken ct) =>
        FileHash(Path.Combine(output, name + ".partial.jsonl"), ct);

    private static async Task<PeoplePairBaselineArtifact> AssemblyArtifact(
        string path,
        CancellationToken ct
    ) => new(path, new FileInfo(path).Length, await FileHash(path, ct));

    private static async Task<string> FileHash(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
}

public sealed record PeopleAdjudicatedBaselineRow(
    string TaskId,
    string TaskKind,
    string FocusEndpointId,
    string CandidateEndpointId,
    string FocusEvidenceId,
    string CandidateEvidenceId,
    bool SameRecording,
    int DeclaredCandidateCount,
    IReadOnlyList<double>? Features,
    IdentityCandidateComparison? Comparison,
    bool ProjectionSupported,
    string? ProjectionIssue
);

public sealed record PeopleAdjudicatedBaselineReport(
    string State,
    string Version,
    string ProjectionVersion,
    string ActorFeaturesVersion,
    IReadOnlyList<string> FeatureOrder,
    string PackageManifestSha256,
    IReadOnlyDictionary<string, PeoplePairBaselineFile> InputFiles,
    string BaselineFile,
    string BaselineSha256,
    string EndpointFile,
    string EndpointSha256,
    string GroupingFile,
    string GroupingSha256,
    string BridgeFile,
    string BridgeSha256,
    int Questions,
    int Pairs,
    int AllTasks,
    int Endpoints,
    int Sources,
    int Groups,
    int SupportedPairs,
    int Bridges,
    bool PolicyActivationPermitted,
    IReadOnlyList<PeoplePairBaselineArtifact> Artifacts,
    string CreditExtractorVersion,
    string ActorEvidenceVersion,
    string ActorProjectionVersion,
    string SourceContextVersion,
    string SourceReferenceVersion
);
