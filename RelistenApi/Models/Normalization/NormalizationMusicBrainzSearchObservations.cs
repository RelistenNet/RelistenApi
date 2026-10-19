using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// Facts from one retained search page. Its opaque resource key cannot establish which query
/// produced it; consumers must authenticate that association through the acquisition receipt.
/// Null count/offset/hits mean missing or invalid fields, never a clean zero-result page.
/// </summary>
public sealed record MusicBrainzSearchPageObservation
{
    public ProviderObservationOrigin Origin { get; init; } = null!;
    public ObservedValue<string>? Created { get; init; }
    public ObservedValue<long>? Count { get; init; }
    public ObservedValue<long>? Offset { get; init; }
    public IReadOnlyList<MusicBrainzSearchHitObservation>? Hits { get; init; }
    public IReadOnlyList<ProviderDecodeIssue> Issues { get; init; } = [];

    /// <summary>Offset paging cannot prove completeness of the changing external search index.</summary>
    public bool ExternalUniverseComplete => false;
}

/// <summary>
/// A search summary, not a full lookup or an accepted catalog correspondence. SourceIndex is
/// the original zero-based array slot, including positions occupied by malformed members.
/// Scores are original retrieval metadata; they supply no acceptance confidence or authority.
/// </summary>
public sealed record MusicBrainzSearchHitObservation
{
    public NormalizedEvidenceReference Evidence { get; init; } = null!;
    public int SourceIndex { get; init; }
    public MusicBrainzEntityReference Entity { get; init; } = null!;

    /// <summary>Unsigned integer metadata read from a JSON number or decimal string at its original citation.</summary>
    public ObservedValue<int>? Score { get; init; }
    public IReadOnlyList<MusicBrainzArtistCreditObservation>? ArtistCredits { get; init; }
    public IReadOnlyList<MusicBrainzAliasObservation>? Aliases { get; init; }
    public IReadOnlyList<MusicBrainzIswcObservation>? Iswcs { get; init; }
    public IReadOnlyList<MusicBrainzSearchWorkArtistObservation>? WorkArtists { get; init; }

    /// <summary>Actual returned release memberships, not an inference from the query or score.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<MusicBrainzSearchReleaseObservation>? Releases { get; init; }

    /// <summary>Search summaries never assert an exhaustive set of authors.</summary>
    public bool? CompleteAuthorship => null;
}

/// <summary>
/// An original work-search artist relation summary. Type and direction retain provider wording;
/// they do not allocate a canonical person or convert a performer/unknown role into an author.
/// Uninterpreted fields remain recoverable through the relationship-object citation.
/// </summary>
public sealed record MusicBrainzSearchWorkArtistObservation
{
    public NormalizedEvidenceReference Evidence { get; init; } = null!;
    public ObservedValue<string>? Type { get; init; }
    public ObservedValue<string>? Direction { get; init; }
    public ObservedValue<string>? TargetType { get; init; }
    public MusicBrainzEntityReference? Artist { get; init; }
}

/// <summary>
/// One original recording-search release slot. Duplicate release IDs remain separate;
/// editions, release credits and partial matching tracks do not identify a target rendition.
/// </summary>
public sealed record MusicBrainzSearchReleaseObservation
{
    public NormalizedEvidenceReference Evidence { get; init; } = null!;
    public int SourceIndex { get; init; }
    public MusicBrainzEntityReference Release { get; init; } = null!;
    public IReadOnlyList<MusicBrainzArtistCreditObservation>? ArtistCredits { get; init; }
    public MusicBrainzEntityReference? ReleaseGroup { get; init; }
    public ObservedValue<string>? Status { get; init; }
    public ObservedValue<string>? Date { get; init; }
    public ObservedValue<string>? Country { get; init; }
    public ObservedValue<long>? TrackCount { get; init; }
    public IReadOnlyList<MusicBrainzSearchMediumObservation>? Media { get; init; }
}

/// <summary>
/// A returned medium and its matching track window. SourceIndex preserves source order;
/// Position, TrackCount and TrackOffset are independent provider assertions when supplied.
/// </summary>
public sealed record MusicBrainzSearchMediumObservation
{
    public NormalizedEvidenceReference Evidence { get; init; } = null!;
    public int SourceIndex { get; init; }
    public ObservedProviderIdentity? Identity { get; init; }
    public ObservedValue<long>? Position { get; init; }
    public ObservedValue<string>? Format { get; init; }
    public ObservedValue<long>? TrackCount { get; init; }
    public ObservedValue<long>? TrackOffset { get; init; }
    public IReadOnlyList<MusicBrainzSearchTrackObservation>? Tracks { get; init; }
}

/// <summary>
/// An original release-track slot under the recording search hit. Its qualified
/// release-track ID differs from the enclosing recording MBID; no audio time is inferred.
/// </summary>
public sealed record MusicBrainzSearchTrackObservation
{
    public NormalizedEvidenceReference Evidence { get; init; } = null!;
    public int SourceIndex { get; init; }
    public ObservedProviderIdentity? Identity { get; init; }
    public ObservedValue<string>? Number { get; init; }
    public ObservedValue<string>? Title { get; init; }
    public ObservedValue<long>? Position { get; init; }
    public ObservedValue<long>? Length { get; init; }
}
