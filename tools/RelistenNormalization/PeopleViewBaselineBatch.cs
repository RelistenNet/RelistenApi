using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>Exports every independently attributed source view without choosing or merging actor identities.</summary>
public sealed class PeopleViewBaselineBatch
{
    public const string Version = "people-view-baseline-v1";
    private const int MaximumViews = 100_000;
    private const long MaximumLeafPairs = 1_000_000;
    private const long MaximumOutputBytes = 128L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public async Task<PeopleViewBaselineReport> RunAsync(
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
            "Choose a new people view baseline output directory."
        );
        var package = await PeopleAdjudicatedSourcePackage.ReadAsync(
            Path.GetFullPath(packageDirectory),
            expectedManifestSha256,
            cancellationToken
        );
        var endpoints = ProjectViews(package, cancellationToken);
        await package.RecheckAsync(cancellationToken);

        Directory.CreateDirectory(output);
        // Keep the established comparator byte contract and old mode intact. This bounded
        // second read authenticates the same pinned sources; neither phase opens targets.
        var comparator = await new PeopleAdjudicatedBaselineBatch().RunAsync(
            packageDirectory,
            Path.Combine(output, "comparator"),
            expectedManifestSha256,
            cancellationToken
        );
        await package.RecheckAsync(cancellationToken);
        var bagCounts = PreflightBags(package, endpoints, cancellationToken);
        await WriteEndpointViews(output, endpoints.Values, cancellationToken);
        await WriteViewPairs(output, package, endpoints, cancellationToken);
        await package.RecheckAsync(cancellationToken);

