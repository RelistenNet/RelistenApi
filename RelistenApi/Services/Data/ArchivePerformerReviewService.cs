using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Relisten.Api.Models;
using Relisten.Ingestion.Providers.TapersSection;

namespace Relisten.Data;

/// <summary>
/// Owns explicit per-item performer reviews. Reviews never create a global artist alias, publish a
/// source, or replace a published assignment. Clearing approval pauses later writes while preserving playback.
/// </summary>
public sealed class ArchivePerformerReviewService(DbService db)
{
    private const string DecisionQuery = """
        SELECT r.item_identifier,r.performer_candidate_id AS candidate_id,
            r.performer_observation_id AS supporting_observation_id,r.performer_approved AS approved,
            r.performer_evidence::text AS evidence_json,r.performer_review_basis AS review_basis,
            r.performer_alternatives AS alternatives,r.performer_revision AS revision,
            COALESCE(c.artist_uuid,CASE WHEN cardinality(c.matched_artist_uuids)=1 THEN c.matched_artist_uuids[1] END) AS selected_artist_uuid,
            o.payload::text AS supporting_payload_json,o.content_hash AS supporting_content_hash,
            c.proposed_name,c.reviewed_creator_keys
        FROM normalization.archive_item_resolutions r
        JOIN normalization.archive_artist_candidates c ON c.candidate_id=r.performer_candidate_id
        JOIN normalization.archive_item_observations o ON o.observation_id=r.performer_observation_id
        WHERE r.item_identifier=@itemIdentifier
        """;

    /// <summary>Reads the retained review, including an unapproved or revoked decision and its original evidence.</summary>
    public Task<ArchivePerformerDecision?> FindPerformerDecisionAsync(string itemIdentifier) =>
        ReadDecisionAsync(itemIdentifier, false);

    /// <summary>
    /// Locks the current review for a publication decision. Call inside the catalog transaction
    /// after locking the candidate and intake item; approval edits follow that same lock order.
    /// </summary>
    public Task<ArchivePerformerDecision?> FindPerformerDecisionForUpdateAsync(
        string itemIdentifier
    ) => ReadDecisionAsync(itemIdentifier, true);

    /// <summary>Retains a reviewed project assignment supported by exact fields in one observation.</summary>
    public Task<ArchivePerformerDecision> ReviewAsync(
        ArchivePerformerReview review,
        CancellationToken token = default,
        DateTimeOffset? deadline = null
    )
    {
        ValidateReview(review);
        return new CatalogImportTransaction(db).RunAsync(
            deadline ?? DateTimeOffset.UtcNow.AddMinutes(5),
            token,
            async () =>
            {
                var project = await LockTargetAsync(review.ItemIdentifier, review.CandidateId);
                await EnsureCanonicalTargetAsync(review.ItemIdentifier, project.artist_uuid);
                var payload = await db.WithWriteConnection(connection =>
                    connection.QuerySingleOrDefaultAsync<string>(
                        "SELECT payload::text FROM normalization.archive_item_observations WHERE observation_id=@id AND item_identifier=@item",
                        new { id = review.SupportingObservationId, item = review.ItemIdentifier }
                    )
                );
                if (payload == null)
                {
                    throw new ArgumentException(
                        "The supporting observation must exist and belong to this item."
                    );
                }
                if (
                    !ArchivePerformerEvidence.IsValid(JObject.Parse(payload), review.Evidence)
                    || !ArchivePerformerEvidence.SupportsProject(project.Labels, review.Evidence)
                )
                {
                    throw new ArgumentException(
                        "Performer evidence must match retained billing fields and include an exact reviewed project name or label."
                    );
                }
                await db.WithWriteConnection(connection =>
                    connection.ExecuteAsync(
                        """
                        UPDATE normalization.archive_item_resolutions SET performer_candidate_id=@CandidateId,
                            performer_observation_id=@SupportingObservationId,performer_approved=@Approved,
                            performer_evidence=CAST(@evidence AS jsonb),performer_review_basis=@ReviewBasis,
                            performer_alternatives=@alternatives,
                            performer_revision=performer_revision+CASE WHEN
                                (performer_candidate_id,performer_observation_id,performer_approved,performer_evidence,performer_review_basis,performer_alternatives)
                                IS DISTINCT FROM (@CandidateId,@SupportingObservationId,@Approved,CAST(@evidence AS jsonb),@ReviewBasis,@alternatives)
                                THEN 1 ELSE 0 END,updated_at=now()
                        WHERE item_identifier=@ItemIdentifier
                        """,
                        new
                        {
                            review.ItemIdentifier,
                            review.CandidateId,
                            review.SupportingObservationId,
                            review.Approved,
                            evidence = JsonConvert.SerializeObject(review.Evidence),
                            review.ReviewBasis,
                            alternatives = review.Alternatives ?? [],
                        }
                    )
                );
                return (await FindPerformerDecisionAsync(review.ItemIdentifier))!;
            }
        );
    }

