using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>One explicitly selected target in the frozen, scoped hypothesis image.</summary>
public sealed record CompositionSelectionSongKey(
    string Scope,
    Guid ArtistUuid,
    string SongEntityKey
);

/// <summary>
/// Operator-approved limits. Mode is initial or retry_failed; changing it requires
/// a new plan and output, never a resume. Job caps are immutable once registered.
/// A retry may reuse that job's remaining allowance or name a deliberately new job.
/// Concurrency defaults to one and permits at most four requests; output remains ordered.
/// </summary>
public sealed record CompositionSelectionPlan(
    string Version,
    MusicConsolidationArtifact HypothesisManifest,
    Guid JobUuid,
    string Mode,
    IReadOnlyList<CompositionSelectionSongKey> Songs,
    int MaximumInputs,
    decimal CapUsd,
    int MaximumTotalAttempts,
    int MaximumDurationSeconds,
    long MaximumOutputBytes,
    long MinimumFreeBytes,
    int Concurrency = 1,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        MusicConsolidationArtifact? TargetContextManifest = null
);

public sealed record CompositionSelectionOrigin(string Chunk, long Line, string Sha256);

/// <summary>
/// A source-only export for the original plan's exact targets. The enriched plan
/// pins this manifest; this manifest pins the base plan, avoiding a circular hash.
/// </summary>
public sealed record CompositionSelectionTargetContextManifest(
    string Version,
    string State,
    MusicConsolidationArtifact SelectionPlan,
    MusicConsolidationArtifact HypothesisManifest,
    MusicConsolidationArtifact MusicInputManifest,
    MusicConsolidationArtifact MusicOutputManifest,
    MusicConsolidationArtifact Catalogue,
    MusicConsolidationArtifact Normalization,
    MusicConsolidationArtifact Targets,
    MusicConsolidationArtifact Texts,
    IReadOnlyList<CompositionSelectionContextSourceArtifact> SourceManifests,
    IReadOnlyList<CompositionSelectionContextSourceArtifact> WitnessChunks,
    long TargetCount,
    bool Complete
);

public sealed record CompositionSelectionContextSourceArtifact(
    string Path,
    long Bytes,
    string Sha256,
    long Rows,
    string? Run = null
);

/// <summary>Request measurements are pure; eligibility grants no permission to spend.</summary>
public sealed record CompositionSelectionPreparedRow(
    CompositionSelectionSongKey Key,
    string? InputSha256,
    CompositionSelectionOrigin? HypothesisOrigin,
    CompositionHypothesisRequestDescription? Request,
    string? SkipReason
);

public sealed record CompositionSelectionPreflight(
    string Version,
    string PlanSha256,
    string Mode,
    long HypothesisSongs,
    long CandidateBearingSongs,
    long RetainedWorks,
    int SelectedInputs,
    int EligibleInputs,
    long RemainingCandidateBearingSongs,
    decimal MaximumNewReservationUsd,
    IReadOnlyDictionary<string, long> SkipReasons,
    IReadOnlyList<CompositionSelectionPreparedRow> Rows
);

/// <summary>The original song/work facts stay in the pinned hypothesis image.</summary>
public sealed record CompositionSelectionBatchRow(
    CompositionSelectionPreparedRow Input,
    CompositionHypothesisSelection? Selection
);

public sealed record CompositionSelectionBatchReport(
    string Version,
    string State,
    string PlanSha256,
    Guid JobUuid,
    string Method,
    CompositionSelectionPreflight Preflight,
    long ProcessedInputs,
    IReadOnlyDictionary<string, long> Outcomes,
    int CacheHits,
    decimal? JobAccountedUsd,
    decimal KnownActualUsd,
    IReadOnlyList<MusicConsolidationArtifact> Chunks
)
{
    public bool PublicationPermitted => false;
    public bool ExternalUniverseComplete => false;
}
