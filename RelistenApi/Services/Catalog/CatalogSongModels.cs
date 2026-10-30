using System;
using System.Collections.Generic;

namespace Relisten.Catalog;

/// <summary>Canonical song metadata and recording-derived discovery counts.</summary>
public sealed class CatalogSongDetail
{
    public string id { get; set; } = null!;
    public Guid artist_uuid { get; set; }
    public string name { get; set; } = null!;
    public string? kind { get; set; }
    public string slug { get; set; } = null!;
    public string? original_artist { get; set; }
    public int matched_track_count { get; set; }
    public int matched_show_count { get; set; }
    public IReadOnlyList<string> Aliases { get; set; } = [];
}

public sealed class CatalogImportedSong : CatalogTimestamps
{
    public long id { get; set; }
    public Guid uuid { get; set; }
    public Guid artist_uuid { get; set; }
    public string name { get; set; } = null!;
    public string slug { get; set; } = null!;
    public string upstream_identifier { get; set; } = null!;
    public int setlist_show_count { get; set; }
}

public sealed record CatalogArtistSongEntry(
    CatalogSongDetail? Song,
    CatalogImportedSong? ImportedSong
);

public sealed record CatalogImportedSongDetail(
    CatalogImportedSong Song,
    IReadOnlyList<CatalogShow> Shows
);

public sealed record CatalogSongWithShows(CatalogSongDetail Song, IReadOnlyList<CatalogShow> Shows);

public sealed record CatalogSongWithTracks(
    CatalogSongDetail Song,
    IReadOnlyList<CatalogTrack> Tracks
);
