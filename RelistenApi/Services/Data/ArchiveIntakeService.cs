using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json.Linq;
using Relisten.Api.Models;
using Relisten.Import;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.TapersSection;

namespace Relisten.Data;

/// <summary>
/// Owns retained Archive evidence, reviewed artist membership and recording publication.
/// PostgreSQL is the recovery source: workflow history can be lost without losing approval,
/// stable catalog references or the revisions that still need a derived-catalog rebuild.
/// </summary>
public sealed partial class ArchiveIntakeService(
    DbService db,
    CatalogImportTransaction transactions,
    ArtistService artists,
    ArchiveOrgImporter importer,
    UpstreamSourceService upstreamSources,
    CatalogRebuildService rebuild
)
{
    private readonly TapersSectionEligibilityEvaluator evaluator = new();

    private Task<T> InTransaction<T>(
        CancellationToken token,
        Func<Task<T>> apply,
        DateTimeOffset? deadline = null
    ) => transactions.RunAsync(deadline ?? DateTimeOffset.UtcNow.AddMinutes(5), token, apply);

    /// <summary>Retains a whole received page before its discovery cursor may advance.</summary>
    public async Task<ArchiveItemId[]> PersistDiscoveryAsync(
        ArchiveItemId[] items,
        CancellationToken token = default,
        DateTimeOffset? deadline = null
    )
    {
        foreach (var batch in items.Distinct().Chunk(500))
        {
            token.ThrowIfCancellationRequested();
            await InTransaction(
                token,
                () =>
                    db.WithWriteConnection(connection =>
                        connection.ExecuteAsync(
                            """
                            INSERT INTO normalization.archive_item_intake(item_identifier)
                            SELECT unnest(@ids::text[])
                            ON CONFLICT(item_identifier) DO UPDATE SET last_discovered_at = now()
                            """,
                            new { ids = batch.Select(item => item.Value).ToArray() }
                        )
                    ),
                deadline
            );
        }

        var pending = await db.WithWriteConnection(connection =>
            connection.QueryAsync<string>(
                """
                SELECT item_identifier FROM normalization.archive_item_intake
                WHERE item_identifier = ANY(@ids) AND
                    (latest_observation_id IS NULL OR last_error IS NOT NULL OR rules_version <> @rulesVersion)
                ORDER BY item_identifier
                """,
                new
                {
                    ids = items.Select(item => item.Value).Distinct().ToArray(),
                    rulesVersion = TapersSectionEligibilityEvaluator.RulesVersion,
                }
            )
        );
        return pending.Select(id => new ArchiveItemId(id)).ToArray();
    }

    private Task<int> EnsureItemAsync(
        ArchiveItemId item,
        CancellationToken token,
        DateTimeOffset? deadline = null
    ) =>
        InTransaction(
            token,
            () =>
                db.WithWriteConnection(connection =>
                    connection.ExecuteAsync(
                        """
                        INSERT INTO normalization.archive_item_intake(item_identifier) VALUES(@item)
                        ON CONFLICT(item_identifier) DO NOTHING
                        """,
                        new { item = item.Value }
                    )
                ),
            deadline
        );

    /// <summary>Captures immutable evidence and current review state without writing a recording.</summary>
    public async Task<TapersSectionItemResult> RecordObservationAsync(
        ArchiveObservationProjection observation,
        CancellationToken token = default,
        DateTimeOffset? deadline = null
    )
    {
        await EnsureItemAsync(observation.Item, token, deadline);
        return await InTransaction(
            token,
            async () =>
            {
                var observationId = await SaveObservationAsync(observation);
                var candidate = await DiscoverCandidateAsync(observation, observationId);
                if (candidate != null)
                {
                    await LockCandidateAsync(candidate.candidate_id);
                }

                var item = await LockItemAsync(observation.Item);
                var interpretation = await InterpretAsync(observation, item);
                await SaveInterpretationAsync(item, observationId, observation, interpretation);
                return Result(item, interpretation);
            },
            deadline
        );
    }

    /// <summary>
    /// Retains collection evidence without assigning an artist or claiming recording ownership.
    /// Native recordings can be linked into collections while their current importer remains in charge.
    /// </summary>
    public Task<Guid> RetainObservationAsync(
        ArchiveObservationProjection observation,
        CancellationToken token = default,
        DateTimeOffset? deadline = null
    ) => InTransaction(token, () => SaveObservationAsync(observation), deadline);

    private Task<Guid> SaveObservationAsync(ArchiveObservationProjection observation) =>
        db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<Guid>(
                """
                INSERT INTO normalization.archive_item_observations
                    (observation_id,item_identifier,projection_version,content_hash,provider_url,payload)
                VALUES(@id,@item,@ProjectionVersion,@ContentHash,@url,CAST(@PayloadJson AS jsonb))
                ON CONFLICT(item_identifier,projection_version,content_hash)
                    DO UPDATE SET content_hash = EXCLUDED.content_hash
                RETURNING observation_id
                """,
                new
                {
                    id = Guid.NewGuid(),
                    item = observation.Item.Value,
                    observation.ProjectionVersion,
                    observation.ContentHash,
                    observation.PayloadJson,
                    url = "https://archive.org/metadata/"
                        + Uri.EscapeDataString(observation.Item.Value),
                }
            )
        );

    private async Task<CandidateRow?> DiscoverCandidateAsync(
        ArchiveObservationProjection observation,
        Guid observationId
    )
    {
        if (observation.IsAvailabilityOnly)
        {
            return null;
        }

        var creator = ArchiveObservationProjector
            .ParseObject(observation.PayloadJson)["metadata"]
            ?["creator"];
        var labels = (
            creator is JArray array ? array
            : creator == null ? new JArray()
            : new JArray(creator)
        ).Where(value => value.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string?)value)).Select(value => value.Value<string>()!).ToArray();
        var keys = labels.Select(ArtistRegistryService.NormalizeIdentityKey).Distinct().ToArray();
        if (keys.Length != 1)
        {
            return null;
        }

        var candidates = await LoadCandidatesAsync();
        var matches = candidates.Where(candidate => candidate.Keys.Contains(keys[0])).ToArray();
        CandidateRow selected;
        if (matches.Length > 1)
        {
            return null;
        }
        if (matches.Length == 1)
        {
            selected = matches[0];
        }
        else
        {
            selected = await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<CandidateRow>(
                    """
                    INSERT INTO normalization.archive_artist_candidates
                        (candidate_id,creator_key,proposed_name,proposed_slug,reviewed_creator_keys)
                    VALUES(@id,@key,@name,@slug,@keys)
                    ON CONFLICT(creator_key) DO UPDATE SET updated_at=now()
                    RETURNING *
                    """,
                    new
                    {
                        id = Guid.NewGuid(),
                        key = keys[0],
                        name = labels[0].Trim(),
                        slug = SlugUtils.Slugify(labels[0].Trim()),
                        keys,
                    }
                )
            );
        }

        // A short evidence sample keeps the bulk review view useful without making the
        // candidate row a second observation ledger. Approval and reviewed labels are untouched.
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                """
                UPDATE normalization.archive_artist_candidates
                SET representative_observation_ids = CASE
                    WHEN @observationId = ANY(representative_observation_ids) THEN representative_observation_ids
                    ELSE (ARRAY[@observationId] || representative_observation_ids)[1:5] END,
                    updated_at=now()
                WHERE candidate_id=@id
                """,
                new { observationId, id = selected.candidate_id }
            )
        );
        await RefreshMatchesAsync(selected);
        return selected;
    }

    private async Task RefreshMatchesAsync(CandidateRow candidate)
    {
        if (candidate.artist_uuid != null)
        {
            return;
        }

        var matches = await FindRegistryMatchesAsync(candidate);
        candidate.matched_artist_uuids = matches;
        candidate.identity_status =
            matches.Length == 0 ? "pending"
            : matches.Length == 1 ? "existing_match"
            : "needs_review";
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                """
                UPDATE normalization.archive_artist_candidates
                SET matched_artist_uuids=@matches, identity_status=@identity, updated_at=now()
                WHERE candidate_id=@id AND artist_uuid IS NULL
                """,
                new
                {
                    matches,
                    identity = candidate.identity_status,
                    id = candidate.candidate_id,
                }
            )
        );
    }

    private async Task<Guid[]> FindRegistryMatchesAsync(CandidateRow candidate)
    {
        var labels = await db.WithWriteConnection(connection =>
            connection.QueryAsync<RegistryLabel>(
                """
                SELECT a.uuid, a.name, a.slug, aus.upstream_identifier AS archive_label
                FROM artists a LEFT JOIN artists_upstream_sources aus ON aus.artist_id=a.id
                    AND aus.upstream_source_id IN (SELECT id FROM upstream_sources WHERE name='archive.org')
                """
            )
        );
        var keys = candidate
            .Keys.Append(ArtistRegistryService.NormalizeIdentityKey(candidate.proposed_name))
            .Append(ArtistRegistryService.NormalizeIdentityKey(candidate.proposed_slug))
            .ToHashSet();
        return labels
            .Where(row =>
                new[] { row.name, row.slug, row.archive_label }
                    .Where(label => label != null)
                    .Any(label => keys.Contains(ArtistRegistryService.NormalizeIdentityKey(label!)))
            )
            .Select(row => row.uuid)
            .Distinct()
            .ToArray();
    }

    private async Task<TapersSectionInterpretation> InterpretAsync(
        ArchiveObservationProjection observation,
        ItemRow item
    )
    {
        var candidates = await LoadCandidatesAsync();
        return evaluator.Evaluate(
            observation,
            candidates
                .Select(candidate => new TapersSectionCandidateIdentity(
                    candidate.candidate_id,
                    candidate.approved,
                    candidate.Keys,
                    candidate.artist_uuid,
                    candidate.artist_uuid == null && candidate.matched_artist_uuids.Length == 1,
                    candidate.artist_uuid == null
                        && (
                            candidate.matched_artist_uuids.Length > 1
                            || candidate.identity_status == "needs_review"
                        )
                ))
                .ToArray(),
            item.Assignment
        );
    }

    private Task SaveInterpretationAsync(
        ItemRow item,
        Guid observationId,
        ArchiveObservationProjection observation,
        TapersSectionInterpretation interpretation
    ) =>
        db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                """
                UPDATE normalization.archive_item_intake SET
                    latest_observation_id=CASE WHEN @availabilityOnly THEN latest_observation_id ELSE @observationId END,
                    candidate_id=CASE WHEN published_source_uuid IS NOT NULL THEN candidate_id ELSE @candidateId END,
                    last_observed_at=now(), last_attempt_at=now(), rules_version=@RulesVersion,
                    interpretation=CAST(@interpretation AS jsonb), status=@status, reason=@reason,
                    last_error=NULL, attempts=0, next_attempt_at=now()+interval '1 day'
                WHERE item_identifier=@item
                """,
                new
                {
                    observationId,
                    availabilityOnly = observation.IsAvailabilityOnly,
                    candidateId = interpretation.CandidateId,
                    interpretation.RulesVersion,
                    interpretation = JsonSerializer.Serialize(interpretation),
                    status = Status(item, interpretation),
                    reason = Reason(interpretation),
                    item = item.item_identifier,
                }
            )
        );

    private static TapersSectionReason? PrimaryReason(TapersSectionInterpretation value)
    {
        if (value.Reasons.Contains(TapersSectionReason.UpstreamUnavailable))
        {
            return TapersSectionReason.UpstreamUnavailable;
        }
        if (value.Reasons.Contains(TapersSectionReason.NoVbrMp3))
        {
            return TapersSectionReason.NoVbrMp3;
        }
        if (value.Reasons.Contains(TapersSectionReason.PublishedAssignmentConflict))
        {
            return TapersSectionReason.PublishedAssignmentConflict;
        }
        return value.Reasons.Length > 0 ? value.Reasons[0] : null;
    }

    private static string? Reason(TapersSectionInterpretation value) =>
        PrimaryReason(value) is { } reason ? JsonSerializer.Serialize(reason).Trim('"') : null;

    private static string Status(ItemRow item, TapersSectionInterpretation value)
    {
        if (value.IsUnavailable)
        {
            return "unavailable";
        }
        if (value.Reasons.Contains(TapersSectionReason.PublishedAssignmentConflict))
        {
            return "needs_review";
        }
        if (value.Reasons.Contains(TapersSectionReason.ExistingArtist))
        {
            return "deferred_existing_artist";
        }
        if (value.Reasons.Length == 1 && value.Reasons[0] == TapersSectionReason.UnapprovedArtist)
        {
            return item.published_source_uuid != null ? "paused" : "awaiting_artist_approval";
        }
        if (!value.IsEligible)
        {
            return "needs_review";
        }
        return item.published_source_uuid != null ? "published" : "eligible";
    }

    private static TapersSectionItemResult Result(
        ItemRow item,
        TapersSectionInterpretation value
    ) =>
        new(
            new ArchiveItemId(item.item_identifier),
            Status(item, value) switch
            {
                "unavailable" => TapersSectionItemOutcome.Unavailable,
                "deferred_existing_artist" => TapersSectionItemOutcome.Deferred,
                "paused" => TapersSectionItemOutcome.Paused,
                "published" => TapersSectionItemOutcome.Unchanged,
                _ => TapersSectionItemOutcome.NeedsReview,
            },
            item.published_source_uuid,
            PrimaryReason(value)
        );

    private Task<CandidateRow[]> LoadCandidatesAsync() =>
        db.WithWriteConnection(async connection =>
            (
                await connection.QueryAsync<CandidateRow>(
                    "SELECT * FROM normalization.archive_artist_candidates"
                )
            ).ToArray()
        );

    private Task<CandidateRow?> LockCandidateAsync(Guid id) =>
        db.WithWriteConnection(connection =>
            connection.QuerySingleOrDefaultAsync<CandidateRow>(
                "SELECT * FROM normalization.archive_artist_candidates WHERE candidate_id=@id FOR UPDATE",
                new { id }
            )
        );

    private Task<ItemRow> LockItemAsync(ArchiveItemId item) =>
        db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<ItemRow>(
                "SELECT * FROM normalization.archive_item_intake WHERE item_identifier=@item FOR UPDATE",
                new { item = item.Value }
            )
        );

    private sealed class RegistryLabel
    {
        public Guid uuid { get; set; }
        public string name { get; set; } = "";
        public string slug { get; set; } = "";
        public string? archive_label { get; set; }
    }

    private sealed class CandidateRow
    {
        public Guid candidate_id { get; set; }
        public string creator_key { get; set; } = "";
        public string proposed_name { get; set; } = "";
        public string proposed_slug { get; set; } = "";
        public string[] reviewed_creator_keys { get; set; } = [];
        public bool approved { get; set; }
        public string identity_status { get; set; } = "";
        public Guid[] matched_artist_uuids { get; set; } = [];
        public Guid? artist_uuid { get; set; }
        public int? artist_id { get; set; }
        public string? creation_slug { get; set; }
        public string[] Keys =>
            reviewed_creator_keys
                .Select(ArtistRegistryService.NormalizeIdentityKey)
                .Distinct()
                .ToArray();
    }

    private sealed class ItemRow
    {
        public string item_identifier { get; set; } = "";
        public Guid? latest_observation_id { get; set; }
        public Guid? candidate_id { get; set; }
        public Guid? published_observation_id { get; set; }
        public int? published_rules_version { get; set; }
        public Guid? published_artist_uuid { get; set; }
        public int? published_artist_id { get; set; }
        public string? published_display_date { get; set; }
        public Guid? published_source_uuid { get; set; }
        public long catalog_revision { get; set; }
        public long rebuilt_revision { get; set; }
        public TapersSectionPublishedAssignment? Assignment =>
            published_artist_uuid is { } uuid && published_display_date is { } date
                ? new(uuid, date)
                : null;
    }
}