    /// <summary>Changes publication permission without changing the reviewed target or supporting evidence.</summary>
    public async Task<ArchivePerformerDecision> SetApprovalAsync(
        string itemIdentifier,
        bool approved,
        string reviewBasis,
        CancellationToken token = default,
        DateTimeOffset? deadline = null
    )
    {
        if (string.IsNullOrWhiteSpace(reviewBasis))
        {
            throw new ArgumentException("A review basis is required.");
        }
        var captured =
            await FindPerformerDecisionAsync(itemIdentifier)
            ?? throw new ArgumentException("No performer review exists for this item.");
        return await new CatalogImportTransaction(db).RunAsync(
            deadline ?? DateTimeOffset.UtcNow.AddMinutes(5),
            token,
            async () =>
            {
                var project = await LockTargetAsync(itemIdentifier, captured.CandidateId);
                var current = (await FindPerformerDecisionForUpdateAsync(itemIdentifier))!;
                if (
                    current.CandidateId != captured.CandidateId
                    || current.Revision != captured.Revision
                )
                {
                    throw new InvalidOperationException(
                        "The performer review changed during this approval edit; reload the review."
                    );
                }
                if (approved)
                {
                    await EnsureCanonicalTargetAsync(itemIdentifier, project.artist_uuid);
                    if (!ArchivePerformerEvidence.SupportsProject(project.Labels, current.Evidence))
                    {
                        throw new ArgumentException(
                            "The retained evidence no longer identifies this reviewed project."
                        );
                    }
                }
                await db.WithWriteConnection(connection =>
                    connection.ExecuteAsync(
                        """
                        UPDATE normalization.archive_item_resolutions SET performer_approved=@approved,performer_review_basis=@reviewBasis,
                            performer_revision=performer_revision+CASE WHEN performer_approved<>@approved
                                OR performer_review_basis IS DISTINCT FROM @reviewBasis THEN 1 ELSE 0 END,updated_at=now()
                        WHERE item_identifier=@itemIdentifier
                        """,
                        new
                        {
                            itemIdentifier,
                            approved,
                            reviewBasis,
                        }
                    )
                );
                return (await FindPerformerDecisionAsync(itemIdentifier))!;
            }
        );
    }

