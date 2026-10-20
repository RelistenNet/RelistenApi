using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>One resumable phase of a recording's journey into accepted song discovery.</summary>
public enum RecordingMusicDeliveryPhase
{
    Admission,
    Extraction,
    DictionaryCapture,
    DictionaryResolution,
    RecordingResolution,
    Contents,
    Completion,
    Finished,
}

public enum RecordingMusicDeliveryDisposition
{
    Completed,
    Review,
    Stale,
    Unresolved,
}

/// <summary>
/// A terminal delivery receipt. Completed includes published file contents; an extraction
/// acknowledgement alone cannot complete delivery. Attention outcomes retain earlier accepted data.
/// </summary>
public sealed record RecordingMusicDeliveryResult(
    RecordingMusicDeliveryDisposition Disposition,
    string Reason,
    int Accepted = 0,
    int Review = 0,
    int Unresolved = 0
);

/// <summary>
/// Durable orchestration coordinates over existing domain attempts. These UUIDs are allocated
/// before extraction acknowledgement and survive cancellation, concurrent calls and host restarts.
/// DictionaryFetchUuid names an actual complete imported-catalog capture, including an empty one.
/// </summary>
public sealed record RecordingMusicDelivery(
    Guid HandoffUuid,
    Guid WorkUuid,
    Guid SourceUuid,
    Guid ArtistUuid,
    string ProcessingVersion,
    Guid DictionaryAttemptUuid,
    Guid RepertoireAttemptUuid,
    Guid ContentAttemptUuid,
    RecordingMusicDeliveryPhase Phase,
    Guid? DictionaryFetchUuid = null,
    RecordingMusicDeliveryResult? Result = null
);

public sealed record RecordingMusicDeliveryPage(
    IReadOnlyList<RecordingMusicDelivery> Deliveries,
    Guid? NextCursor
);
