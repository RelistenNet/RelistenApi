using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>Original typed source output; scope remains in its captured catalog namespace.</summary>
public sealed record SharedPeopleLocator(string Run, string Chunk, long Line, string Sha256);

/// <summary>Exact source actor and its credit occurrences, never an accepted person.</summary>
public sealed record SharedPeopleOccurrence(
    string OccurrenceKey,
    string BucketId,
    ShadowPerson Person,
    IReadOnlyList<string> CreditKeys,
    SharedPeopleLocator Origin
);

/// <summary>Equivalent matching facts stored once; every original occurrence has its own ledger row.</summary>
public sealed record SharedPeopleBucket(
    string BucketId,
    string RepresentativeOccurrence,
    PersonMatchRecord Facts,
    bool ProjectionComplete,
    IReadOnlyList<NormalizationTextEvidence> Evidence,
    long Occurrences,
    long DistinctSources,
    long DistinctArtists
);

/// <summary>
/// Directed comparison of two fact buckets under the shared domain policy. Membership
/// is proposed only when Supported is true; neither an account nor a name alone suffices.
/// </summary>
public sealed record SharedPeopleSupport(
    string AnchorBucketId,
    string CandidateBucketId,
    bool Supported,
    bool HasConflict,
    bool Uncertain,
    bool AccountAgrees,
    IReadOnlyList<IdentityMatchingFeature> Features,
    string Method = "direct_name_or_explicit_alias_and_attributed_account",
    bool AcceptedIdentity = false,
    bool QualifiedForAutomaticAcceptance = false
);

internal sealed record SharedPeopleActor(
    ShadowPerson Person,
    PersonMatchRecord Facts,
    bool Complete,
    IReadOnlyList<NormalizationTextEvidence> Evidence,
    string[] CreditKeys
);
