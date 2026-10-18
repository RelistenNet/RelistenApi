using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Relisten.Api.Models.Normalization;

public enum MusicMaterializationDisposition
{
    Pending,
    Completed,
    Stale,
}

public enum MusicMaterializationDecisionDisposition
{
    Accepted,
    Retained,
    Withdrawn,
    Review,
    Unresolved,
}

/// <summary>Original peer configuration and current factual bindings from one primary snapshot.</summary>
public sealed record MusicMaterializationOwnerContext(
    NormalizationInputContributionsSnapshot Membership,
    RepertoireResolutionReadSet Bindings,
    IReadOnlyList<NormalizationBindingInputReference> InputReferences
);

/// <summary>Retry freezes lineage first; the completed repertoire receipt is selected once before preparation.</summary>
public sealed record MusicMaterializationRetrySelection(
    Guid RegistrationUuid,
    Guid PreviousAttemptUuid
);

/// <summary>A permanent rejection cannot enroll unavailable proof or retry unchanged inputs indefinitely.</summary>
public sealed record MusicMaterializationRetryRequirement(
    Guid? RepertoireResolutionAttemptUuid,
    string ProcessorVersion,
    string LimitProfile,
    string Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        NormalizationSubjectObservation? CapacityOwner = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        NormalizationInputContributionsCapacityStamp? CapacityMembership = null
);

/// <summary>Only explicit music-consumer capacity refusals use this type; incidental failures remain resumable.</summary>
public sealed class MusicMaterializationCapacityException(
    string message,
    NormalizationSubjectObservation? owner = null,
    NormalizationInputContributionsCapacityStamp? membership = null
) : InvalidOperationException(message)
{
    public NormalizationSubjectObservation? Owner { get; } = owner;
    public NormalizationInputContributionsCapacityStamp? Membership { get; } = membership;
}

public sealed record MusicMaterializationResult(
    Guid AttemptUuid,
    MusicMaterializationDisposition Disposition,
    string Reason,
    int Accepted = 0,
    int Retained = 0,
    int Review = 0,
    int Unresolved = 0,
    MusicMaterializationRetryRequirement? RetryRequirement = null,
    bool Replayed = false
);
