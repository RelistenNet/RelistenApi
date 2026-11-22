using System.Data;
using Dapper;
using Npgsql;
using Relisten;
using Relisten.Identifiers;

namespace RelistenIngestionWorker.PlaylistSearch;

internal sealed class PlaylistSearchRows(string accountsConnectionString, DbService catalog)
{
    internal const int PageSize = 256;

    public async Task<PlaylistSearchDocument[]> Read(
        string? playlistId,
        string after,
        CancellationToken cancellationToken
    )
    {
        var (headers, entries, segments) = await ReadAccounts(playlistId, after, cancellationToken);
        var trackIds = entries
            .Select(entry => entry.TrackId)
            .Distinct()
            .Select(id => TrackId.TryParse(id, out var track) ? track.Value : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToArray();
        var tracks = await ReadCatalog(trackIds, cancellationToken);
        var entriesByPlaylist = entries.ToLookup(entry => entry.PlaylistId);
        var guideByPlaylist = segments.ToLookup(segment => segment.PlaylistId);

        return headers
            .Select(header =>
            {
                var playlistEntries = entriesByPlaylist[header.Id];
                var metadata = playlistEntries
                    .SelectMany(entry => tracks[entry.TrackId ?? ""])
                    .ToArray();
                return new PlaylistSearchDocument(
                    header.Id,
                    header.OwnerId,
                    header.Name,
                    header.Description ?? "",
                    Text(playlistEntries.Select(entry => entry.Description)),
                    Text(guideByPlaylist[header.Id].Select(segment => segment.Description)),
                    Text(metadata.Select(track => track.Artist)),
                    metadata.Select(track => track.ArtistId).Distinct().ToArray(),
                    Text(metadata.Select(track => track.Song)),
                    header.Published && !header.Archived && !header.TakenDown
                        ? header.Username
                        : "",
                    header.Published,
                    header.Archived,
                    header.TakenDown,
                    header.HistoryGeneration,
                    header.Revision,
                    header.FollowCount,
                    header.UpdatedAt
                );
            })
            .ToArray();
    }

    public async Task<HashSet<string>> ExistingOwners(
        string[] ids,
        CancellationToken cancellationToken
    )
    {
        await using var db = new NpgsqlConnection(accountsConnectionString);
        var existing = await db.QueryAsync<string>(
            new CommandDefinition(
                "SELECT id FROM identity.users WHERE id = ANY(@ids)",
                new { ids },
                cancellationToken: cancellationToken
            )
        );
        return existing.ToHashSet(StringComparer.Ordinal);
    }

    private async Task<(Header[] Headers, Entry[] Entries, Segment[] Segments)> ReadAccounts(
        string? playlistId,
        string after,
        CancellationToken cancellationToken
    )
    {
        await using var db = new NpgsqlConnection(accountsConnectionString);
        await db.OpenAsync(cancellationToken);
        await using var snapshot = await db.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken
        );
        var headers = (
            await db.QueryAsync<Header>(
                new CommandDefinition(
                    """
                    SELECT p.id, p.owner_id AS OwnerId, p.name, p.description, p.published, p.archived,
                           p.taken_down AS TakenDown, p.revision, (extract(epoch FROM p.updated_at) * 1000)::bigint AS UpdatedAt,
                           u.username,
                           g.value AS HistoryGeneration,
                           (SELECT count(*) FROM user_data.playlist_follows f WHERE f.playlist_id = p.id) AS FollowCount
                    FROM user_data.playlists p
                    JOIN identity.users u ON u.id = p.owner_id
                    CROSS JOIN user_data.history_generation g
                    WHERE p.id > @after AND (@playlistId::text IS NULL OR p.id = @playlistId)
                    ORDER BY p.id LIMIT @limit
                    """,
                    new
                    {
                        after,
                        playlistId,
                        limit = PageSize,
                    },
                    snapshot,
                    cancellationToken: cancellationToken
                )
            )
        ).ToArray();
        var page = headers.Select(row => row.Id).ToArray();
        if (page.Length == 0)
        {
            return (headers, [], []);
        }

        // A note or track in a removed guide segment must not affect matching.
        using var content = await db.QueryMultipleAsync(
            new CommandDefinition(
                """
                SELECT e.playlist_id AS PlaylistId, e.track_id AS TrackId, e.description
                FROM user_data.playlist_entries e
                JOIN user_data.playlist_segments s ON s.id = e.segment_id AND s.playlist_id = e.playlist_id
                WHERE e.playlist_id = ANY(@page) AND NOT e.deleted AND NOT s.deleted;
                SELECT playlist_id AS PlaylistId, description FROM user_data.playlist_segments
                WHERE playlist_id = ANY(@page) AND NOT deleted;
                """,
                new { page },
                snapshot,
                cancellationToken: cancellationToken
            )
        );
        return (
            headers,
            (await content.ReadAsync<Entry>()).ToArray(),
            (await content.ReadAsync<Segment>()).ToArray()
        );
    }

    private async Task<ILookup<string, TrackMetadata>> ReadCatalog(
        Guid[] ids,
        CancellationToken cancellationToken
    )
    {
        if (ids.Length == 0)
        {
            return Array.Empty<TrackMetadata>().ToLookup(track => track.Artist);
        }

        await using var db = catalog.CreateConnection(longTimeout: true, readOnly: false);
        var tracks = await db.QueryAsync<TrackMetadata>(
            new CommandDefinition(
                """
                SELECT t.uuid, a.uuid AS ArtistUuid, a.name AS Artist, COALESCE(s.name, t.title, '') AS Song
                FROM source_tracks t JOIN artists a ON a.id = t.artist_id
                LEFT JOIN norm.track_songs m ON m.track_uuid = t.uuid
                LEFT JOIN norm.songs s ON s.id = m.song_id
                WHERE t.uuid = ANY(@ids)
                """,
                new { ids },
                cancellationToken: cancellationToken
            )
        );
        return tracks.ToLookup(track => new TrackId(track.Uuid).ToString());
    }

    private static string[] Text(IEnumerable<string?> parts) =>
        parts
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!)
            .Distinct()
            .ToArray();

    private sealed record Header(
        string Id,
        string OwnerId,
        string Name,
        string? Description,
        bool Published,
        bool Archived,
        bool TakenDown,
        long Revision,
        long UpdatedAt,
        string Username,
        string HistoryGeneration,
        long FollowCount
    );

    private sealed record Entry(string PlaylistId, string? TrackId, string? Description);

    private sealed record Segment(string PlaylistId, string? Description);

    private sealed record TrackMetadata(Guid Uuid, Guid ArtistUuid, string Artist, string Song)
    {
        public string ArtistId => new ArtistId(ArtistUuid).ToString();
    }
}

internal sealed record PlaylistSearchDocument(
    string Id,
    string OwnerId,
    string Name,
    string Description,
    string[] EntryNotes,
    string[] GuideText,
    string[] Artists,
    string[] ArtistIds,
    string[] Songs,
    string Curator,
    bool Published,
    bool Archived,
    bool TakenDown,
    string HistoryGeneration,
    long Revision,
    long FollowCount,
    long UpdatedAt
);