        var report = await ReadReport(
            output,
            package,
            endpoints,
            comparator,
            bagCounts,
            cancellationToken
        );
        await PublishManifest(output, report, cancellationToken);
        return report;
    }

    private static IReadOnlyDictionary<string, PeopleEndpointViewDiagnostic> ProjectViews(
        PeopleAdjudicatedSourcePackage package,
        CancellationToken ct
    )
    {
        var projector = new PeopleIndependentSourceViewProjection(package, ct);
        var endpoints = new Dictionary<string, PeopleEndpointViewDiagnostic>(
            StringComparer.Ordinal
        );
        var views = 0;
        var variants = 0;
        foreach (var endpoint in package.Endpoints.Values)
        {
            ct.ThrowIfCancellationRequested();
            var projected = projector.Project(endpoint);
            views += projected.Views.Count;
            variants += projected.Variants.Count;
            PeoplePairBaselineBundle.Require(
                views <= MaximumViews && variants <= MaximumViews,
                "The source package exceeds 100,000 views or actor variants."
            );
            endpoints.Add(endpoint.Id, projected);
        }
        return endpoints;
    }

    private static IEnumerable<PeopleViewBag> ReadBags(
        PeopleAdjudicatedSourcePackage package,
        IReadOnlyDictionary<string, PeopleEndpointViewDiagnostic> endpoints,
        CancellationToken ct
    )
    {
        foreach (var question in package.Questions.Where(question => question.Kind != "actor_kind"))
        {
            var left = endpoints[question.Focus];
            foreach (var candidateId in question.Candidates)
            {
                ct.ThrowIfCancellationRequested();
                var right = endpoints[candidateId];
                var leaves = (long)left.Views.Count * right.Views.Count;
                yield return new(
                    JsonSerializer.Serialize(new[] { question.Id, candidateId }),
                    question.Id,
                    question.Kind,
                    question.Focus,
                    candidateId,
                    left.Coverage,
                    right.Coverage,
                    left.Views.Count,
                    right.Views.Count,
                    leaves,
                    left.Coverage == "complete" && right.Coverage == "complete" && leaves > 0
                );
            }
        }
    }

    private static BagCounts PreflightBags(
        PeopleAdjudicatedSourcePackage package,
        IReadOnlyDictionary<string, PeopleEndpointViewDiagnostic> endpoints,
        CancellationToken ct
    )
    {
        var edges = 0;
        long leaves = 0;
        var complete = 0;
        var partial = 0;
        var unsupported = 0;
        foreach (var bag in ReadBags(package, endpoints, ct))
        {
            leaves += bag.ExpectedLeafPairs;
            PeoplePairBaselineBundle.Require(
                leaves <= MaximumLeafPairs,
                "The view export exceeds 1,000,000 leaf pairs; no top completion manifest was written."
            );
            edges++;
            if (bag.Complete)
            {
                complete++;
            }
            else if (bag.ExpectedLeafPairs > 0)
            {
                partial++;
            }
            else
            {
                unsupported++;
            }
        }
        return new(edges, leaves, complete, partial, unsupported);
    }

    private static async Task WriteEndpointViews(
        string output,
        IEnumerable<PeopleEndpointViewDiagnostic> endpoints,
        CancellationToken ct
    )
    {
        await using var stream = NewFile(Path.Combine(output, "endpoint-views.partial.jsonl"));
        foreach (var endpoint in endpoints)
        {
            ct.ThrowIfCancellationRequested();
            await WriteLine(stream, endpoint, ct);
        }
        await stream.FlushAsync(ct);
    }

    private static async Task WriteViewPairs(
        string output,
        PeopleAdjudicatedSourcePackage package,
        IReadOnlyDictionary<string, PeopleEndpointViewDiagnostic> endpoints,
        CancellationToken ct
    )
    {
        await using var bags = NewFile(Path.Combine(output, "view-bags.partial.jsonl"));
        await using var pairs = NewFile(Path.Combine(output, "view-pairs.partial.jsonl"));
        var matcher = new IdentityCandidateMatcher();
        // Retain zero-view bags and partial bags. A leaf compares only its own two original
        // records; exporting it supplies no label, co-reference, or aggregate match authority.
        foreach (var bag in ReadBags(package, endpoints, ct))
        {
            await WriteLine(bags, bag, ct);
            foreach (var left in endpoints[bag.FocusEndpointId].Views)
            {
                foreach (var right in endpoints[bag.CandidateEndpointId].Views)
                {
                    ct.ThrowIfCancellationRequested();
                    var comparison = matcher.ComparePeople(
                        left.Projection.Record,
                        right.Projection.Record
                    );
                    await WriteLine(
                        pairs,
                        new PeopleViewPair(
                            bag.EdgeId,
                            bag.TaskId,
                            bag.FocusEndpointId,
                            bag.CandidateEndpointId,
                            left.Key,
                            right.Key,
                            comparison,
                            PeoplePairBaselineBatch.Features(comparison)
                        ),
                        ct
                    );
                }
            }
        }
        await bags.FlushAsync(ct);
        await pairs.FlushAsync(ct);
    }

    private static async Task<PeopleViewBaselineReport> ReadReport(
        string output,
        PeopleAdjudicatedSourcePackage package,
        IReadOnlyDictionary<string, PeopleEndpointViewDiagnostic> endpoints,
        PeopleAdjudicatedBaselineReport comparator,
        BagCounts counts,
        CancellationToken ct
    ) =>
        new(
            "completed",
            Version,
            PeopleIndependentSourceViewProjection.Version,
            PeoplePairBaselineBatch.FeatureOrder,
            package.ManifestHash,
            package.Files,
            await ReadArtifact(output, "comparator/manifest.json", null, ct),
            await ReadArtifact(
                output,
                "comparator/" + comparator.BaselineFile,
                comparator.Pairs,
                ct
            ),
            await ReadArtifact(output, "endpoint-views.jsonl", endpoints.Count, ct, partial: true),
            await ReadArtifact(output, "view-bags.jsonl", counts.Edges, ct, partial: true),
            await ReadArtifact(output, "view-pairs.jsonl", counts.Leaves, ct, partial: true),
            await ReadArtifact(
                output,
                "comparator/" + comparator.GroupingFile,
                comparator.Groups,
                ct
            ),
            await ReadArtifact(
                output,
                "comparator/" + comparator.BridgeFile,
                comparator.Bridges,
                ct
            ),
            new(
                package.Questions.Count,
                endpoints.Count,
                counts.Edges,
                endpoints.Values.Sum(endpoint => endpoint.Views.Count),
                counts.Leaves,
                counts.Complete,
                counts.Partial,
                counts.Unsupported
            ),
            comparator.Artifacts,
            new(
                comparator.Version,
                comparator.ProjectionVersion,
                comparator.ActorFeaturesVersion,
                comparator.CreditExtractorVersion,
                comparator.ActorEvidenceVersion,
                comparator.ActorProjectionVersion,
                comparator.SourceContextVersion,
                comparator.SourceReferenceVersion
            ),
            new(MaximumViews, MaximumLeafPairs, MaximumOutputBytes),
            false
        );

    private static async Task<PeopleViewOutputArtifact> ReadArtifact(
        string output,
        string relativePath,
        long? rows,
        CancellationToken ct,
        bool partial = false
    )
    {
        var physicalPath = Path.Combine(
            output,
            partial
                ? relativePath.Replace(".jsonl", ".partial.jsonl", StringComparison.Ordinal)
                : relativePath
        );
        await using var stream = File.OpenRead(physicalPath);
        return new(
            relativePath,
            stream.Length,
            Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)),
            rows
        );
    }

    private static async Task PublishManifest(
        string output,
        PeopleViewBaselineReport report,
        CancellationToken ct
    )
    {
        await using (var stream = NewFile(Path.Combine(output, "manifest.partial.json")))
        {
            await JsonSerializer.SerializeAsync(stream, report, Json, ct);
            await stream.FlushAsync(ct);
        }
        ct.ThrowIfCancellationRequested();
        foreach (var name in new[] { "endpoint-views", "view-bags", "view-pairs" })
        {
            File.Move(
                Path.Combine(output, name + ".partial.jsonl"),
                Path.Combine(output, name + ".jsonl")
            );
        }
        // A nested comparator manifest may survive cancellation/overflow. Only this final
        // top marker authenticates a complete view export; retry always chooses a new directory.
        File.Move(
            Path.Combine(output, "manifest.partial.json"),
            Path.Combine(output, "manifest.json")
        );
    }

    private static async Task WriteLine<T>(FileStream stream, T row, CancellationToken ct)
    {
        await JsonSerializer.SerializeAsync(stream, row, Json, ct);
        await stream.WriteAsync(new byte[] { (byte)'\n' }, ct);
        PeoplePairBaselineBundle.Require(
            stream.Position <= MaximumOutputBytes,
            "A view output exceeds 128 MiB; partial output is retained without a top completion manifest."
        );
    }

    private static FileStream NewFile(string path) =>
        new(path, FileMode.CreateNew, FileAccess.Write);

    private sealed record BagCounts(
        int Edges,
        long Leaves,
        int Complete,
        int Partial,
        int Unsupported
    );
}

