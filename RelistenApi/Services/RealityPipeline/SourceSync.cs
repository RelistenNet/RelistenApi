using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace Relisten.RealityPipeline;

/// What one sync found: the events whose inputs may have changed, each with the sources that
/// changed in it. Only these events are signalled.
public sealed record SourceSyncResult(
    IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> ChangedEvents,
    int EventsMinted,
    int SnapshotsWritten
);

/// Runs after an import has rebuilt an artist's shows. It records what each source looks like
/// now (media.import_snapshots, written only when something changed), files every source
/// under its show's default event (the importer default of one event per show: the
/// event.legacy_show and source.event claims, docs/catalogue.md §Events vs shows), and
/// reports the events whose manifests could have changed. A source changes its event's
/// inputs only when its tracks change in position, md5 or title, when it appears or
/// disappears, or when it moves to another event; never on sources.updated_at
/// (data-model.md §5).
public sealed class SourceSync(RealityDb db)
{
    public const string Author = "event-default@1.0.0";
    public const string SnapshotImporter = "catalog-sync@1";

    public async Task<SourceSyncResult> SyncArtist(int artistId)
    {
        await using var connection = await db.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        // One sync per artist at a time; imports of other artists proceed.
        await connection.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtext('reality-source-sync'), @artistId)",
            new { artistId },
            tx
        );

        var changed = new Dictionary<Guid, HashSet<Guid>>();
        void Changed(Guid eventUuid, Guid source)
        {
            if (!changed.TryGetValue(eventUuid, out var sources))
                changed[eventUuid] = sources = [];
            sources.Add(source);
        }

        Guid? botRun = null;
        async Task<Guid> BotRun()
        {
            if (botRun is { } existing)
                return existing;
            botRun = Guid.CreateVersion7();
            await connection.ExecuteAsync(
                """
                INSERT INTO kb.bot_runs (uuid, author_type, author_ref, mode, finished_at)
                VALUES (@botRun, 'importer', @Author, 'live', now())
                """,
                new { botRun, Author },
                tx
            );
            return botRun.Value;
        }

        var minted = await MintShowEvents(connection, tx, artistId, BotRun);
        var moves = await FileSources(connection, tx, artistId, BotRun);
        foreach (var (source, from, to) in moves)
        {
            if (from is { } old)
                Changed(old, source);
            Changed(to, source);
        }

        var snapshots = 0;
        foreach (var source in await ChangedSources(connection, tx, artistId))
        {
            snapshots++;
            if (!source.ContentChanged)
                continue; // only a track uuid or url moved: evidence, not an input
            foreach (var eventUuid in source.Events)
                Changed(eventUuid, source.SourceUuid);
        }

        foreach (var (source, eventUuid) in await VanishedSources(connection, tx, artistId))
        {
            snapshots++;
            Changed(eventUuid, source);
        }

        await tx.CommitAsync();
        return new SourceSyncResult(
            changed.ToDictionary(e => e.Key, e => (IReadOnlyList<Guid>)e.Value.Order().ToList()),
            minted,
            snapshots
        );
    }

    /// One event per show that has sources and no event yet.
    private static async Task<int> MintShowEvents(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        int artistId,
        Func<Task<Guid>> botRun
    )
    {
        var shows = (
            await connection.QueryAsync<Guid>(
                """
                SELECT sh.uuid FROM shows sh
                WHERE sh.artist_id = @artistId
                  AND EXISTS (SELECT 1 FROM sources s WHERE s.show_id = sh.id)
                  AND NOT EXISTS (SELECT 1 FROM kb.current_event_shows ces WHERE ces.show_uuid = sh.uuid)
                """,
                new { artistId },
                tx
            )
        ).ToArray();
        if (shows.Length == 0)
            return 0;

        var events = shows.Select(_ => Guid.CreateVersion7()).ToArray();
        var claims = shows.Select(_ => Guid.CreateVersion7()).ToArray();
        await connection.ExecuteAsync(
            """
            INSERT INTO kb.entities (uuid, type) SELECT unnest(@events), 'event';
            INSERT INTO kb.claims (uuid, subject_type, subject_uuid, predicate, value_schema_version,
                                   value, author_type, author_ref, bot_run_uuid)
            SELECT c, 'event', e, 'legacy_show', 1, jsonb_build_object('show', s), 'importer', @Author, @botRun
            FROM unnest(@claims, @events, @shows) AS x(c, e, s);
            INSERT INTO kb.current_event_shows (event_uuid, show_uuid, claim_uuid)
            SELECT e, s, c FROM unnest(@events, @shows, @claims) AS x(e, s, c);
            """,
            new
            {
                events,
                claims,
                shows,
                Author,
                botRun = await botRun(),
            },
            tx
        );
        return shows.Length;
    }

    /// Files each source under its show's default event, unless a non-importer claim
    /// (an algorithm's split, a moderator) placed it. Returns (source, old event, new event)
    /// for every source that joined or moved.
    private static async Task<List<(Guid Source, Guid? From, Guid To)>> FileSources(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        int artistId,
        Func<Task<Guid>> botRun
    )
    {
        var rows = await connection.QueryAsync<(
            Guid Source,
            Guid Desired,
            Guid? Current,
            Guid? Claim,
            string? AuthorType
        )>(
            """
            WITH defaults AS (
                -- A show's default event: the one this importer filed it under (the earliest,
                -- if a split added more).
                SELECT DISTINCT ON (ces.show_uuid) ces.show_uuid, ces.event_uuid
                FROM kb.current_event_shows ces
                JOIN kb.claims c ON c.uuid = ces.claim_uuid
                JOIN shows sh ON sh.uuid = ces.show_uuid AND sh.artist_id = @artistId
                ORDER BY ces.show_uuid, (c.author_ref = @Author) DESC, c.created_at, ces.event_uuid
            )
            SELECT s.uuid, d.event_uuid, cse.event_uuid, cse.claim_uuid, c.author_type
            FROM sources s
            JOIN shows sh ON sh.id = s.show_id
            JOIN defaults d ON d.show_uuid = sh.uuid
            LEFT JOIN kb.current_source_events cse ON cse.source_uuid = s.uuid
            LEFT JOIN kb.claims c ON c.uuid = cse.claim_uuid
            WHERE s.artist_id = @artistId
            """,
            new { artistId, Author },
            tx
        );

        var moves = new List<(Guid, Guid?, Guid)>();
        foreach (var source in rows.GroupBy(r => r.Source))
        {
            var desired = source.First().Desired;
            var current = source.Where(r => r.Current != null).ToList();
            if (current.Any(r => r.AuthorType != "importer"))
                continue; // placed by review or an algorithm: not the importer's to change
            if (current.Count == 1 && current[0].Current == desired)
                continue;

            var claim = Guid.CreateVersion7();
            await connection.ExecuteAsync(
                """
                INSERT INTO kb.claims (uuid, subject_type, subject_uuid, predicate, value_schema_version,
                                       value, author_type, author_ref, bot_run_uuid, supersedes)
                VALUES (@claim, 'source', @source, 'event', 1, jsonb_build_object('event', @desired),
                        'importer', @Author, @botRun, @supersedes);
                DELETE FROM kb.current_source_events WHERE source_uuid = @source;
                INSERT INTO kb.current_source_events (source_uuid, event_uuid, md5s, claim_uuid)
                VALUES (@source, @desired, NULL, @claim);
                """,
                new
                {
                    claim,
                    source = source.Key,
                    desired,
                    Author,
                    botRun = await botRun(),
                    supersedes = current.FirstOrDefault().Claim,
                },
                tx
            );
            if (current.Count == 0)
                moves.Add((source.Key, null, desired));
            foreach (var old in current)
                moves.Add((source.Key, old.Current, desired));
        }
        return moves;
    }

    private sealed record ChangedSource(Guid SourceUuid, bool ContentChanged, Guid[] Events);

    /// Sources whose tracks differ from their latest snapshot in anything (a new snapshot is
    /// written for each), flagged ContentChanged when position, md5 or title differ.
    private static async Task<List<ChangedSource>> ChangedSources(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        int artistId
    )
    {
        const string content =
            "concat_ws(E'\\x1f', {0}track_position, coalesce({0}md5, ''), {0}title)";
        const string full =
            "concat_ws(E'\\x1f', {0}track_position, coalesce({0}md5, ''), {0}title, {0}track_uuid, coalesce({0}mp3_url, ''))";
        var current = $"""
            SELECT s.uuid AS source_uuid, s.upstream_identifier,
                   md5(string_agg({string.Format(
                content,
                "t."
            )}, E'\x1e' ORDER BY t.track_position)) AS content_fp,
                   md5(string_agg({string.Format(
                full,
                "t."
            )}, E'\x1e' ORDER BY t.track_position)) AS full_fp
            FROM sources s
            JOIN (SELECT source_id, track_position, mp3_md5 AS md5, title, uuid AS track_uuid, mp3_url
                  FROM source_tracks WHERE source_id IS NOT NULL AND NOT is_orphaned) t ON t.source_id = s.id
            WHERE s.artist_id = @artistId
            GROUP BY s.uuid, s.upstream_identifier
            """;
        var rows = await connection.QueryAsync<(
            Guid Source,
            string Upstream,
            string ContentFp,
            string? OldContentFp,
            Guid[] Events
        )>(
            $"""
            WITH cur AS ({current}),
            latest AS (
                SELECT DISTINCT ON (source_uuid) uuid, source_uuid FROM media.import_snapshots
                WHERE source_uuid IN (SELECT source_uuid FROM cur)
                ORDER BY source_uuid, seen_at DESC, seq DESC
            ),
            old AS (
                SELECT l.source_uuid,
                       md5(string_agg({string.Format(
                content,
                "st."
            )}, E'\x1e' ORDER BY st.track_position)) AS content_fp,
                       md5(string_agg({string.Format(
                full,
                "st."
            )}, E'\x1e' ORDER BY st.track_position)) AS full_fp
                FROM latest l JOIN media.import_snapshot_tracks st ON st.snapshot_uuid = l.uuid
                GROUP BY l.source_uuid
            )
            SELECT cur.source_uuid, cur.upstream_identifier, cur.content_fp, old.content_fp,
                   coalesce((SELECT array_agg(cse.event_uuid ORDER BY cse.event_uuid)
                             FROM kb.current_source_events cse WHERE cse.source_uuid = cur.source_uuid),
                            ARRAY[]::uuid[])
            FROM cur LEFT JOIN old USING (source_uuid)
            WHERE old.full_fp IS DISTINCT FROM cur.full_fp
            """,
            new { artistId },
            tx
        );

        var changed = rows.ToList();
        if (changed.Count == 0)
            return [];

        var snapshotUuids = changed.Select(_ => Guid.CreateVersion7()).ToArray();
        await connection.ExecuteAsync(
            """
            INSERT INTO media.import_snapshots (uuid, source_uuid, upstream_identifier, importer)
            SELECT * , @SnapshotImporter FROM unnest(@snapshotUuids, @sources, @upstreams);
            INSERT INTO media.import_snapshot_tracks (snapshot_uuid, track_uuid, track_position, md5, title, mp3_url)
            SELECT x.snapshot_uuid, t.uuid, t.track_position, t.mp3_md5, t.title, t.mp3_url
            FROM unnest(@snapshotUuids, @sources) AS x(snapshot_uuid, source_uuid)
            JOIN sources s ON s.uuid = x.source_uuid
            JOIN source_tracks t ON t.source_id = s.id AND NOT t.is_orphaned;
            """,
            new
            {
                snapshotUuids,
                sources = changed.Select(c => c.Source).ToArray(),
                upstreams = changed.Select(c => c.Upstream).ToArray(),
                SnapshotImporter,
            },
            tx
        );
        return changed
            .Select(c => new ChangedSource(c.Source, c.ContentFp != c.OldContentFp, c.Events))
            .ToList();
    }

    /// Sources filed under this artist's events that no longer exist. Each gets an empty
    /// snapshot (the disappearance, as evidence) once; its event is reported.
    private static async Task<List<(Guid Source, Guid Event)>> VanishedSources(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        int artistId
    )
    {
        var rows = (
            await connection.QueryAsync<(Guid Source, Guid Event, string Upstream)>(
                """
                SELECT cse.source_uuid, cse.event_uuid, snap.upstream_identifier
                FROM kb.current_source_events cse
                JOIN kb.current_event_shows ces ON ces.event_uuid = cse.event_uuid
                JOIN shows sh ON sh.uuid = ces.show_uuid AND sh.artist_id = @artistId
                JOIN LATERAL (
                    SELECT uuid, upstream_identifier FROM media.import_snapshots
                    WHERE source_uuid = cse.source_uuid ORDER BY seen_at DESC, seq DESC LIMIT 1
                ) snap ON true
                WHERE NOT EXISTS (SELECT 1 FROM sources s WHERE s.uuid = cse.source_uuid)
                  AND EXISTS (SELECT 1 FROM media.import_snapshot_tracks st WHERE st.snapshot_uuid = snap.uuid)
                """,
                new { artistId },
                tx
            )
        ).ToList();
        foreach (var source in rows.DistinctBy(r => r.Source))
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO media.import_snapshots (uuid, source_uuid, upstream_identifier, importer)
                VALUES (@uuid, @Source, @Upstream, @SnapshotImporter)
                """,
                new
                {
                    uuid = Guid.CreateVersion7(),
                    source.Source,
                    source.Upstream,
                    SnapshotImporter,
                },
                tx
            );
        }
        return rows.Select(r => (r.Source, r.Event)).ToList();
    }
}
