using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>A complete role within a checked recording or track scope. Unknown file scope cannot be widened to recording scope.</summary>
public sealed record RecordingCreditCompleteScope(string Role, Guid? ScopeTrackUuid = null);

/// <summary>
/// Replaces this contributor's support only in explicitly complete successful scopes. Every
/// extracted mention in those scopes must have a corresponding evidenced credit. Unresolved
/// prose or file association prevents declaring that scope complete.
/// </summary>
public sealed record RecordingCreditContributorReplacement(
    NormalizationResourceKey Contributor,
    IReadOnlyList<RecordingCreditCompleteScope> CompleteScopes,
    IReadOnlyList<RecordingCredit> Credits
);

/// <summary>
/// Accepted publication of a published extraction. Consumers retain the original extraction
/// observation and complete raw read set in the publication work ticket. This is separate
/// from append-style PeoplePublication and grants no matching-policy authority by itself.
/// </summary>
public sealed record RecordingCreditReconciliation(
    Guid SourceUuid,
    Guid ExtractionAttemptUuid,
    IReadOnlyList<RecordingCreditContributorReplacement> Contributors
);
