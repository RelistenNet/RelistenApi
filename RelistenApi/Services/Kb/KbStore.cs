using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace Relisten.Kb;

public static class AuthorTypes
{
    public const string Importer = "importer";
    public const string Algorithm = "algorithm";
    public const string Llm = "llm";
    public const string User = "user";
    public const string Moderator = "moderator";

    public static bool IsHuman(string authorType) => authorType is User or Moderator;
}

public enum KbWriteMode
{
    /// Insert, or update a row when a value differs. A machine skips rows a person set.
    Upsert,

    /// Insert only; an existing row is never changed (the LLM's mode, docs/llm.md §3).
    FillOnly,
}

public sealed record SongRecord(
    Guid Uuid,
    Guid ArtistUuid,
    string Name,
    string? Kind = null,
    string? OriginalArtist = null,
    float? Confidence = null
);

public sealed record AliasRecord(
    Guid ArtistUuid,
    string Alias,
    Guid SongUuid,
    float? Confidence = null
);

public sealed record EvidenceRecord(Guid SetlistSongUuid, Guid SongUuid, float? Confidence = null);

/// One part of a track's title. A track's parts are written together.
public sealed record TrackPartRecord(
    Guid TrackUuid,
    int Part,
    Guid? SongUuid,
    string? Text,
    bool SegueAfter = false,
    IReadOnlyList<string>? Qualifiers = null,
    float? Confidence = null
);

/// The one writer of the kb song catalogue (string-theory ddl/002_kb.sql). A store is a run:
/// it records the run and sets the transaction's author, which the kb triggers use to log
/// every change and to refuse a machine overwriting a person. Writes are batched and change a
/// row only when a value differs, so a re-run logs nothing; a machine skips rows a person set
/// rather than tripping the trigger.
public sealed class KbStore
{
    private readonly NpgsqlConnection connection;
    private readonly NpgsqlTransaction tx;

    private KbStore(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid run,
        string authorType,
        string authorRef
    )
    {
        this.connection = connection;
        this.tx = tx;
        Run = run;
        AuthorType = authorType;
        AuthorRef = authorRef;
    }

    public Guid Run { get; }
    public string AuthorType { get; }
    public string AuthorRef { get; }

    private bool Human => AuthorTypes.IsHuman(AuthorType);

