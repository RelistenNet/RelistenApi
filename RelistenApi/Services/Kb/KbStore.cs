using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Kb.Titles;

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
    float? Confidence = null,
    bool Resolved = true
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

    /// Forgets a machine's decisions about these spellings for one artist, so they can be
    /// asked again: machine-written aliases with those keys, every track whose parts used them
    /// (a track's parts go together, so the whole track becomes unresolved), and the upstream
    /// setlist songs of those names. People's rows are kept, here and by the guard trigger.
    public async Task<(int Aliases, int Tracks)> ForgetMachineDecisionsAsync(
        Guid artistUuid,
        IReadOnlyCollection<string> spellings
    )
    {
        var normalized = spellings
            .Select(TitleNormalizer.Normalize)
            .Where(k => k.Length > 0)
            .Distinct()
            .ToArray();
        if (normalized.Length == 0)
        {
            return (0, 0);
        }

        // A numbered spelling ("03 tweezer") was decided through its song's alias, as the
        // matcher folds it: forget by the folded key too, and match parts the same way.
        var aliasKeys = (
            await connection.QueryAsync<string>(
                "SELECT normalized FROM kb.song_aliases WHERE artist_uuid = @artistUuid",
                new { artistUuid },
                tx
            )
        ).ToHashSet();
        string Fold(string key) => TrackNumberFold.Fold(key, aliasKeys.Contains);
        var keys = normalized.Concat(normalized.Select(Fold)).Distinct().ToArray();

        var aliases = await connection.ExecuteAsync(
            """
            DELETE FROM kb.song_aliases
            WHERE artist_uuid = @artistUuid AND normalized = ANY(@keys)
              AND author_type NOT IN ('user', 'moderator')
            """,
            new { artistUuid, keys },
            tx
        );

        // Track parts keep their text as titled; compare it normalised, in code, like lookups.
        var parts = await connection.QueryAsync<(Guid Track, string? Text, string AuthorType)>(
            """
            SELECT ts.track_uuid, ts.text, ts.author_type
            FROM kb.track_songs ts JOIN source_tracks t ON t.uuid = ts.track_uuid
            JOIN artists a ON a.id = t.artist_id
            WHERE a.uuid = @artistUuid
            """,
            new { artistUuid },
            tx
        );
        var keySet = keys.ToHashSet();
        var tracks = parts
            .GroupBy(p => p.Track)
            .Where(g => g.All(p => !AuthorTypes.IsHuman(p.AuthorType)))
            .Where(g =>
                g.Any(p =>
                    p.Text != null && keySet.Contains(Fold(TitleNormalizer.Normalize(p.Text)))
                )
            )
            .Select(g => g.Key)
            .ToArray();
        await connection.ExecuteAsync(
            "DELETE FROM kb.track_songs WHERE track_uuid = ANY(@tracks)",
            new { tracks },
            tx
        );

        // Upstream setlist songs mapped through those spellings were mapped by the same
        // answers: forget the machine's mappings too, so the match rebuilds them from the new
        // aliases.
        var setlist = await connection.QueryAsync<(Guid Uuid, string Name)>(
            """
            SELECT ss.uuid, ss.name FROM kb.song_evidence e
            JOIN setlist_songs ss ON ss.uuid = e.setlist_song_uuid
            JOIN artists a ON a.id = ss.artist_id
            WHERE a.uuid = @artistUuid AND e.author_type NOT IN ('user', 'moderator')
            """,
            new { artistUuid },
            tx
        );
        var evidence = setlist
            .Where(s => keySet.Contains(TitleNormalizer.Normalize(s.Name)))
            .Select(s => s.Uuid)
            .ToArray();
        await connection.ExecuteAsync(
            "DELETE FROM kb.song_evidence WHERE setlist_song_uuid = ANY(@evidence)",
            new { evidence },
            tx
        );

        return (aliases, tracks.Length);
    }

    private static readonly Dictionary<string, string[]> Keys = new()
    {
        ["songs"] = ["uuid"],
        ["song_aliases"] = ["artist_uuid", "normalized"],
        ["song_evidence"] = ["setlist_song_uuid"],
        ["track_songs"] = ["track_uuid", "part"],
    };

    /// Undoes another run from kb.changes, newest change first: its inserts are deleted, its
    /// updates restored and its deletes re-inserted. Only a person may revert (a run can hold
    /// people's rows), and a row someone changed since is left as it is now and reported.
    public async Task<(int Reverted, int Skipped)> RevertRunAsync(Guid run)
    {
        if (!Human)
        {
            throw new InvalidOperationException("Only a person can revert a run");
        }

        var changes = await connection.QueryAsync<(
            long Id,
            string Table,
            string Key,
            string Op,
            string? Before,
            string? After
        )>(
            """
            SELECT id, table_name, row_key::text, op, before::text, after::text FROM kb.changes
            WHERE run_uuid = @run ORDER BY id DESC
            """,
            new { run },
            tx
        );

        var (reverted, skipped) = (0, 0);
        foreach (var change in changes)
        {
            var keys = Keys[change.Table];
            var args = new
            {
                change.Table,
                change.Key,
                change.Before,
                change.After,
            };
            var match = string.Join(" AND ", keys.Select(k => $"t.{k} = k.{k}"));
            var keyRow = $"jsonb_populate_record(NULL::kb.{change.Table}, @Key::jsonb)";

            // Untouched since: the row is still exactly what this change left (or still absent).
            var untouched = await connection.ExecuteScalarAsync<bool>(
                change.Op == "delete"
                    ? $"SELECT NOT EXISTS (SELECT 1 FROM kb.{change.Table} t, {keyRow} k WHERE {match})"
                    : $"SELECT EXISTS (SELECT 1 FROM kb.{change.Table} t, {keyRow} k WHERE {match} AND to_jsonb(t) = @After::jsonb)",
                args,
                tx
            );
            if (!untouched)
            {
                skipped++;
                continue;
            }

            var columns = await connection.QueryAsync<string>(
                "SELECT quote_ident(column_name) FROM information_schema.columns WHERE table_schema = 'kb' AND table_name = @Table ORDER BY ordinal_position",
                args,
                tx
            );
            var list = string.Join(", ", columns);
            await connection.ExecuteAsync(
                change.Op switch
                {
                    "insert" => $"DELETE FROM kb.{change.Table} t USING {keyRow} k WHERE {match}",
                    "update" =>
                        $"UPDATE kb.{change.Table} t SET ({list}) = (SELECT {list} FROM jsonb_populate_record(NULL::kb.{change.Table}, @Before::jsonb)) FROM {keyRow} k WHERE {match}",
                    _ =>
                        $"INSERT INTO kb.{change.Table} ({list}) SELECT {list} FROM jsonb_populate_record(NULL::kb.{change.Table}, @Before::jsonb)",
                },
                args,
                tx
            );
            reverted++;
        }

        return (reverted, skipped);
    }

    /// How many distinct tracks this run has changed so far, from its own change log.
    public Task<int> TracksChangedAsync() =>
        connection.ExecuteScalarAsync<int>(
            """
            SELECT count(DISTINCT row_key ->> 'track_uuid') FROM kb.changes
            WHERE run_uuid = @Run AND table_name = 'track_songs'
            """,
            new { Run },
            tx
        );

    public Task FinishAsync() =>
        connection.ExecuteAsync(
            "UPDATE kb.runs SET finished_at = clock_timestamp() WHERE uuid = @Run",
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
                        // A track with an unresolved part is still open: filling it in is not
                        // changing a settled fact, unless a person wrote it.
                        ? """
                        SELECT track_uuid FROM kb.track_songs WHERE track_uuid = ANY(@tracks)
                        GROUP BY track_uuid
                        HAVING bool_and(resolved) OR bool_or(author_type IN ('user', 'moderator'))
                        """
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
            INSERT INTO kb.track_songs (track_uuid, part, song_uuid, text, segue_after, qualifiers, resolved,
                                        author_type, author_ref, confidence, run_uuid)
            SELECT x.track, x.part, x.song, x.text, x.segue,
                   coalesce(string_to_array(nullif(x.qualifiers, ''), ','), '{}'), x.resolved,
                   @AuthorType, @AuthorRef, x.confidence, @Run
            FROM unnest(@tracks, @partNumbers, @songs, @texts, @segues, @qualifiers, @resolved, @confidences)
                 AS x(track, part, song, text, segue, qualifiers, resolved, confidence)
            ON CONFLICT (track_uuid, part) DO UPDATE SET song_uuid = EXCLUDED.song_uuid, text = EXCLUDED.text,
                segue_after = EXCLUDED.segue_after, qualifiers = EXCLUDED.qualifiers, resolved = EXCLUDED.resolved,
                author_type = EXCLUDED.author_type, author_ref = EXCLUDED.author_ref,
                confidence = EXCLUDED.confidence, run_uuid = EXCLUDED.run_uuid, updated_at = now()
            WHERE (kb.track_songs.song_uuid, kb.track_songs.text, kb.track_songs.segue_after, kb.track_songs.qualifiers, kb.track_songs.resolved)
                  IS DISTINCT FROM (EXCLUDED.song_uuid, EXCLUDED.text, EXCLUDED.segue_after, EXCLUDED.qualifiers, EXCLUDED.resolved)
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
                resolved = writable.Select(p => p.Resolved).ToArray(),
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
