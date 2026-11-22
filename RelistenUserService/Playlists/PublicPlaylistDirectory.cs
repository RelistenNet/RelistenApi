using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Relisten.Accounts.Contracts.Playlists;
using Relisten.Identifiers;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Persistence;

namespace RelistenUserService.Playlists;

/// <summary>
/// Lists public playlists for anonymous discovery, optionally only those containing
/// an artist. Pages are keyset-paginated on (sort key, playlist ID), descending.
/// </summary>
/// <remarks>
/// Follower and play counts are computed over every public playlist on each request.
/// That is cheap while public playlists number in the thousands; responses are cached
/// at the edge for a minute. Counts can change between pages, so a playlist whose count
/// moves past the cursor may be skipped or repeated; the ID tiebreak keeps pages disjoint
/// for unchanged counts.
/// </remarks>
public sealed class PublicPlaylistDirectory(AccountsDbContext database, TimeProvider clock)
{
    internal const int DefaultLimit = 25;
    internal const int MaximumLimit = 50;
    private static readonly TimeSpan PlayWindow = TimeSpan.FromDays(30);

    // Integer keys for every sort, so one cursor shape and one keyset predicate serve all.
    private static readonly Dictionary<string, string> SortKeys = new(StringComparer.Ordinal)
    {
        ["recent"] = "(extract(epoch FROM updated_at) * 1000000)::bigint",
        ["followers"] = "follower_count::bigint",
        ["plays"] = "recent_play_count",
    };

    public async Task<PublicPlaylistPage> ListAsync(
        string sort,
        string? artist,
        string? cursor,
        int limit,
        CancellationToken cancellationToken
    )
    {
        var query = ParseQuery(sort, artist, cursor, limit);

        var rows = await database
            .Database.SqlQueryRaw<ListingRow>(BuildSql(query), Parameters(query))
            .ToListAsync(cancellationToken);

        var page = rows.Take(query.Limit).ToArray();
        var nextCursor =
            rows.Count > query.Limit
                ? EncodeCursor(new(query.Sort, page[^1].SortKey, page[^1].Id))
                : null;
        return new(1, page.Select(ToListing).ToArray(), nextCursor);
    }

    private static ListingQuery ParseQuery(string sort, string? artist, string? cursor, int limit)
    {
        if (!SortKeys.ContainsKey(sort))
        {
            throw InvalidQuery("sort must be recent, followers or plays.");
        }

        if (limit is < 1 or > MaximumLimit)
        {
            throw InvalidQuery($"limit must be between 1 and {MaximumLimit}.");
        }

        Guid? artistUuid = null;
        if (artist is not null)
        {
            artistUuid = ParseArtist(artist) ?? throw InvalidQuery("artist must be an artist ID.");
        }

        Cursor? after = null;
        if (cursor is not null)
        {
            after = DecodeCursor(cursor);
            // A cursor's key means something only under the sort that produced it.
            if (after is null || after.Sort != sort)
            {
                throw InvalidQuery("cursor is invalid for this sort.");
            }
        }

        return new(sort, artistUuid, after, limit);
    }

    /// <summary>Accepts the v4 typed artist ID or the catalog's bare artist UUID.</summary>
    private static Guid? ParseArtist(string value)
    {
        if (ArtistId.TryParse(value, out var typed))
        {
            return typed.Value;
        }

        return Guid.TryParseExact(value, "D", out var uuid) ? uuid : null;
    }

