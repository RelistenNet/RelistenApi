using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>Actual policy-free pair comparisons from exact retained development mention wording.</summary>
public sealed class PeoplePairBaselineBatch
{
    public const string Version = "people-pair-baseline-v1";
    public const string ProjectionVersion = "people-mentions-name-artist-five-v1";
    public static IReadOnlyList<string> FeatureOrder { get; } =
        Array.AsReadOnly(
            new[]
            {
                "name_token_similarity",
                "exact_name",
                "artist_overlap",
                "contact_agreement",
                "profile_agreement",
            }
        );
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>
    /// Authenticates selection first, then exports every declared candidate edge without using
    /// label answers as matcher inputs. Partial output is retained on interruption; manifest.json is completion.
    /// </summary>
    public async Task<PeoplePairBaselineReport> RunAsync(
        string repositoryRoot,
        string bundleDirectory,
        string newOutputDirectory,
        string frozenManifestSha256,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(repositoryRoot);
        var bundle = Path.GetFullPath(bundleDirectory);
        var output = Path.GetFullPath(newOutputDirectory);
        PeoplePairBaselineBundle.Require(
            !Directory.Exists(output) && !File.Exists(output),
            "Choose a new people baseline output directory."
        );
        var inputs = await PeoplePairBaselineBundle.ReadAsync(
            root,
            bundle,
            frozenManifestSha256,
            cancellationToken
        );

        Directory.CreateDirectory(output);
        var partial = Path.Combine(output, "baseline.partial.jsonl");
        var pairs = await WriteComparisons(inputs, partial, cancellationToken);
        await inputs.RecheckAsync(cancellationToken);
        var artifacts = new[]
        {
            await AssemblyArtifact(
                typeof(IdentityCandidateMatcher).Assembly.Location,
                cancellationToken
            ),
            await AssemblyArtifact(
                typeof(PeoplePairBaselineBatch).Assembly.Location,
                cancellationToken
            ),
        };
        var report = new PeoplePairBaselineReport(
            "completed",
            Version,
            ProjectionVersion,
            IdentityCandidateMatcher.PeopleActorFeaturesVersion,
            FeatureOrder,
            inputs.ManifestHash,
            inputs.Files,
            "baseline.jsonl",
            await FileHash(partial, cancellationToken),
            inputs.Questions.Count(question => question.Kind != "actor_kind"),
            pairs,
            inputs.Questions.Count,
            artifacts[0].Sha256,
            artifacts[1].Sha256,
            artifacts,
            [
                "canonical_identity",
                "actor_kind",
                "contacts",
                "profiles",
                "source_supported_alias_lists",
            ],
            false
        );
        await using (
            var stream = new FileStream(
                Path.Combine(output, "manifest.partial.json"),
                FileMode.CreateNew,
                FileAccess.Write
            )
        )
        {
            await JsonSerializer.SerializeAsync(stream, report, Json, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        // A failure between these renames can leave baseline.jsonl alone. Only manifest.json
        // marks completion; retain that incomplete directory rather than overwriting it.
        File.Move(partial, Path.Combine(output, "baseline.jsonl"));
        File.Move(
            Path.Combine(output, "manifest.partial.json"),
            Path.Combine(output, "manifest.json")
        );
        return report;
    }

    private static async Task<int> WriteComparisons(
        PeoplePairBaselineBundle bundle,
        string path,
        CancellationToken ct
    )
    {
        var matcher = new IdentityCandidateMatcher();
        var pairs = 0;
        await using var output = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read
        );
        foreach (var question in bundle.Questions.Where(question => question.Kind != "actor_kind"))
        {
            var focus = bundle.Mentions[question.Focus];
            foreach (var candidateId in question.Candidates)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = bundle.Mentions[candidateId];
                var comparison = matcher.ComparePeople(Project(focus), Project(candidate));
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
                    Features(comparison),
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

    // Mention IDs and artist UUIDs identify source ownership, not learned identity classes.
    // Kind, contacts/profiles, alias lists and external IDs are deliberately absent: the
    // annotation answer cannot supply facts to the model that the source projection lacks.
    private static PersonMatchRecord Project(PeoplePairBaselineBundle.Mention mention) =>
        new(
            null,
            null,
            [new IdentityMatchingName(mention.Name)],
            [],
            new HashSet<Guid> { mention.ArtistUuid }
        );

    private static IReadOnlyList<double> Features(IdentityCandidateComparison comparison)
    {
        double Agrees(string feature) =>
            comparison.Features.Any(value =>
                value.Name == feature && value.Agreement == IdentityFeatureAgreement.Agrees
            )
                ? 1
                : 0;
        return
        [
            comparison.NameTokenSimilarity,
            Agrees("name"),
            Agrees("artist_context"),
            Agrees("actor_contact"),
            Agrees("actor_profile"),
        ];
    }

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

public sealed record PeoplePairBaselineFile(string Sha256, long Bytes, int? Rows);

public sealed record PeoplePairBaselineArtifact(string Path, long Bytes, string Sha256);

public sealed record PeoplePairBaselineRow(
    string TaskId,
    string TaskKind,
    string FocusMentionId,
    string CandidateMentionId,
    string FocusEvidenceId,
    string CandidateEvidenceId,
    bool SameRecording,
    int DeclaredCandidateCount,
    IReadOnlyList<double> Features,
    IdentityCandidateComparison Comparison,
    bool ProjectionSupported,
    string? ProjectionIssue
);

public sealed record PeoplePairBaselineReport(
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
    bool PolicyActivationPermitted
);
