using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.TapersSection;

namespace Relisten.Data;

public sealed partial class ArchiveIntakeService
{
    /// <summary>Recovers retained observation work even when an item leaves the discovery collection.</summary>
    public async Task<ArchiveItemId[]> PendingObservationsAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var pending = await db.WithWriteConnection(connection =>
            connection.QueryAsync<string>(
                """
                SELECT item_identifier FROM normalization.archive_item_intake
                WHERE next_attempt_at <= now() AND
                    (latest_observation_id IS NULL OR last_error IS NOT NULL OR rules_version <> @version)
                ORDER BY next_attempt_at,item_identifier LIMIT 5000
                """,
                new { version = TapersSectionEligibilityEvaluator.RulesVersion }
            )
        );
        return pending.Select(item => new ArchiveItemId(item)).ToArray();
    }

    /// <summary>Selects approved cohort members and every owner of committed work awaiting rebuild.</summary>
    public async Task<ArtistId[]> PrepareCohortAsync(
        CancellationToken token = default,
        DateTimeOffset? deadline = null
    )
    {
        var candidates = await LoadCandidatesAsync();
        foreach (var candidate in candidates.Where(value => value.approved))
        {
            token.ThrowIfCancellationRequested();
            await MaterializeArtistAsync(candidate.candidate_id, token, deadline);
        }

        var selected = await db.WithWriteConnection(connection =>
            connection.QueryAsync<Guid>(
                """
                SELECT c.artist_uuid FROM normalization.archive_artist_candidates c
                WHERE c.artist_uuid IS NOT NULL AND
                    (c.approved OR EXISTS(SELECT 1 FROM normalization.archive_item_intake i
                        WHERE i.published_artist_uuid=c.artist_uuid AND i.catalog_revision>i.rebuilt_revision))
                ORDER BY c.artist_uuid
                """
            )
        );
        return selected.Select(uuid => new ArtistId(uuid)).ToArray();
    }

    /// <summary>Admits mapped cohort artists, including paused owners that need recovery.</summary>
    public Task<bool> IsCohortArtistAsync(ArtistId artist, CancellationToken token = default) =>
        db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM normalization.archive_artist_candidates WHERE artist_uuid=@uuid)",
                new { uuid = artist.Value }
            )
        );

    /// <summary>Published identifiers remain selected after collection moves and for historical dates.</summary>
    public async Task<TapersSectionItemInput[]> ItemsForArtistAsync(
        ArtistId artist,
        CancellationToken token = default
    )
    {
        token.ThrowIfCancellationRequested();
        var rows = await db.WithWriteConnection(connection =>
            connection.QueryAsync<SelectedItem>(
                """
                SELECT i.item_identifier,c.candidate_id
                FROM normalization.archive_item_intake i
                JOIN normalization.archive_artist_candidates c ON c.candidate_id=i.candidate_id
                WHERE c.artist_uuid=@uuid AND c.approved
                ORDER BY i.item_identifier
                """,
                new { uuid = artist.Value }
            )
        );
        return rows.Select(row => new TapersSectionItemInput(
                new ArchiveItemId(row.item_identifier),
                row.candidate_id
            ))
            .ToArray();
    }

    /// <summary>Records transport/parser failures separately from identity classification and publication.</summary>
    public async Task RecordObservationFailureAsync(
        ArchiveItemId item,
        string error,
        CancellationToken token = default
    )
    {
        token.ThrowIfCancellationRequested();
        await EnsureItemAsync(item, token);
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                """
                UPDATE normalization.archive_item_intake
                SET last_error=@error,last_attempt_at=now(),attempts=LEAST(attempts+1,1000000),
                    next_attempt_at=now()+interval '1 hour'
                WHERE item_identifier=@item
                """,
                new { item = item.Value, error = error.Length > 1000 ? error[..1000] : error }
            )
        );
    }

    /// <summary>Captures revisions before rebuilding; later writes remain dirty after this acknowledgement.</summary>
    public async Task<TapersSectionDirtyRevision[]> CaptureDirtyRevisionsAsync(
        ArtistId artist,
        CancellationToken token = default
    )
    {
        token.ThrowIfCancellationRequested();
        var rows = await db.WithWriteConnection(connection =>
            connection.QueryAsync<DirtyRow>(
                """
                SELECT item_identifier,catalog_revision FROM normalization.archive_item_intake
                WHERE published_artist_uuid=@uuid AND catalog_revision>rebuilt_revision
                ORDER BY item_identifier
                """,
                new { uuid = artist.Value }
            )
        );
        return rows.Select(row => new TapersSectionDirtyRevision(
                new(row.item_identifier),
                row.catalog_revision
            ))
            .ToArray();
    }

    /// <summary>
    /// Acknowledges only captured revisions after shows, years and cache rebuild succeed.
    /// Visibility promotion locks approval separately from observation work. A paused artist's
    /// committed rows are repaired, but its AutoCreated flag stays set.
    /// </summary>
    public async Task<bool> CompleteRebuildAsync(
        ArtistId artist,
        TapersSectionDirtyRevision[] revisions,
        CancellationToken token = default,
        DateTimeOffset? deadline = null
    )
    {
        var promoted = await InTransaction(
            token,
            async () =>
            {
                var candidateId = await db.WithWriteConnection(connection =>
                    connection.QuerySingleOrDefaultAsync<Guid?>(
                        "SELECT candidate_id FROM normalization.archive_artist_candidates WHERE artist_uuid=@uuid",
                        new { uuid = artist.Value }
                    )
                );
                if (candidateId == null)
                {
                    return false;
                }
                var candidate = (await LockCandidateAsync(candidateId.Value))!;
                foreach (
                    var revision in revisions.OrderBy(
                        value => value.Item.Value,
                        StringComparer.Ordinal
                    )
                )
                {
                    await db.WithWriteConnection(connection =>
                        connection.ExecuteAsync(
                            """
                            UPDATE normalization.archive_item_intake
                            SET rebuilt_revision=GREATEST(rebuilt_revision,LEAST(catalog_revision,@Revision))
                            WHERE item_identifier=@item AND published_artist_uuid=@uuid
                            """,
                            new
                            {
                                item = revision.Item.Value,
                                uuid = artist.Value,
                                revision.Revision,
                            }
                        )
                    );
                }
                var current = await artists.FindArtistForImport(artist.ToString());
                if (!candidate.approved || !IdentityMatches(candidate, current))
                {
                    return false;
                }
                var changed = await db.WithWriteConnection(connection =>
                    connection.ExecuteAsync(
                        """
                        UPDATE artists a SET featured=featured & ~@flag,updated_at=now()
                        WHERE a.uuid=@uuid AND (featured & @flag)<>0
                            AND NOT EXISTS(SELECT 1 FROM normalization.archive_item_intake i
                                WHERE i.published_artist_uuid=a.uuid AND i.catalog_revision>i.rebuilt_revision)
                            AND EXISTS(SELECT 1 FROM normalization.archive_item_intake i
                                JOIN sources s ON s.uuid=i.published_source_uuid
                                JOIN source_tracks t ON t.source_id=s.id AND NOT t.is_orphaned
                                WHERE i.published_artist_uuid=a.uuid AND t.mp3_url IS NOT NULL)
                        """,
                        new { uuid = artist.Value, flag = (int)ArtistFeaturedFlags.AutoCreated }
                    )
                );
                return changed > 0;
            },
            deadline
        );
        // This cache operation is intentionally retryable after the transaction. A retry
        // must invalidate even if the previous attempt committed promotion before failing.
        var currentArtist = await artists.FindArtistForImport(artist.ToString());
        if (currentArtist != null)
        {
            await rebuild.InvalidateArtistCache(currentArtist);
        }
        return promoted;
    }

    /// <summary>Returns bounded operational state from primary PostgreSQL without requiring Temporal.</summary>
    public async Task<object> StatusAsync(string? item = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var counts = await db.WithWriteConnection(connection =>
            connection.QueryAsync(
                """
                SELECT status,count(*) AS count,
                    count(*) FILTER(WHERE catalog_revision>rebuilt_revision) AS dirty,
                    min(last_attempt_at) FILTER(WHERE catalog_revision>rebuilt_revision) AS oldest_dirty_attempt
                FROM normalization.archive_item_intake GROUP BY status ORDER BY status
                """
            )
        );
        var items = await db.WithWriteConnection(connection =>
            connection.QueryAsync(
                """
                SELECT i.item_identifier,i.candidate_id,i.status,i.reason,i.last_error,
                    i.last_attempt_at,i.last_observed_at,i.next_attempt_at,i.attempts,
                    i.latest_observation_id,i.rules_version,i.published_observation_id,
                    i.published_rules_version,i.published_artist_uuid,i.published_artist_id,
                    i.published_display_date,i.published_source_uuid,i.published_at,
                    i.catalog_revision,i.rebuilt_revision,
                    c.approved,c.identity_status,c.proposed_name,c.artist_uuid,
                    o.content_hash AS latest_content_hash,o.projection_version
                FROM normalization.archive_item_intake i
                LEFT JOIN normalization.archive_artist_candidates c ON c.candidate_id=i.candidate_id
                LEFT JOIN normalization.archive_item_observations o ON o.observation_id=i.latest_observation_id
                WHERE @item IS NULL OR i.item_identifier=@item
                ORDER BY i.discovered_at DESC,i.item_identifier LIMIT 100
                """,
                new { item }
            )
        );
        var candidates = await db.WithWriteConnection(connection =>
            connection.QueryAsync(
                """
                SELECT candidate_id,proposed_name,proposed_slug,approved,identity_status,
                    artist_uuid,artist_id,creation_slug,reason,item_count,eligible_count,example_item
                FROM normalization.archive_artist_review ORDER BY created_at DESC,candidate_id LIMIT 100
                """
            )
        );
        return new
        {
            counts,
            items,
            candidates,
            limit = 100,
        };
    }

    private sealed class SelectedItem
    {
        public string item_identifier { get; set; } = "";
        public Guid candidate_id { get; set; }
    }

    private sealed class DirtyRow
    {
        public string item_identifier { get; set; } = "";
        public long catalog_revision { get; set; }
    }
}
