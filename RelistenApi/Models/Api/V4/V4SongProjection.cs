using System.Linq;
using Relisten.Catalog;
using Relisten.Normalization;

namespace Relisten.Api.Models.Api.V4;

public static partial class V4CatalogProjection
{
    public static ArtistSongEntryResource SongEntry(CatalogArtistSongEntry entry) =>
        new(
            entry.Song is not null ? SongEntryKind.Song : SongEntryKind.ImportedSong,
            entry.Song is { } song ? SongDetail(song) : null,
            entry.ImportedSong is { } imported ? ImportedSong(imported) : null
        );

    public static SongDetailResource SongDetail(CatalogSongDetail song) =>
        ProjectSong<SongDetailResource>(song);

    public static SongWithShowsResource SongWithShows(CatalogSongWithShows detail) =>
        ProjectSong<SongWithShowsResource>(detail.Song) with
        {
            Shows = detail.Shows.Select(ShowSummary).ToArray(),
        };

    public static SongWithTracksResource SongWithTracks(CatalogSongWithTracks detail) =>
        ProjectSong<SongWithTracksResource>(detail.Song) with
        {
            Tracks = detail.Tracks.Select(Track).ToArray(),
        };

    private static T ProjectSong<T>(CatalogSongDetail song)
        where T : SongDetailResource, new() =>
        new()
        {
            Id = SongId.Parse(song.id),
            ArtistId = new(song.artist_uuid),
            Name = song.name,
            Kind = song.kind is { } kind ? NormalizationEnums.Parse<SongKind>(kind) : null,
            Slug = song.slug,
            OriginalArtist = song.original_artist,
            Aliases = song.Aliases,
            MatchedTrackCount = song.matched_track_count,
            MatchedShowCount = song.matched_show_count,
        };

    public static ImportedSongResource ImportedSong(CatalogImportedSong song) =>
        ProjectImportedSong<ImportedSongResource>(song);

    public static ImportedSongWithShowsResource ImportedSongWithShows(
        CatalogImportedSongDetail detail
    ) =>
        ProjectImportedSong<ImportedSongWithShowsResource>(detail.Song) with
        {
            Shows = detail.Shows.Select(ShowSummary).ToArray(),
        };

    private static T ProjectImportedSong<T>(CatalogImportedSong song)
        where T : ImportedSongResource, new() =>
        new()
        {
            Id = new(song.uuid),
            ArtistId = new(song.artist_uuid),
            Name = song.name,
            Slug = song.slug,
            UpstreamIdentifier = song.upstream_identifier,
            SetlistShowCount = song.setlist_show_count,
            CreatedAt = song.created_at,
            UpdatedAt = song.updated_at,
        };
}
