using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>Policy-free pair comparisons with source-attached actor context and complete declared pools.</summary>
public sealed class PeopleSourceContextBaselineBatch
{
    public const string Version = "people-pair-context-baseline-v1";
    public const string ProjectionVersion = "people-mentions-source-context-five-v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public async Task<PeopleSourceContextBaselineReport> RunAsync(
        string repositoryRoot,
        string bundleDirectory,
        string newOutputDirectory,
        string frozenManifestSha256,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = Path.GetFullPath(newOutputDirectory);
        PeoplePairBaselineBundle.Require(
            !Directory.Exists(output) && !File.Exists(output),
            "Choose a new people source-context output directory."
        );
        var inputs = await PeoplePairBaselineBundle.ReadAsync(
            Path.GetFullPath(repositoryRoot),
            Path.GetFullPath(bundleDirectory),
            frozenManifestSha256,
            cancellationToken
        );
        var contexts = ProjectMentions(inputs, cancellationToken);

        Directory.CreateDirectory(output);
        var contextPath = Path.Combine(output, "context-diagnostics.partial.jsonl");
        await WriteContexts(inputs, contexts, contextPath, cancellationToken);
        var baselinePath = Path.Combine(output, "baseline.partial.jsonl");
        var pairs = await WriteComparisons(inputs, contexts, baselinePath, cancellationToken);
        await inputs.RecheckAsync(cancellationToken);
        var artifacts = new[]
        {
            await AssemblyArtifact(
                typeof(IdentityCandidateMatcher).Assembly.Location,
                cancellationToken
            ),
            await AssemblyArtifact(
                typeof(PeopleSourceContextBaselineBatch).Assembly.Location,
                cancellationToken
            ),
        };
        var report = new PeopleSourceContextBaselineReport(
            "completed",
            Version,
            ProjectionVersion,
            IdentityCandidateMatcher.PeopleActorFeaturesVersion,
            PeoplePairBaselineBatch.FeatureOrder,
            inputs.ManifestHash,
            inputs.Files,
            "baseline.jsonl",
            await FileHash(baselinePath, cancellationToken),
            inputs.Questions.Count(question => question.Kind != "actor_kind"),
            pairs,
            inputs.Questions.Count,
            artifacts[0].Sha256,
            artifacts[1].Sha256,
            artifacts,
            ["canonical_identity", "unsupported_actor_context", "query_or_attachment_uncertainty"],
            false,
            "context-diagnostics.jsonl",
            await FileHash(contextPath, cancellationToken),
            contexts.Count,
            PeopleSourceContextProjection.Version,
            RecordingCreditExtractor.Version,
            RecordingActorEvidenceExtractor.Version,
            PeopleActorProjection.Version
        );
        await using (var stream = NewFile(Path.Combine(output, "manifest.partial.json")))
        {
            await JsonSerializer.SerializeAsync(stream, report, Json, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Only manifest.json marks completion. A failed or interrupted export leaves its
        // partial artifacts intact; callers choose a new directory rather than overwrite them.
        File.Move(contextPath, Path.Combine(output, "context-diagnostics.jsonl"));
        File.Move(baselinePath, Path.Combine(output, "baseline.jsonl"));
        File.Move(
            Path.Combine(output, "manifest.partial.json"),
            Path.Combine(output, "manifest.json")
        );
        return report;
    }

    private static IReadOnlyDictionary<string, PeopleSourceContextResult> ProjectMentions(
        PeoplePairBaselineBundle inputs,
        CancellationToken ct
    )
    {
        var projector = new PeopleSourceContextProjection(inputs.SourceFields, ct);
        var result = new Dictionary<string, PeopleSourceContextResult>(StringComparer.Ordinal);
        foreach (var mention in inputs.Mentions.Values)
        {
            ct.ThrowIfCancellationRequested();
            result.Add(
                mention.Id,
                projector.Project(
                    mention.EvidenceId,
                    mention.Name,
                    mention.Kind,
                    mention.ArtistUuid,
                    mention.Anchor
                )
            );
        }
        return result;
    }

    private static async Task WriteContexts(
        PeoplePairBaselineBundle inputs,
        IReadOnlyDictionary<string, PeopleSourceContextResult> contexts,
        string path,
        CancellationToken ct
    )
    {
        await using var output = NewFile(path);
        foreach (var mention in inputs.Mentions.Values)
        {
            var context = contexts[mention.Id];
            var raw = inputs.SourceFields[mention.EvidenceId][mention.Anchor.Field]!;
            var diagnostic = new PeopleSourceContextDiagnostic(
                mention.Id,
                mention.EvidenceId,
                "/raw_record/row" + mention.Anchor.JsonPointer,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw))),
                mention.Anchor,
                context.Status,
                context.MatchedVia,
                context.ReferenceUuid,
                context.Projection.Complete,
                context.Issues,
                context.Facts,
                context.SourceAssertion
            );
            await JsonSerializer.SerializeAsync(output, diagnostic, Json, ct);
            await output.WriteAsync(new byte[] { (byte)'\n' }, ct);
        }
        await output.FlushAsync(ct);
    }

    private static async Task<int> WriteComparisons(
        PeoplePairBaselineBundle inputs,
        IReadOnlyDictionary<string, PeopleSourceContextResult> contexts,
        string path,
        CancellationToken ct
    )
    {
        var matcher = new IdentityCandidateMatcher();
        var pairs = 0;
        await using var output = NewFile(path);
        foreach (var question in inputs.Questions.Where(question => question.Kind != "actor_kind"))
        {
            var focus = inputs.Mentions[question.Focus];
            foreach (var candidateId in question.Candidates)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = inputs.Mentions[candidateId];
                var comparison = matcher.ComparePeople(
                    contexts[focus.Id].Projection.Record,
                    contexts[candidate.Id].Projection.Record
                );
                var supported = focus.Kind == "name" && candidate.Kind == "name";
                var row = new PeoplePairBaselineRow(
                    question.Id,
                    question.Kind,
                    focus.Id,
                    candidate.Id,
                    focus.EvidenceId,
                    candidate.EvidenceId,
                    focus.EvidenceId == candidate.EvidenceId,
                    question.Candidates.Count,
                    PeoplePairBaselineBatch.Features(comparison),
                    comparison,
                    supported,
                    supported ? null : "unsupported_mention_expression"
                );
                await JsonSerializer.SerializeAsync(output, row, Json, ct);
                await output.WriteAsync(new byte[] { (byte)'\n' }, ct);
                pairs++;
            }
        }
        await output.FlushAsync(ct);
        return pairs;
    }

    private static FileStream NewFile(string path) =>
        new(path, FileMode.CreateNew, FileAccess.Write);

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