    private static string BuildSql(ListingQuery query)
    {
        // The visibility predicate mirrors PlaylistVisibility.Public.
        var artistFilter = query.ArtistUuid is null
            ? ""
            : """
                    AND EXISTS (
                        SELECT FROM user_data.playlist_artists AS artist
                        WHERE artist.playlist_id = playlist.id AND artist.artist_uuid = @artist
                    )
                """;
        var keyset = query.After is null ? "" : "WHERE (sort_key, id) < (@after_key, @after_id)";

        return $"""
            WITH listed AS (
                SELECT playlist.id, playlist.public_code, playlist.name, playlist.owner_id,
                       owner.username, playlist.track_count, playlist.catalog_duration_seconds,
                       playlist.updated_at,
                       (
                           SELECT count(*)::integer FROM user_data.playlist_follows AS follow
                           WHERE follow.playlist_id = playlist.id
                       ) AS follower_count,
                       (
                           SELECT coalesce(sum(bucket.plays), 0)::bigint
                           FROM public.source_track_plays_playlist_hourly AS bucket
                           WHERE bucket.playlist_id = playlist.id AND bucket.play_hour >= @plays_since
                       ) AS recent_play_count
                FROM user_data.playlists AS playlist
                JOIN identity.users AS owner ON owner.id = playlist.owner_id
                WHERE playlist.published AND NOT playlist.archived AND NOT playlist.taken_down
                  AND owner.status = @active_status
                  {artistFilter}
            ), keyed AS (
                SELECT *, {SortKeys[query.Sort]} AS sort_key FROM listed
            )
            SELECT id AS "Id", public_code AS "PublicCode", name AS "Name",
                   owner_id AS "OwnerId", username AS "Username", track_count AS "TrackCount",
                   catalog_duration_seconds AS "DurationSeconds", updated_at AS "UpdatedAt",
                   follower_count AS "FollowerCount", recent_play_count AS "RecentPlayCount",
                   sort_key AS "SortKey"
            FROM keyed
            {keyset}
            ORDER BY sort_key DESC, id DESC
            LIMIT @take
            """;
    }

    private object[] Parameters(ListingQuery query)
    {
        var parameters = new List<NpgsqlParameter>
        {
            new("plays_since", NpgsqlDbType.TimestampTz) { Value = clock.GetUtcNow() - PlayWindow },
            new("active_status", NpgsqlDbType.Text) { Value = UserStatuses.Active },
            // One extra row tells whether another page follows.
            new("take", NpgsqlDbType.Integer) { Value = query.Limit + 1 },
        };
        if (query.ArtistUuid is { } artist)
        {
            parameters.Add(new("artist", NpgsqlDbType.Uuid) { Value = artist });
        }
        if (query.After is { } after)
        {
            parameters.Add(new("after_key", NpgsqlDbType.Bigint) { Value = after.Key });
            parameters.Add(new("after_id", NpgsqlDbType.Text) { Value = after.Id });
        }
        return parameters.ToArray<object>();
    }

    private static PublicPlaylistListing ToListing(ListingRow row) =>
        new(
            PlaylistId.Parse(row.Id),
            row.PublicCode,
            row.Name,
            new(UserId.Parse(row.OwnerId), row.Username),
            row.TrackCount,
            row.DurationSeconds,
            row.UpdatedAt,
            row.FollowerCount,
            row.RecentPlayCount
        );

    // Cursors carry only public listing values, so they are encoded rather than protected.
    private static string EncodeCursor(Cursor cursor) =>
        WebEncoders.Base64UrlEncode(
            Encoding.UTF8.GetBytes(
                $"{cursor.Sort}\n{cursor.Key.ToString(CultureInfo.InvariantCulture)}\n{cursor.Id}"
            )
        );

    private static Cursor? DecodeCursor(string value)
    {
        try
        {
            var fields = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(value)).Split('\n');
            return
                fields.Length == 3
                && long.TryParse(
                    fields[1],
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var key
                )
                && PlaylistId.TryParse(fields[2], out var id)
                ? new(fields[0], key, id.Value)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static PlaylistException InvalidQuery(string detail) =>
        new(422, "invalid_query", detail);

    private sealed record ListingQuery(string Sort, Guid? ArtistUuid, Cursor? After, int Limit);

    private sealed record Cursor(string Sort, long Key, string Id);

    private sealed class ListingRow
    {
        public string Id { get; init; } = "";
        public string PublicCode { get; init; } = "";
        public string Name { get; init; } = "";
        public string OwnerId { get; init; } = "";
        public string Username { get; init; } = "";
        public int TrackCount { get; init; }
        public int DurationSeconds { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public int FollowerCount { get; init; }
        public long RecentPlayCount { get; init; }
        public long SortKey { get; init; }
    }
}