public sealed record PeopleViewBag(
    string EdgeId,
    string TaskId,
    string TaskKind,
    string FocusEndpointId,
    string CandidateEndpointId,
    string LeftCoverage,
    string RightCoverage,
    int LeftViewCount,
    int RightViewCount,
    long ExpectedLeafPairs,
    bool Complete
);

public sealed record PeopleViewPair(
    string EdgeId,
    string TaskId,
    string FocusEndpointId,
    string CandidateEndpointId,
    PeopleSourceViewKey LeftViewKey,
    PeopleSourceViewKey RightViewKey,
    IdentityCandidateComparison Comparison,
    IReadOnlyList<double> Features
);

public sealed record PeopleViewOutputArtifact(string Path, long Bytes, string Sha256, long? Rows);

public sealed record PeopleViewBaselineCounts(
    int Tasks,
    int Endpoints,
    int Edges,
    int Views,
    long LeafPairs,
    int CompleteBags,
    int PartialBags,
    int UnsupportedBags
);

public sealed record PeopleViewBaselineVersions(
    string ComparatorVersion,
    string ComparatorProjectionVersion,
    string ActorFeaturesVersion,
    string CreditExtractorVersion,
    string ActorEvidenceVersion,
    string ActorProjectionVersion,
    string SourceContextVersion,
    string SourceReferenceVersion
);

public sealed record PeopleViewBaselineLimits(
    int MaximumViews,
    long MaximumLeafPairs,
    long MaximumOutputBytesPerArtifact
);

public sealed record PeopleViewBaselineReport(
    string State,
    string Version,
    string ProjectionVersion,
    IReadOnlyList<string> FeatureOrder,
    string PackageManifestSha256,
    IReadOnlyDictionary<string, PeoplePairBaselineFile> InputFiles,
    PeopleViewOutputArtifact ComparatorManifest,
    PeopleViewOutputArtifact Baseline,
    PeopleViewOutputArtifact EndpointViews,
    PeopleViewOutputArtifact Bags,
    PeopleViewOutputArtifact LeafPairs,
    PeopleViewOutputArtifact Grouping,
    PeopleViewOutputArtifact Bridges,
    PeopleViewBaselineCounts Counts,
    IReadOnlyList<PeoplePairBaselineArtifact> RuntimeArtifacts,
    PeopleViewBaselineVersions Versions,
    PeopleViewBaselineLimits Limits,
    bool PolicyActivationPermitted
);
