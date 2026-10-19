using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace Relisten.Normalization.Batch;

/// <summary>Externally pinned dictionary evidence and an unexecuted literal-query queue.</summary>
public sealed record CompositionWorkSearchRequest(
    string Version,
    Guid PlanUuid,
    ShadowCompositionAcquisitionFile DictionaryManifest,
    ShadowCompositionAcquisitionFile TaskArtifact,
    CompositionWorkSearchLimits Limits
);

public sealed record CompositionWorkSearchLimits(
    int MaximumServiceCalls = 2000,
    long MaximumNewResponseBytes = 256L * 1024 * 1024,
    long MaximumArtifactBytes = 512L * 1024 * 1024,
    long MinimumFreeBytes = 5905580032,
    TimeSpan? MaximumDuration = null
);

/// <summary>The original typed request has no performer/author constraint or hydration command.</summary>
public sealed record CompositionWorkSearchTask(
    string RetrievalTitleKey,
    ShadowCompositionAcquisitionCommand Command,
    IReadOnlyList<MusicDictionaryName> SourceMembers
);

public sealed record CompositionWorkSearchTaskResult(
    CompositionWorkSearchTask Task,
    string State = "unexecuted",
    ShadowCompositionAcquisitionReceipt? Receipt = null,
    string? InputFile = null,
    Guid? InputUuid = null,
    string? InputSha256 = null,
    string? PageState = null,
    long? ReportedCount = null,
    int? ReturnedSlots = null,
    IReadOnlyList<string>? Issues = null
);

/// <summary>One durable run; resumption keeps its original deadline and dispatch reservations.</summary>
public sealed record CompositionWorkSearchCheckpoint(
    string Version,
    string RequestSha256,
    Guid PlanUuid,
    DateTimeOffset Deadline,
    int ReservedServiceCalls,
    IReadOnlyDictionary<Guid, long> AttemptBodyCharges,
    IReadOnlyList<Guid> UnmeasuredAttempts,
    Guid? InFlightCommand,
    IReadOnlyList<CompositionWorkSearchTaskResult> Tasks,
    string State,
    bool ExternalUniverseComplete = false,
    bool PublicationPermitted = false
);

/// <summary>Reads the same saved response/capture envelopes as the public acquisition CLI.</summary>
public interface ICompositionWorkSearchClient : IShadowCompositionAcquisitionClient
{
    Task<MusicBrainzSavedResponse?> ReadResponseAsync(Guid attemptUuid, CancellationToken ct);
    Task<NormalizationCaptureResult?> ReadCaptureAsync(Guid fetchUuid, CancellationToken ct);
}

public sealed class CompositionWorkSearchClient(
    NormalizationMusicBrainzInputService service,
    NormalizationMusicBrainzInputStore store,
    NormalizationEvidenceService evidence
) : ICompositionWorkSearchClient
{
    private readonly ShadowCompositionAcquisitionClient acquisition = new(service, evidence);

    public Task<ShadowCompositionAcquisitionReceipt?> GetAsync(
        ShadowCompositionAcquisitionCommand command,
        CancellationToken ct
    ) => acquisition.GetAsync(command, ct);

    public Task<ShadowCompositionAcquisitionReceipt> AdvanceAsync(
        ShadowCompositionAcquisitionCommand command,
        CancellationToken ct
    ) => acquisition.AdvanceAsync(command, ct);

    public Task<NormalizationInputRevision?> ReadInputAsync(Guid inputUuid, CancellationToken ct) =>
        evidence.GetInputAsync(inputUuid, ct);

    public Task<MusicBrainzSavedResponse?> ReadResponseAsync(
        Guid attemptUuid,
        CancellationToken ct
    ) => store.GetResponseAsync(attemptUuid, ct);

    public Task<NormalizationCaptureResult?> ReadCaptureAsync(
        Guid fetchUuid,
        CancellationToken ct
    ) => evidence.GetCaptureAsync(fetchUuid, ct);
}
