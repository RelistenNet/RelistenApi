using System;
using System.Text.Json;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// An assertion's exact retained identity. Source-field coordinates remain addressable across
/// revisions; assertion/reference UUIDs are local to this field, revision and producer. The
/// ResourceUuid must identify OriginalScope.Resource, not a second fabricated proposal key.
/// </summary>
public sealed record SourceActorAliasCoordinates(
    Guid SourceUuid,
    NormalizationResourceKey Contributor,
    Guid ResourceUuid,
    Guid InputRevisionUuid,
    string FieldJsonPointer,
    string ProducerVersion,
    Guid AssertionUuid,
    Guid ActorReferenceUuid,
    string FieldSha256
);

/// <summary>
/// Retained authority linking one original source assertion to an existing original person.
/// Redirects do not rewrite PersonUuid or either source owner. JSON receipt object shape is
/// not policy authentication; the later accepted writer must authenticate the saved scope,
/// assertion, authority receipt and original read set before inserting this record.
/// Application/stage UUIDs are retained coordinates until their domain storage is introduced.
/// </summary>
public sealed record SourceActorAliasAssignment(
    Guid Uuid,
    SourceActorAliasCoordinates Coordinates,
    string AssertionSha256,
    SourceActorAliasAssertion<NormalizedEvidenceReference> OriginalAssertion,
    NormalizationRecordingSourceActorAliasScope OriginalScope,
    JsonElement AuthorityReceipt,
    Guid PersonUuid,
    Guid SourceResolutionOwnerUuid,
    Guid ExtractionOwnerUuid,
    Guid ExtractionAttemptUuid,
    Guid ApplicationAttemptUuid,
    Guid StageUuid,
    string Basis,
    Guid IntroducedWorkUuid,
    Guid? RetiredWorkUuid
);

/// <summary>
/// Actual accepted canonical assertion UUID, rather than the UUID requested by a coalescing
/// write. Authority is current only while this support and its assignment are both unretired.
/// Retirement retains the row and never creates a canonical metadata-review retraction.
/// </summary>
public sealed record PersonAliasContributorSupport(
    Guid Uuid,
    Guid AssignmentUuid,
    Guid AliasUuid,
    Guid IntroducedWorkUuid,
    Guid? RetiredWorkUuid
);

/// <summary>
/// Independent append authority. Null introduction records migration backfill, not a guessed
/// Work UUID. This row is append-only and is unrelated to Work's UnmanagedBaseline bindings.
/// The canonical alias still needs accepted state; metadata review remains independent.
/// </summary>
public sealed record PersonAliasUnmanagedSupport(
    Guid Uuid,
    Guid AliasUuid,
    Guid? IntroducedWorkUuid
);

/// <summary>
/// Current projection of immutable trusted review decisions. Absence means revision zero;
/// the first decision writes one, and subsequent compare-and-swap decisions increment by one.
/// Only accepted state selects an existing identity. Prior snapshots and decision receipts
/// remain retained when this projection changes.
/// </summary>
public sealed record SourceActorAliasReviewState(
    Guid Uuid,
    SourceActorAliasCoordinates Coordinates,
    string AssertionSha256,
    SourceActorAliasAssertion<NormalizedEvidenceReference> OriginalAssertion,
    NormalizationRecordingSourceActorAliasScope OriginalScope,
    Guid SourceResolutionOwnerUuid,
    long ReviewRevision,
    string State,
    Guid? SelectedPersonUuid,
    Guid LastOperationUuid
);
