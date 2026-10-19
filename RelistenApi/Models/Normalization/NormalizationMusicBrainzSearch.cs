using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Relisten.Api.Models.Normalization;

public enum MusicBrainzSearchArtistUse
{
    RecordingPerformer,
    WorkAuthor,
}

public sealed record MusicBrainzSearchArtistConstraint(
    MusicBrainzSearchArtistUse Use,
    string? Name = null,
    Guid? ExternalArtistMbid = null
);

/// <summary>Literal program retrieval context, never a claim that a target is this release's audio.</summary>
public sealed record MusicBrainzRecordingProgramConstraint(
    string? ReleaseTitle = null,
    NormalizationResourceKey? ReleaseIdentity = null
);

public sealed record MusicBrainzSearchQuery(
    MusicBrainzLookupKind Kind,
    string Text,
    MusicBrainzSearchArtistConstraint? RelatedArtist = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        MusicBrainzRecordingProgramConstraint? Program = null
);

public sealed record MusicBrainzSearchPlanIdentity(Guid PlanUuid, string RequestSha256);

public sealed record MusicBrainzSearchPageRequest(
    MusicBrainzSearchQuery Query,
    int Offset,
    MusicBrainzCacheMode CacheMode,
    TimeSpan? MaximumCacheAge = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        MusicBrainzSearchPlanIdentity? PlanIdentity = null
);

public sealed record MusicBrainzSearchDescriptor(
    string BuilderVersion,
    MusicBrainzLookupKind Kind,
    string RenderedQuery,
    int Limit,
    int Offset,
    NormalizationResourceKey Resource,
    string CaptureVersion,
    string RequestUrl
);

public sealed record MusicBrainzSearchAcquisition(
    Guid CommandUuid,
    MusicBrainzSearchPageRequest Request,
    MusicBrainzSearchDescriptor Descriptor,
    string AcquisitionVersion,
    MusicBrainzAcquisitionState State,
    DateTimeOffset? NotBefore,
    MusicBrainzAcquisitionResult? Result,
    IReadOnlyList<MusicBrainzAcquisitionAttempt> Attempts
);

/// <summary>Literal support is separate from the caller's performer/author interpretation; no identity authority is conferred.</summary>
public sealed record MusicBrainzSearchArtistSeed(
    MusicBrainzSearchArtistUse Use,
    ObservedValue<string>? Name = null,
    ObservedProviderIdentity? Identity = null,
    ObservedValue<string>? Role = null
);

public sealed record MusicBrainzSearchSeed(
    MusicBrainzLookupKind Kind,
    ObservedValue<string>? Text,
    IReadOnlyList<MusicBrainzSearchArtistSeed> RelatedArtists
);

public sealed record MusicBrainzSearchVariants(
    string Version,
    MusicBrainzSearchSeed Seed,
    IReadOnlyList<MusicBrainzSearchQuery> Queries,
    IReadOnlyList<string> Issues
);
