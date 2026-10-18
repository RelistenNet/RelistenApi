using System.Collections.Generic;

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
