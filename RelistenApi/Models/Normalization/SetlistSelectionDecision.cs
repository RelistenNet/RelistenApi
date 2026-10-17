using System;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// Chooses one display account for a show, independently of that account's source metadata.
/// Null EditionUuid clears the choice. ExpectedSelectedEditionUuid records the previous choice,
/// including a legacy selection that predates the editorial ledger.
/// </summary>
public sealed record SetlistSelectionDecision(
    Guid OperationUuid,
    Guid SubjectUuid,
    Guid ShowUuid,
    long ExpectedEditorialRevision,
    Guid? ExpectedSelectedEditionUuid,
    Guid? EditionUuid,
    long? ExpectedEditionRevision,
    string Reason
);

/// <summary>An edition's current metadata stamp, without making its content editorially reviewed.</summary>
public sealed record SetlistSelectionEdition(
    Guid EditionUuid,
    Guid ArtistUuid,
    Guid? ShowUuid,
    long Revision,
    bool Retracted,
    bool Selected
);

/// <summary>
/// One primary snapshot of the show, display choice and proposed target. A null Owner requires
/// explicit registration of (songs, show_setlist_selection, ShowUuid) before submitting a decision.
/// A null ArtistUuid means the catalog show is missing; retained selections can still be cleared.
/// </summary>
public sealed record SetlistSelectionContext(
    Guid ShowUuid,
    Guid? ArtistUuid,
    NormalizationSubjectObservation? Owner,
    SetlistSelectionEdition? SelectedEdition,
    SetlistSelectionEdition? ProposedEdition
);
