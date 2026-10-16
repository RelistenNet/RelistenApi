using System;
using System.Text.Json.Serialization;

namespace Relisten.Ingestion;

/// <summary>Stable catalog artist identity used by all ingestion commands.</summary>
public readonly record struct ArtistId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public readonly record struct ArchiveItemId(string Value);

public readonly record struct PhishInShowId(int Value);

public readonly record struct SetlistFmId(string Value);

public readonly record struct PhantasyTourShowId(int Value);

public readonly record struct JerryShowId(string Value);

public readonly record struct LocalShowDate(string Value);

public readonly record struct CatalogSourceId(long Value);

public readonly record struct CatalogSetlistId(long Value);

/// <summary>Names the refresh scope without mixing maintenance or targeted-item flags into it.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RefreshArtist), "artist")]
[JsonDerivedType(typeof(RefreshArchiveArtist), "archive")]
[JsonDerivedType(typeof(RefreshPhishInArtist), "phish-in")]
[JsonDerivedType(typeof(RefreshPhishNetArtist), "phish-net")]
[JsonDerivedType(typeof(RefreshSetlistFmArtist), "setlist-fm")]
[JsonDerivedType(typeof(RefreshPhantasyTourArtist), "phantasy-tour")]
[JsonDerivedType(typeof(RefreshJerryArtist), "jerry")]
[JsonDerivedType(typeof(RefreshLocalArtist), "local")]
[JsonDerivedType(typeof(RefreshFilteredArtist), "filtered-artist")]
public abstract record ArtistRefreshCommand(ArtistId Artist);

public sealed record RefreshArtist(ArtistId Artist) : ArtistRefreshCommand(Artist);

public sealed record RefreshArchiveArtist(ArtistId Artist, int? Year = null)
    : ArtistRefreshCommand(Artist);

public sealed record RefreshPhishInArtist(ArtistId Artist, int? FromYear = null)
    : ArtistRefreshCommand(Artist);

public sealed record RefreshPhishNetArtist(ArtistId Artist, int? Year = null)
    : ArtistRefreshCommand(Artist);

public sealed record RefreshSetlistFmArtist(ArtistId Artist) : ArtistRefreshCommand(Artist);

public sealed record RefreshPhantasyTourArtist(ArtistId Artist) : ArtistRefreshCommand(Artist);

public sealed record RefreshJerryArtist(ArtistId Artist) : ArtistRefreshCommand(Artist);

public sealed record RefreshLocalArtist(ArtistId Artist) : ArtistRefreshCommand(Artist);

/// <summary>Preserves the existing thin-import filters; Year is not uniform across providers.</summary>
public sealed record RefreshFilteredArtist(ArtistId Artist, int Year)
    : ArtistRefreshCommand(Artist);

public sealed record ImportArchiveItem(ArtistId Artist, ArchiveItemId Item);

public sealed record RepairJerryVenues(ArtistId Artist);

public sealed record RebuildArtistCatalog(ArtistId Artist);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RefreshCatalog), "full")]
[JsonDerivedType(typeof(RefreshRecentlyActiveArtists), "thin")]
[JsonDerivedType(typeof(RebuildCatalog), "rebuild")]
public abstract record CatalogBatchCommand;

public sealed record RefreshCatalog : CatalogBatchCommand;

public sealed record RefreshRecentlyActiveArtists : CatalogBatchCommand;

public sealed record RebuildCatalog : CatalogBatchCommand;

public sealed record DiscoverArchiveArtists;