    public static async Task<KbStore> BeginAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        string authorType,
        string authorRef,
        string? note = null
    )
    {
        var run = Guid.CreateVersion7();
        await connection.ExecuteAsync(
            """
            INSERT INTO kb.runs (uuid, author_type, author_ref, note) VALUES (@run, @authorType, @authorRef, @note);
            SELECT set_config('kb.author_type', @authorType, true),
                   set_config('kb.author_ref', @authorRef, true),
                   set_config('kb.run_uuid', @run::text, true);
            """,
            new
            {
                run,
                authorType,
                authorRef,
                note,
            },
            tx
        );
        return new KbStore(connection, tx, run, authorType, authorRef);
    }

    public Task FinishAsync() =>
        connection.ExecuteAsync(
            "UPDATE kb.runs SET finished_at = now() WHERE uuid = @Run",
            new { Run },
            tx
        );

    // The update clause a write may apply: only a changed value, never by a machine over a
    // person's row, never in fill-only mode.
    private string UpdateWhen(KbWriteMode mode, string table, string changed) =>
        mode == KbWriteMode.FillOnly ? "false"
        : Human ? $"({changed})"
        : $"({changed}) AND {table}.author_type NOT IN ('user', 'moderator')";

    /// Songs, with a slug unique per artist derived from the name.
    public async Task<int> WriteSongsAsync(
        IReadOnlyList<SongRecord> songs,
        KbWriteMode mode = KbWriteMode.Upsert
    )
    {
        if (songs.Count == 0)
        {
            return 0;
        }

        var slugs = await AssignSlugs(songs);
        var update =
            mode == KbWriteMode.FillOnly
                ? "NOTHING"
                : $"""
                    UPDATE SET name = EXCLUDED.name, slug = EXCLUDED.slug, kind = EXCLUDED.kind,
                        original_artist = EXCLUDED.original_artist, author_type = EXCLUDED.author_type,
                        author_ref = EXCLUDED.author_ref, confidence = EXCLUDED.confidence,
                        run_uuid = EXCLUDED.run_uuid, updated_at = now()
                    WHERE {UpdateWhen(
                        mode,
                        "kb.songs",
                        "(kb.songs.name, kb.songs.kind, kb.songs.original_artist) IS DISTINCT FROM (EXCLUDED.name, EXCLUDED.kind, EXCLUDED.original_artist)"
                    )}
                    """;

        return await connection.ExecuteAsync(
            $"""
            INSERT INTO kb.songs (uuid, artist_uuid, name, slug, kind, original_artist,
                                  author_type, author_ref, confidence, run_uuid)
            SELECT x.uuid, x.artist, x.name, x.slug, x.kind, x.original, @AuthorType, @AuthorRef, x.confidence, @Run
            FROM unnest(@uuids, @artists, @names, @slugs, @kinds, @originals, @confidences)
                 AS x(uuid, artist, name, slug, kind, original, confidence)
            ON CONFLICT (uuid) DO {update}
            """,
            new
            {
                AuthorType,
                AuthorRef,
                Run,
                uuids = songs.Select(s => s.Uuid).ToArray(),
                artists = songs.Select(s => s.ArtistUuid).ToArray(),
                names = songs.Select(s => s.Name).ToArray(),
                slugs = songs.Select(s => slugs[s.Uuid]).ToArray(),
                kinds = songs.Select(s => s.Kind).ToArray(),
                originals = songs.Select(s => s.OriginalArtist).ToArray(),
                confidences = songs.Select(s => s.Confidence).ToArray(),
            },
            tx
        );
    }

    /// A song keeps the slug it has unless its name's slug changes; collisions within an
    /// artist get -2, -3 in uuid order.
    private async Task<Dictionary<Guid, string>> AssignSlugs(IReadOnlyList<SongRecord> songs)
    {
        var taken = (
            await connection.QueryAsync<(Guid Artist, Guid Song, string Slug)>(
                "SELECT artist_uuid, uuid, slug FROM kb.songs WHERE artist_uuid = ANY(@artists)",
                new { artists = songs.Select(s => s.ArtistUuid).Distinct().ToArray() },
                tx
            )
        ).ToList();
        var mine = songs.Select(s => s.Uuid).ToHashSet();
        var used = taken
            .Where(t => !mine.Contains(t.Song))
            .Select(t => (t.Artist, t.Slug))
            .ToHashSet();

        var slugs = new Dictionary<Guid, string>();
        foreach (var song in songs.OrderBy(s => s.Uuid))
        {
            var slug = TitleNormalizer.Slug(song.Name);
            for (var n = 2; !used.Add((song.ArtistUuid, slug)); n++)
            {
                slug = $"{TitleNormalizer.Slug(song.Name)}-{n}";
            }

            slugs[song.Uuid] = slug;
        }

        return slugs;
    }

    public async Task<int> WriteAliasesAsync(
        IReadOnlyList<AliasRecord> aliases,
        KbWriteMode mode = KbWriteMode.Upsert
    )
    {
        var rows = aliases
            .Select(a => (a, Normalized: TitleNormalizer.Normalize(a.Alias)))
            .Where(a => a.Normalized.Length > 0)
            .DistinctBy(a => (a.a.ArtistUuid, a.Normalized))
            .ToList();
        if (rows.Count == 0)
        {
            return 0;
        }

        return await connection.ExecuteAsync(
            $"""
            INSERT INTO kb.song_aliases (artist_uuid, normalized, alias, song_uuid, author_type, author_ref, confidence, run_uuid)
            SELECT x.artist, x.normalized, x.alias, x.song, @AuthorType, @AuthorRef, x.confidence, @Run
            FROM unnest(@artists, @normalized, @aliases, @songs, @confidences) AS x(artist, normalized, alias, song, confidence)
            ON CONFLICT (artist_uuid, normalized) DO {(mode == KbWriteMode.FillOnly ? "NOTHING" : $"""
                UPDATE SET song_uuid = EXCLUDED.song_uuid, author_type = EXCLUDED.author_type,
                    author_ref = EXCLUDED.author_ref, confidence = EXCLUDED.confidence,
                    run_uuid = EXCLUDED.run_uuid, updated_at = now()
                WHERE {UpdateWhen(mode, "kb.song_aliases", "kb.song_aliases.song_uuid IS DISTINCT FROM EXCLUDED.song_uuid")}
                """)}
            """,
            new
            {
                AuthorType,
                AuthorRef,
                Run,
                artists = rows.Select(r => r.a.ArtistUuid).ToArray(),
                normalized = rows.Select(r => r.Normalized).ToArray(),
                aliases = rows.Select(r => r.a.Alias.Trim()).ToArray(),
                songs = rows.Select(r => r.a.SongUuid).ToArray(),
                confidences = rows.Select(r => r.a.Confidence).ToArray(),
            },
            tx
        );
    }

    public async Task<int> WriteEvidenceAsync(
        IReadOnlyList<EvidenceRecord> evidence,
        KbWriteMode mode = KbWriteMode.Upsert
    )
    {
        if (evidence.Count == 0)
        {
            return 0;
        }

        return await connection.ExecuteAsync(
            $"""
            INSERT INTO kb.song_evidence (setlist_song_uuid, song_uuid, author_type, author_ref, confidence, run_uuid)
            SELECT x.setlist_song, x.song, @AuthorType, @AuthorRef, x.confidence, @Run
            FROM unnest(@setlistSongs, @songs, @confidences) AS x(setlist_song, song, confidence)
            ON CONFLICT (setlist_song_uuid) DO {(mode == KbWriteMode.FillOnly ? "NOTHING" : $"""
                UPDATE SET song_uuid = EXCLUDED.song_uuid, author_type = EXCLUDED.author_type,
                    author_ref = EXCLUDED.author_ref, confidence = EXCLUDED.confidence,
                    run_uuid = EXCLUDED.run_uuid, updated_at = now()
                WHERE {UpdateWhen(mode, "kb.song_evidence", "kb.song_evidence.song_uuid IS DISTINCT FROM EXCLUDED.song_uuid")}
                """)}
            """,
            new
            {
                AuthorType,
                AuthorRef,
                Run,
                setlistSongs = evidence.Select(e => e.SetlistSongUuid).ToArray(),
                songs = evidence.Select(e => e.SongUuid).ToArray(),
                confidences = evidence.Select(e => e.Confidence).ToArray(),
            },
            tx
        );
    }

    /// Replaces each listed track's parts. A track a person set is left alone by machines;
    /// in fill-only mode only tracks with no parts yet are written.
    public async Task<int> WriteTrackSongsAsync(
        IReadOnlyList<TrackPartRecord> parts,
        KbWriteMode mode = KbWriteMode.Upsert
    )
    {
        if (parts.Count == 0)
        {
            return 0;
        }

        var tracks = parts.Select(p => p.TrackUuid).Distinct().ToArray();
        var locked = (
            await connection.QueryAsync<Guid>(
                mode == KbWriteMode.FillOnly
                        ? "SELECT DISTINCT track_uuid FROM kb.track_songs WHERE track_uuid = ANY(@tracks)"
                    : Human ? "SELECT NULL::uuid WHERE false"
                    : "SELECT DISTINCT track_uuid FROM kb.track_songs WHERE track_uuid = ANY(@tracks) AND author_type IN ('user', 'moderator')",
                new { tracks },
                tx
            )
        ).ToHashSet();
        var writable = parts.Where(p => !locked.Contains(p.TrackUuid)).ToList();
        if (writable.Count == 0)
        {
            return 0;
        }

        // Parts past a track's new last part go; the rest update only where they differ.
        var removed = await connection.ExecuteAsync(
            """
            DELETE FROM kb.track_songs t
            USING (SELECT track, max(part) AS last FROM unnest(@tracks, @partNumbers) AS x(track, part) GROUP BY track) n
            WHERE t.track_uuid = n.track AND t.part > n.last
            """,
            new
            {
                tracks = writable.Select(p => p.TrackUuid).ToArray(),
                partNumbers = writable.Select(p => p.Part).ToArray(),
            },
            tx
        );

        var written = await connection.ExecuteAsync(
            """
            INSERT INTO kb.track_songs (track_uuid, part, song_uuid, text, segue_after, qualifiers,
                                        author_type, author_ref, confidence, run_uuid)
            SELECT x.track, x.part, x.song, x.text, x.segue,
                   coalesce(string_to_array(nullif(x.qualifiers, ''), ','), '{}'),
                   @AuthorType, @AuthorRef, x.confidence, @Run
            FROM unnest(@tracks, @partNumbers, @songs, @texts, @segues, @qualifiers, @confidences)
                 AS x(track, part, song, text, segue, qualifiers, confidence)
            ON CONFLICT (track_uuid, part) DO UPDATE SET song_uuid = EXCLUDED.song_uuid, text = EXCLUDED.text,
                segue_after = EXCLUDED.segue_after, qualifiers = EXCLUDED.qualifiers,
                author_type = EXCLUDED.author_type, author_ref = EXCLUDED.author_ref,
                confidence = EXCLUDED.confidence, run_uuid = EXCLUDED.run_uuid, updated_at = now()
            WHERE (kb.track_songs.song_uuid, kb.track_songs.text, kb.track_songs.segue_after, kb.track_songs.qualifiers)
                  IS DISTINCT FROM (EXCLUDED.song_uuid, EXCLUDED.text, EXCLUDED.segue_after, EXCLUDED.qualifiers)
            """,
            new
            {
                AuthorType,
                AuthorRef,
                Run,
                tracks = writable.Select(p => p.TrackUuid).ToArray(),
                partNumbers = writable.Select(p => p.Part).ToArray(),
                songs = writable.Select(p => p.SongUuid).ToArray(),
                texts = writable.Select(p => p.Text).ToArray(),
                segues = writable.Select(p => p.SegueAfter).ToArray(),
                // Comma-joined: a text[][] parameter must be rectangular.
                qualifiers = writable.Select(p => string.Join(',', p.Qualifiers ?? [])).ToArray(),
                confidences = writable.Select(p => p.Confidence).ToArray(),
            },
            tx
        );
        return removed + written;
    }
}
