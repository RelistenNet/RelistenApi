using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Identifiers;

namespace Relisten.Catalog;

public sealed record CatalogReviewQuery(
    string? Text,
    string? SongId,
    Guid? ShowUuid,
    int[]? Years,
    int Limit
);

public sealed class CatalogReviewSearch(DbService db, CatalogSongMetadataReader songs)
{
    public async Task<CatalogReviewHit[]?> Read(
        Guid artistUuid,
        CatalogReviewQuery query,
        CancellationToken cancellationToken
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        // Apply the budget to every statement in the search, including alias resolution.
        await session.Connection.ExecuteAsync(
            session.Command("SET LOCAL statement_timeout = '2s'", null, cancellationToken)
        );

        var artist = await session.Connection.QuerySingleOrDefaultAsync<ReviewArtist>(
            session.Command(
                """
                SELECT a.id, coalesce(f.norm_catalogue,false) AS approved
                FROM artists a LEFT JOIN features f ON f.artist_id=a.id WHERE a.uuid=@artistUuid
                """,
                new { artistUuid },
                cancellationToken
            )
        );
        if (artist is null)
        {
            return null;
        }

        // The only expression ever run is one built from a song's aliases; callers
        // never supply their own, so no one can run arbitrary regular expressions
        // against the review corpus.
        string? pattern = null;
        if (query.SongId is { } songId)
        {
            // Song identities and aliases exist only in an approved normalized
            // catalog. Text search reads the imported reviews directly, so it stays
            // available for every artist.
            if (!artist.Approved)
            {
                return [];
            }
            pattern = await ReadSongPattern(session, artistUuid, songId, cancellationToken);
            if (pattern is null)
            {
                return [];
            }
        }

        var rows = await session.Connection.QueryAsync<ReviewRow>(
            session.Command(
                SearchSql(query.Text is not null, pattern is not null),
                new
                {
                    Artist = artist.Id,
                    query.Text,
                    Literal = query.Text is { } text ? LiteralPattern(text) : null,
                    pattern,
                    query.ShowUuid,
                    query.Years,
                    query.Limit,
                },
                cancellationToken
            )
        );
        return rows.Select(row => new CatalogReviewHit(
                new(row.Uuid),
                new(row.ShowUuid),
                new(row.SourceUuid),
                row.Rating,
                row.Excerpt,
                CatalogWebLinks.Source(row.ArtistSlug, row.DisplayDate, row.SourceUuid),
                row.TrackUuids.Select(id => new TrackId(id)).ToArray()
            ))
            .ToArray();
    }