public sealed record PeopleSourceContextDiagnostic(
    string MentionId,
    string EvidenceId,
    string SourceJsonPointer,
    string SourceFieldSha256,
    NormalizationTextEvidence Anchor,
    string Status,
    string? MatchedVia,
    Guid? ReferenceUuid,
    bool QueryComplete,
    IReadOnlyList<string> Issues,
    CreditedActorFacts<NormalizationTextEvidence>? Facts,
    SourceActorAliasAssertion<NormalizationTextEvidence>? SourceAssertion = null
);

public sealed record PeopleSourceContextBaselineReport(
    string State,
    string Version,
    string ProjectionVersion,
    string ActorFeaturesVersion,
    IReadOnlyList<string> FeatureOrder,
    string BundleManifestSha256,
    IReadOnlyDictionary<string, PeoplePairBaselineFile> InputFiles,
    string BaselineFile,
    string BaselineSha256,
    int Questions,
    int Pairs,
    int AllTasks,
    string MatcherAssemblySha256,
    string RunnerAssemblySha256,
    IReadOnlyList<PeoplePairBaselineArtifact> Artifacts,
    IReadOnlyList<string> MissingContext,
    bool PolicyActivationPermitted,
    string ContextFile,
    string ContextSha256,
    int ContextRows,
    string ContextProjectionVersion,
    string CreditExtractorVersion,
    string ActorEvidenceVersion,
    string ActorProjectionVersion
);
