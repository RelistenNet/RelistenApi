using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Catalog;

/// <summary>
/// Song navigation in one read snapshot. Imported setlist membership and exact
/// recording matches remain separate; neither read writes normalization data.
/// </summary>
public sealed class CatalogSongReader(
    DbService db,
    CatalogSongMetadataReader songMetadata,
    CatalogSongMatchReader matches,
    CatalogTrackNormalizationReader trackNormalization,
    CatalogShowMetadataReader showMetadata
)
{
    public async Task<IReadOnlyList<CatalogArtistSongEntry>?> ReadArtistSongs(
        Guid artistUuid,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var artist = await ReadArtist(session, artistUuid, cancellationToken);
        if (artist is null)
        {
            return null;
        }

        var imported = await ReadImportedSongs(session, artist.id, cancellationToken);
        if (!artist.approved)
        {
            return imported.Select(song => new CatalogArtistSongEntry(null, song)).ToArray();
        }

        var songs = await ReadSongDetails(session, artistUuid, null, cancellationToken);
        var mapped = await ReadMappedImportedSongs(
            session,
            artistUuid,
            artist.id,
            cancellationToken
        );
        return songs
            .Select(song => new CatalogArtistSongEntry(song, null))
            .Concat(
                imported
                    .Where(song => !mapped.Contains(song.uuid))
                    .Select(song => new CatalogArtistSongEntry(null, song))
            )
            .OrderBy(entry => entry.Song?.name ?? entry.ImportedSong!.name, StringComparer.Ordinal)
            .ThenBy(
                entry => entry.Song?.id ?? entry.ImportedSong!.uuid.ToString(),
                StringComparer.Ordinal
            )
            .ToArray();
    }

    public async Task<CatalogSongDetail?> ReadSong(
        string songId,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var song = await ReadApprovedSong(session, songId, cancellationToken);
        if (song is null)
        {
            return null;
        }
        return (
            await ReadSongDetails(session, song.artist_uuid, [song.id], cancellationToken)
        ).Single();
    }

    public async Task<IReadOnlyList<CatalogSongTrackMatch>?> ReadTracks(
        string songId,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var song = await ReadApprovedSong(session, songId, cancellationToken);
        if (song is null)
        {
            return null;
        }
        var membership = await matches.ReadMatches(
            session,
            song.artist_uuid,
            song.id,
            cancellationToken
        );
        var tracks = (
            await session.Connection.QueryAsync<CatalogTrack>(
                session.Command(
                    CatalogTrackNormalizationReader.TrackSelect
                        + " WHERE t.uuid=ANY(@uuids) AND NOT t.is_orphaned",
                    new { uuids = membership.Select(row => row.track_uuid).ToArray() },
                    cancellationToken
                )
            )
        ).ToArray();
        await trackNormalization.Apply(session, tracks, true, cancellationToken);
        var byUuid = tracks.ToDictionary(track => track.uuid);
        return membership
            .Select(row => new CatalogSongTrackMatch(
                byUuid[row.track_uuid],
                row.matching_part_ordinals
            ))
            .ToArray();
    }

    public async Task<IReadOnlyList<CatalogShow>?> ReadShows(
        string songId,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var song = await ReadApprovedSong(session, songId, cancellationToken);
        if (song is null)
        {
            return null;
        }
        var membership = await matches.ReadMatches(
            session,
            song.artist_uuid,
            song.id,
            cancellationToken
        );
        return await ReadShows(
            session,
            CatalogShowMetadataReader.ShowSelect + " WHERE s.uuid=ANY(@uuids)",
            new { uuids = membership.Select(row => row.show_uuid).Distinct().ToArray() },
            cancellationToken
        );
    }

    public async Task<IReadOnlyList<CatalogImportedSong>?> ReadImportedSongs(
        Guid artistUuid,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var artist = await ReadArtist(session, artistUuid, cancellationToken);
        return artist is null
            ? null
            : await ReadImportedSongs(session, artist.id, cancellationToken);
    }

    public async Task<CatalogImportedSongDetail?> ReadImportedSong(
        Guid artistUuid,
        Guid songUuid,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var song = await session.Connection.QuerySingleOrDefaultAsync<CatalogImportedSong>(
            session.Command(
                ImportedSongSelect + " WHERE a.uuid=@artistUuid AND s.uuid=@songUuid",
                new { artistUuid, songUuid },
                cancellationToken
            )
        );
        if (song is null)
        {
            return null;
        }
        var shows = await ReadShows(
            session,
            CatalogShowMetadataReader.ShowSelect
                + """
                 WHERE a.uuid=@artistUuid AND EXISTS (
                     SELECT 1 FROM setlist_songs_plays p
                     JOIN setlist_shows ss ON ss.id=p.played_setlist_show_id
                     WHERE p.played_setlist_song_id=@songId AND ss.artist_id=s.artist_id AND ss.date=s.date
                 )
                """,
            new { artistUuid, songId = song.id },
            cancellationToken
        );
        return new(song, shows);
    }

    private async Task<HashSet<Guid>> ReadMappedImportedSongs(
        CatalogReadSession session,
        Guid artistUuid,
        long artistId,
        CancellationToken cancellationToken
    )
    {
        var evidence = (
            await session.Connection.QueryAsync<ImportedSongEvidence>(
                session.Command(
                    """
                    SELECT e.setlist_song_uuid,e.song_id FROM norm.song_evidence e
                    JOIN setlist_songs s ON s.uuid=e.setlist_song_uuid
                    WHERE s.artist_id=@artistId
                    """,
                    new { artistId },
                    cancellationToken
                )
            )
        ).ToArray();
        var resolved = await songMetadata.Read(
            session,
            evidence.Select(row => row.song_id).ToArray(),
            cancellationToken
        );
        // A title is not a mapping. Preserve raw access when evidence is missing,
        // broken, or points outside the artist's canonical catalog.
        return evidence
            .Where(row =>
                resolved.TryGetValue(row.song_id, out var song) && song.artist_uuid == artistUuid
            )
            .Select(row => row.setlist_song_uuid)
            .ToHashSet();
    }

    private static Task<SongArtist?> ReadArtist(
        CatalogReadSession session,
        Guid artistUuid,
        CancellationToken cancellationToken
    ) =>
        session.Connection.QuerySingleOrDefaultAsync<SongArtist>(
            session.Command(
                "SELECT a.id,COALESCE(f.norm_catalogue,false) approved FROM artists a LEFT JOIN features f ON f.artist_id=a.id WHERE a.uuid=@artistUuid",
                new { artistUuid },
                cancellationToken
            )
        );

    private async Task<CatalogSong?> ReadApprovedSong(
        CatalogReadSession session,
        string songId,
        CancellationToken cancellationToken
    )
    {
        var owner = await session.Connection.QuerySingleOrDefaultAsync<Guid?>(
            session.Command(
                """
                SELECT a.uuid FROM norm.songs s JOIN artists a ON a.uuid=s.artist_uuid
                JOIN features f ON f.artist_id=a.id AND f.norm_catalogue WHERE s.id=@songId
                """,
                new { songId },
                cancellationToken
            )
        );
        if (owner is null)
        {
            return null;
        }
        var resolved = await songMetadata.Read(session, [songId], cancellationToken);
        if (!resolved.TryGetValue(songId, out var song) || song.artist_uuid != owner)
        {
            throw new CatalogNormalizationUnavailableException(
                "The song's canonical identity cannot be resolved."
            );
        }
        return song;
    }

    private async Task<IReadOnlyList<CatalogSongDetail>> ReadSongDetails(
        CatalogReadSession session,
        Guid artistUuid,
        string[]? ids,
        CancellationToken cancellationToken
    )
    {
        var songs = (
            await session.Connection.QueryAsync<CatalogSongDetail>(
                session.Command(
                    """
                    SELECT id,artist_uuid,name,kind,slug,original_artist FROM norm.songs
                    WHERE artist_uuid=@artistUuid AND merged_into IS NULL AND (@ids::text[] IS NULL OR id=ANY(@ids))
                    ORDER BY name COLLATE "C",id
                    """,
                    new { artistUuid, ids },
                    cancellationToken
                )
            )
        ).ToArray();
        var counts = await matches.ReadCounts(session, artistUuid, ids, cancellationToken);
        var aliases = await ReadAliases(
            session,
            artistUuid,
            songs.Select(song => song.id).ToArray(),
            cancellationToken
        );
        foreach (var song in songs)
        {
            if (counts.TryGetValue(song.id, out var count))
            {
                song.matched_track_count = count.matched_track_count;
                song.matched_show_count = count.matched_show_count;
            }
            song.Aliases = aliases[song.id]
                .Select(alias => alias.alias)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(alias => alias, StringComparer.Ordinal)
                .ToArray();
        }
        return songs;
    }

    private static async Task<ILookup<string, SongAlias>> ReadAliases(
        CatalogReadSession session,
        Guid artistUuid,
        string[] songIds,
        CancellationToken cancellationToken
    )
    {
        return (
            await session.Connection.QueryAsync<SongAlias>(
                session.Command(
                    """
                    WITH RECURSIVE lineage AS (
                        SELECT id canonical_id,id,name,1 depth FROM norm.songs
                        WHERE artist_uuid=@artistUuid AND id=ANY(@songIds)
                        UNION ALL
                        SELECT l.canonical_id,s.id,s.name,l.depth+1 FROM lineage l
                        JOIN norm.songs s ON s.merged_into=l.id AND s.artist_uuid=@artistUuid
                        WHERE l.depth<32
                    )
                    SELECT l.canonical_id,a.alias FROM lineage l
                    JOIN norm.song_aliases a ON a.song_id=l.id AND a.artist_uuid=@artistUuid
                    UNION
                    SELECT canonical_id,name alias FROM lineage WHERE id<>canonical_id
                    """,
                    new { artistUuid, songIds },
                    cancellationToken
                )
            )
        ).ToLookup(alias => alias.canonical_id);
    }

    private static async Task<IReadOnlyList<CatalogImportedSong>> ReadImportedSongs(
        CatalogReadSession session,
        long artistId,
        CancellationToken cancellationToken
    ) =>
        (
            await session.Connection.QueryAsync<CatalogImportedSong>(
                session.Command(
                    ImportedSongSelect
                        + " WHERE s.artist_id=@artistId ORDER BY s.name COLLATE \"C\",s.uuid",
                    new { artistId },
                    cancellationToken
                )
            )
        ).ToArray();

    private async Task<IReadOnlyList<CatalogShow>> ReadShows(
        CatalogReadSession session,
        string sql,
        object parameters,
        CancellationToken cancellationToken
    )
    {
        var shows = (
            await session.Connection.QueryAsync<CatalogShow>(
                session.Command(sql + " ORDER BY s.date,s.uuid", parameters, cancellationToken)
            )
        ).ToArray();
        await showMetadata.Apply(session, shows, cancellationToken);
        return shows;
    }

    // Date-based imported evidence does not identify a recording or playable file.
    // Count distinct catalog show IDs: multiple imported setlists can describe one date.
    private const string ImportedSongSelect = """
        SELECT s.*,a.uuid artist_uuid,counts.setlist_show_count
        FROM setlist_songs s JOIN artists a ON a.id=s.artist_id
        CROSS JOIN LATERAL (
            SELECT count(DISTINCT sh.id)::int setlist_show_count FROM setlist_songs_plays p
            JOIN setlist_shows ss ON ss.id=p.played_setlist_show_id AND ss.artist_id=s.artist_id
            JOIN shows sh ON sh.artist_id=s.artist_id AND sh.date=ss.date
            WHERE p.played_setlist_song_id=s.id
        ) counts
        """;

    private sealed class SongArtist
    {
        public long id { get; set; }
        public bool approved { get; set; }
    }

    private sealed class ImportedSongEvidence
    {
        public Guid setlist_song_uuid { get; set; }
        public string song_id { get; set; } = null!;
    }

    private sealed class SongAlias
    {
        public string canonical_id { get; set; } = null!;
        public string alias { get; set; } = null!;
    }
}