    private async Task<string?> ReadSongPattern(
        CatalogReadSession session,
        Guid artistUuid,
        string songId,
        CancellationToken cancellationToken
    )
    {
        var matches = await songs.Read(session, [songId], cancellationToken);
        if (!matches.TryGetValue(songId, out var song) || song.artist_uuid != artistUuid)
        {
            return null;
        }

        var aliases = await session.Connection.QueryAsync<string>(
            session.Command(
                """
                WITH RECURSIVE variants AS (
                  SELECT id, 1 AS depth FROM norm.songs WHERE id=@id AND artist_uuid=@artistUuid
                  UNION ALL
                  SELECT s.id, v.depth+1 FROM norm.songs s JOIN variants v ON s.merged_into=v.id
                  WHERE s.artist_uuid=@artistUuid AND v.depth<32
                )
                SELECT DISTINCT alias FROM norm.song_aliases a JOIN variants v ON v.id=a.song_id
                WHERE a.artist_uuid=@artistUuid AND alias <> '' ORDER BY alias
                """,
                new { id = song.id, artistUuid },
                cancellationToken
            )
        );

        return string.Join(
            "|",
            aliases
                .Append(song.name)
                .Where(alias => alias.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(BoundedAlias)
        );
    }

    // Alias text is literal. PostgreSQL's word-boundary operators prevent a song
    // such as "Play" from matching every use of "playing" in a review. \m and \M
    // only match next to a word character, so an edge such as the apostrophe in
    // "Truckin'" or the "!" in "Slipknot!" takes no boundary on that side.
    // Regex.Escape output is also literal in a PostgreSQL ARE: it prefixes
    // metacharacters and spaces with a backslash, which makes any non-alphanumeric
    // character literal there, and writes tab, newline, and similar characters as
    // \t and \n, which ARE reads as the same characters.
    private static string BoundedAlias(string alias) =>
        (IsWordCharacter(alias[0]) ? @"\m" : "")
        + Regex.Escape(alias)
        + (IsWordCharacter(alias[^1]) ? @"\M" : "");

    // PostgreSQL's word characters for \m and \M: alphanumerics and underscore.
    private static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';

    // Must match the expression indexed by migration 22.
    private const string ReviewBody = "coalesce(r.title || ' — ', '') || r.review";

    // An excerpt starts this many characters before the match and is this long.
    private const int ExcerptLead = 80;
    private const int ExcerptLength = 300;

    private static string LiteralPattern(string text) =>
        "%" + text.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "%";

    private static string SearchSql(bool hasText, bool hasPattern)
    {
        var filter =
            hasPattern ? $"AND ({ReviewBody}) ~* @pattern"
            : hasText ? $"AND ({ReviewBody}) ILIKE @literal ESCAPE '!'"
            : "";

        // Select candidates with the indexable predicate, then find the precise
        // excerpt offset only for the returned page.
        return $"""
            WITH matching AS (
                SELECT r.uuid, r.rating, r.source_id, sh.uuid AS show_uuid, sh.display_date,
                       {ReviewBody} AS body
                FROM shows sh JOIN sources s ON s.show_id=sh.id
                JOIN source_reviews r ON r.source_id=s.id
                WHERE sh.artist_id=@artist AND (@showUuid::uuid IS NULL OR sh.uuid=@showUuid)
                  AND (@years::int[] IS NULL OR extract(year from sh.date)::int=ANY(@years))
                  {filter}
            ), page AS MATERIALIZED (
                SELECT * FROM matching
                ORDER BY rating DESC NULLS LAST, length(body) DESC, uuid LIMIT @limit
            ), excerpts AS (
                SELECT p.*,
                       CASE WHEN @pattern::text IS NOT NULL THEN regexp_instr(body,@pattern,1,1,0,'i')
                            WHEN @text::text IS NOT NULL THEN strpos(lower(body),lower(@text))
                            ELSE 1 END AS match_at
                FROM page p
            )
            SELECT p.uuid, p.show_uuid AS ShowUuid, s.uuid AS SourceUuid, a.slug AS ArtistSlug,
                   p.display_date AS DisplayDate, p.rating,
                   (CASE WHEN p.match_at>{ExcerptLead + 1} THEN '…' ELSE '' END) ||
                     substring(body,greatest(1,p.match_at-{ExcerptLead}),{ExcerptLength}) ||
                     (CASE WHEN length(body)>=greatest(1,p.match_at-{ExcerptLead})+{ExcerptLength} THEN '…' ELSE '' END) AS Excerpt,
                   ARRAY(SELECT t.uuid FROM source_tracks t JOIN source_sets sets ON sets.id=t.source_set_id
                         WHERE t.source_id=s.id AND NOT t.is_orphaned AND (t.mp3_url IS NOT NULL OR t.flac_url IS NOT NULL)
                         ORDER BY sets.index,t.track_position,t.uuid LIMIT 100) AS TrackUuids
            FROM excerpts p JOIN sources s ON s.id=p.source_id JOIN artists a ON a.id=s.artist_id
            ORDER BY p.rating DESC NULLS LAST, length(p.body) DESC, p.uuid
            """;
    }

    private sealed record ReviewArtist(int Id, bool Approved);

    private sealed class ReviewRow
    {
        public Guid Uuid { get; init; }
        public Guid ShowUuid { get; init; }
        public Guid SourceUuid { get; init; }
        public string ArtistSlug { get; init; } = null!;
        public string DisplayDate { get; init; } = null!;
        public short? Rating { get; init; }
        public string Excerpt { get; init; } = null!;
        public Guid[] TrackUuids { get; init; } = [];
    }
}

internal static class CatalogWebLinks
{
    public static string Source(string artistSlug, string displayDate, Guid sourceUuid) =>
        $"https://relisten.net/{Uri.EscapeDataString(artistSlug)}/"
        + string.Join("/", displayDate.Split('-').Select(Uri.EscapeDataString))
        + $"?source={sourceUuid:D}";
}

[Newtonsoft.Json.JsonObject(
    NamingStrategyType = typeof(Newtonsoft.Json.Serialization.SnakeCaseNamingStrategy)
)]
public sealed record CatalogReviewHit(
    SourceReviewId Id,
    ShowId ShowId,
    SourceId SourceId,
    short? Rating,
    string Excerpt,
    string Link,
    IReadOnlyList<TrackId> TrackIds
);
