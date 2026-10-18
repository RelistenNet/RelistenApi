using System;

namespace Relisten.Api.Models.Normalization;

internal enum CanonicalPersonAliasWriteDisposition
{
    Accepted,
    ExactDuplicate,
    Pending,
    ProtectedReviewedNaturalClaim,
    ConflictingUuid,
}

/// <summary>
/// Canonical storage result, separate from source-support authority. Only accepted
/// results expose an assertion to which an authenticated publisher may attach support.
/// Pending retries retain the actual pending UUID; review protection grants no support.
/// AssertionInserted tracks new pending history separately from accepted support authority.
/// </summary>
internal sealed record CanonicalPersonAliasWriteOutcome(
    CanonicalPersonAliasWriteDisposition Disposition,
    Guid? AliasUuid,
    bool AcceptedFactChanged = false,
    bool AssertionInserted = false
)
{
    public bool Accepted =>
        Disposition
            is CanonicalPersonAliasWriteDisposition.Accepted
                or CanonicalPersonAliasWriteDisposition.ExactDuplicate;
}