    private async Task<ProjectTarget> LockTargetAsync(string itemIdentifier, Guid candidateId)
    {
        var known = await db.WithWriteConnection(connection =>
            connection.QuerySingleOrDefaultAsync<ProjectTarget>(
                """
                SELECT proposed_name,reviewed_creator_keys,
                    COALESCE(artist_uuid,CASE WHEN cardinality(matched_artist_uuids)=1 THEN matched_artist_uuids[1] END) AS artist_uuid
                FROM normalization.archive_artist_candidates WHERE candidate_id=@candidateId FOR UPDATE
                """,
                new { candidateId }
            )
        );
        if (known == null)
        {
            throw new ArgumentException("The performing-project candidate does not exist.");
        }
        var published = await db.WithWriteConnection(connection =>
            connection.QuerySingleOrDefaultAsync<PublishedAssignment>(
                "SELECT candidate_id,published_source_uuid FROM normalization.archive_item_intake WHERE item_identifier=@itemIdentifier FOR UPDATE",
                new { itemIdentifier }
            )
        );
        if (published?.published_source_uuid != null && published.candidate_id != candidateId)
        {
            throw new ArgumentException(
                "A published performer assignment requires reviewed catalog repair before changing projects."
            );
        }
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                "INSERT INTO normalization.archive_item_resolutions(item_identifier) VALUES(@itemIdentifier) ON CONFLICT DO NOTHING",
                new { itemIdentifier }
            )
        );
        await db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<string>(
                "SELECT item_identifier FROM normalization.archive_item_resolutions WHERE item_identifier=@itemIdentifier FOR UPDATE",
                new { itemIdentifier }
            )
        );
        return known;
    }

    private async Task EnsureCanonicalTargetAsync(string itemIdentifier, Guid? targetArtist)
    {
        var accepted = await db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<AcceptedTarget>(
                "SELECT canonical_source_uuid,selected_artist_uuid FROM normalization.archive_item_resolutions WHERE item_identifier=@itemIdentifier",
                new { itemIdentifier }
            )
        );
        if (accepted.canonical_source_uuid != null && accepted.selected_artist_uuid != targetArtist)
        {
            throw new ArgumentException(
                "The accepted canonical performer requires reviewed catalog repair before selecting another project."
            );
        }
    }

    private async Task<ArchivePerformerDecision?> ReadDecisionAsync(
        string itemIdentifier,
        bool forUpdate
    )
    {
        var row = await db.WithWriteConnection(connection =>
            connection.QuerySingleOrDefaultAsync<DecisionRow>(
                DecisionQuery + (forUpdate ? " FOR UPDATE OF r" : ""),
                new { itemIdentifier }
            )
        );
        return row == null
            ? null
            : new(
                row.item_identifier,
                row.candidate_id,
                row.supporting_observation_id,
                row.approved,
                JsonConvert.DeserializeObject<TapersSectionEvidence[]>(row.evidence_json)!,
                row.review_basis,
                row.alternatives,
                row.revision,
                row.selected_artist_uuid,
                row.supporting_payload_json,
                row.supporting_content_hash
            )
            {
                ProjectLabels = row.reviewed_creator_keys.Append(row.proposed_name).ToArray(),
            };
    }

    private static void ValidateReview(ArchivePerformerReview review)
    {
        if (
            string.IsNullOrWhiteSpace(review.ItemIdentifier)
            || review.CandidateId == Guid.Empty
            || review.SupportingObservationId == Guid.Empty
            || string.IsNullOrWhiteSpace(review.ReviewBasis)
            || review.Evidence.Length == 0
        )
        {
            throw new ArgumentException(
                "An item, project candidate, supporting observation, evidence, and review basis are required."
            );
        }
    }

    private sealed class ProjectTarget
    {
        public string proposed_name { get; set; } = "";
        public string[] reviewed_creator_keys { get; set; } = [];
        public Guid? artist_uuid { get; set; }
        public string[] Labels => reviewed_creator_keys.Append(proposed_name).ToArray();
    }

    private sealed class AcceptedTarget
    {
        public Guid? canonical_source_uuid { get; set; }
        public Guid? selected_artist_uuid { get; set; }
    }

    private sealed class PublishedAssignment
    {
        public Guid? candidate_id { get; set; }
        public Guid? published_source_uuid { get; set; }
    }

    private sealed class DecisionRow
    {
        public string item_identifier { get; set; } = "";
        public Guid candidate_id { get; set; }
        public Guid supporting_observation_id { get; set; }
        public bool approved { get; set; }
        public string evidence_json { get; set; } = "[]";
        public string review_basis { get; set; } = "";
        public string[] alternatives { get; set; } = [];
        public long revision { get; set; }
        public Guid? selected_artist_uuid { get; set; }
        public string supporting_payload_json { get; set; } = "";
        public string supporting_content_hash { get; set; } = "";
        public string proposed_name { get; set; } = "";
        public string[] reviewed_creator_keys { get; set; } = [];
    }
}
