using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relisten.Api.Models.Normalization;

/// <summary>A normalization owner, not a generic relationship between arbitrary domain entities.</summary>
public sealed record NormalizationSubjectKey(string Domain, string Kind, Guid Uuid);

/// <summary>Exact resource state consumed by one run. Resource generations detect A → B → A changes.</summary>
public sealed record NormalizationInputDependency(
    Guid ResourceUuid,
    Guid RevisionUuid,
    long Generation
);

/// <summary>Accepted-state dependency; even an unchanged desired generation can publish a new interpretation.</summary>
public sealed record NormalizationSubjectDependency(
    Guid SubjectUuid,
    long DesiredGeneration,
    long EditorialRevision,
    Guid? PublishedWorkUuid
);

/// <summary>
/// A primary catalog snapshot's artist watermark. It covers binding and stored metadata changes;
/// it is an opaque revision, not a count of recordings or committed transactions.
/// </summary>
public sealed record NormalizationCatalogDependency(Guid ArtistUuid, long Generation);

/// <summary>
/// Fences a global source-identifier lookup, including a matching source arriving under
/// another artist. Artist clocks alone cannot establish that a global match remains unique.
/// </summary>
public sealed record NormalizationSourceIdentityDependency(
    string UpstreamIdentifier,
    long Generation
);

/// <summary>
/// Fences provider-mapping searches, including an artist mapping that did not exist in the
/// caller's snapshot. Generation zero is valid; null on a ticket means no registry dependency.
/// </summary>
public sealed record NormalizationMappingRegistryDependency(long Generation);

/// <summary>
/// Fences an imported venue by its stable catalog UUID, including a missing or unowned venue.
/// The retained clock survives deletion; zero means no catalog change has yet been observed.
/// </summary>
public sealed record NormalizationImportedVenueDependency(Guid VenueUuid, long Generation);

/// <summary>
/// Durable read set captured before extraction. Load immutable input bytes by RevisionUuid.
/// A retry reuses WorkUuid; a new attempt after changed evidence needs a new WorkUuid.
/// </summary>
public sealed record NormalizationWorkTicket(
    Guid WorkUuid,
    Guid SubjectUuid,
    long DesiredGeneration,
    long EditorialRevision,
    string PolicyVersion,
    Guid? PreviousPublishedWorkUuid,
    IReadOnlyList<NormalizationInputDependency> Inputs,
    IReadOnlyList<NormalizationSubjectDependency> Dependencies,
    IReadOnlyList<NormalizationCatalogDependency>? CatalogInputs = null,
    IReadOnlyList<NormalizationSourceIdentityDependency>? SourceIdentities = null,
    NormalizationMappingRegistryDependency? MappingRegistry = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<NormalizationImportedVenueDependency>? ImportedVenues = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<NormalizationCandidateUniverseDependency>? CandidateUniverses = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<NormalizationSubjectObservation>? OwnerObservations = null
);

public enum NormalizationPublicationStatus
{
    Published,
    Stale,
}

/// <summary>Replay returns the original result and never invokes the domain write callback again.</summary>
public sealed record NormalizationPublicationResult(
    Guid WorkUuid,
    NormalizationPublicationStatus Status,
    bool Replayed
);

/// <summary>
/// Audit metadata for a trusted domain command. The domain service owns the typed payload and
/// action semantics; this shared store owns idempotency, editorial revision and atomic commit.
/// AdditionalSubjectUuids names pre-registered identity owners whose accepted meaning also changes.
/// All named owners are locked before the domain callback and invalidated in the same transaction.
/// </summary>
public sealed record NormalizationReviewCommand(
    Guid OperationUuid,
    Guid SubjectUuid,
    long ExpectedEditorialRevision,
    string Action,
    JsonElement Payload,
    string Reason,
    string Operator,
    IReadOnlyList<Guid>? AdditionalSubjectUuids = null
);

public sealed record NormalizationReviewResult(
    Guid OperationUuid,
    long EditorialRevision,
    bool Replayed
);

/// <summary>A stale revision or an operation UUID reused for different content cannot change accepted data.</summary>
public sealed class NormalizationConflictException(string message) : Exception(message);

/// <summary>Durable owner state. A pending page also compares input and accepted dependency snapshots.</summary>
public sealed record NormalizationSubjectState(
    Guid SubjectUuid,
    NormalizationSubjectKey Subject,
    long DesiredGeneration,
    long EditorialRevision,
    string PolicyVersion,
    long PublishedGeneration,
    Guid? PublishedWorkUuid
);
