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
    /// <summary>
    /// Proposes an explicitly reviewed performing project, then uses ordinary cohort
    /// materialization when approved. Labels must name this project: broad creator labels
    /// belong to individual performer decisions, never to this shared vocabulary.
    /// A pre-existing catalog match remains deferred rather than creating a duplicate.
    /// </summary>
    public async Task<Guid> ProposeProjectAsync(
        string name,
        string slug,
        string[] reviewedLabels,
        bool approved,
        CancellationToken token = default,
        DateTimeOffset? deadline = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        if (reviewedLabels.Length == 0 || reviewedLabels.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Reviewed project labels must be nonempty.",
                nameof(reviewedLabels)
            );
        }

        var keys = reviewedLabels
            .Append(name)
            .Select(ArtistRegistryService.NormalizeIdentityKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (
            ArtistRegistryService.NormalizeIdentityKey(name) != "JERRY GARCIA"
            && keys.Contains("JERRY GARCIA", StringComparer.Ordinal)
        )
        {
            throw new ArgumentException(
                "Jerry Garcia is an umbrella creator label, not an alias for another project.",
                nameof(reviewedLabels)
            );
        }
        var candidateId = await transactions.RunArtistRegistryAsync(
            deadline ?? DateTimeOffset.UtcNow.AddMinutes(5),
            token,
            async () =>
            {
                var candidate = await db.WithWriteConnection(connection =>
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
                            key = ArtistRegistryService.NormalizeIdentityKey(name),
                            name,
                            slug,
                            keys,
                        }
                    )
                );
                candidate = (await LockCandidateAsync(candidate.candidate_id))!;
                if (
                    (candidate.approved || candidate.artist_uuid != null)
                    && (
                        candidate.proposed_name != name
                        || candidate.proposed_slug != slug
                        || !candidate.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(keys)
                    )
                )
                {
                    throw new ArgumentException(
                        "Pause and review an existing project's identity before changing its labels."
                    );
                }

                await db.WithWriteConnection(connection =>
                    connection.ExecuteAsync(
                        """
                        UPDATE normalization.archive_artist_candidates
                        SET proposed_name=@name,proposed_slug=@slug,reviewed_creator_keys=@keys,
                            approved=@approved,updated_at=now()
                        WHERE candidate_id=@id
                        """,
                        new
                        {
                            id = candidate.candidate_id,
                            name,
                            slug,
                            keys,
                            approved,
                        }
                    )
                );
                candidate.proposed_name = name;
                candidate.proposed_slug = slug;
                candidate.reviewed_creator_keys = keys;
                await RefreshMatchesAsync(candidate);
                return candidate.candidate_id;
            }
        );

        // Materialization rechecks the live registry under its own transaction. A race
        // after proposal can defer the candidate, but cannot create a duplicate artist.
        if (approved)
        {
            await MaterializeArtistAsync(candidateId, token, deadline);
        }
        return candidateId;
    }

    private async Task<TapersSectionItemResult?> ResolveReviewedNativeAsync(
        ArchiveObservationProjection observation,
        CancellationToken token,
        DateTimeOffset? deadline
    )
    {
        var decision = await performerReviews.FindPerformerDecisionAsync(observation.Item.Value);
        if (decision == null)
        {
            return null;
        }

        return await InTransaction<TapersSectionItemResult?>(
            token,
            async () =>
            {
                // Publication and reviews use candidate -> intake item -> resolution. Linking
                // a native recording must not assign candidate_id and claim its refresh ownership.
                await LockCandidateAsync(decision.CandidateId);
                var item = await db.WithWriteConnection(connection =>
                    connection.QuerySingleOrDefaultAsync<ItemRow>(
                        "SELECT * FROM normalization.archive_item_intake WHERE item_identifier=@item FOR UPDATE",
                        new { item = observation.Item.Value }
                    )
                );
                if (item?.published_source_uuid != null)
                {
                    return null;
                }

                var observationId = await SaveObservationAsync(observation);
                var resolution = await new ArchiveItemResolutionService(db).ResolveExistingAsync(
                    new ArchiveItemResolutionRequest(observation.Item.Value)
                    {
                        observation_id = observationId,
                        expected_artist_uuid = decision.SelectedArtistUuid,
                        decision_basis = decision.ReviewBasis,
                    },
                    token,
                    deadline
                );
                if (resolution.status == "pending" && resolution.canonical_source_uuid == null)
                {
                    // No native source or accepted reference exists. The normal candidate
                    // publisher remains responsible for this new project's recording.
                    return null;
                }

                // Discovery may have assigned an unpublished candidate before review. Release
                // that claim while recording this check, so native refresh stays with its importer
                // and discovery does not keep treating a resolved item as unobserved. Clear
                // the superseded interpretation so operator dates cannot outlive that claim.
                await db.WithWriteConnection(connection =>
                    connection.ExecuteAsync(
                        """
                        UPDATE normalization.archive_item_intake SET
                            candidate_id=NULL,interpretation=NULL,
                            latest_observation_id=CASE WHEN @availabilityOnly THEN latest_observation_id ELSE @observationId END,
                            rules_version=@rulesVersion,last_observed_at=now(),last_attempt_at=now(),
                            last_error=NULL,attempts=0,next_attempt_at=now()+interval '1 day',
                            status=@status,reason=@reason
                        WHERE item_identifier=@item AND published_source_uuid IS NULL
                        """,
                        new
                        {
                            item = observation.Item.Value,
                            observationId,
                            availabilityOnly = observation.IsAvailabilityOnly,
                            rulesVersion = TapersSectionEligibilityEvaluator.RulesVersion,
                            status = resolution.status == "resolved"
                                ? "deferred_existing_artist"
                                : "needs_review",
                            reason = resolution.status == "resolved"
                                ? "native_source_linked"
                                : "resolution_conflict",
                        }
                    )
                );

                return new TapersSectionItemResult(
                    observation.Item,
                    resolution.status == "resolved"
                        ? TapersSectionItemOutcome.Unchanged
                        : TapersSectionItemOutcome.NeedsReview,
                    resolution.canonical_source_uuid,
                    resolution.status == "resolved"
                        ? null
                        : TapersSectionReason.CatalogIdentityMissing
                );
            },
            deadline
        );
    }

    private readonly ArchivePerformerReviewService performerReviews = new(db);
}
