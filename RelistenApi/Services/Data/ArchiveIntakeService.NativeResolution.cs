using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json.Linq;
using Relisten.Api.Models;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.TapersSection;

namespace Relisten.Data;

// Exact native-source reuse happens before publication and again under the item lock.
// It records evidence without transferring the recording to the intake writer.
public sealed partial class ArchiveIntakeService
{
    private async Task<TapersSectionItemResult?> ResolveNativeAsync(
        ArchiveObservationProjection observation,
        CancellationToken token,
        DateTimeOffset? deadline
    )
    {
        var target = await FindNativeResolutionTargetAsync(observation);
        if (target == null)
        {
            return null;
        }

        return await InTransaction<TapersSectionItemResult?>(
            token,
            async () =>
            {
                // Publication and reviews use candidate -> intake item -> resolution. Linking
                // a native recording must not assign candidate_id and claim its refresh ownership.
                await LockCandidateAsync(target.CandidateId);
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
                        expected_artist_uuid = target.ArtistUuid,
                        decision_basis = target.Basis,
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

    private async Task<NativeResolutionTarget?> FindNativeResolutionTargetAsync(
        ArchiveObservationProjection observation
    )
    {
        var decision = await performerReviews.FindPerformerDecisionAsync(observation.Item.Value);
        if (decision != null)
        {
            return new(decision.CandidateId, decision.SelectedArtistUuid, decision.ReviewBasis);
        }
        var creator = ArchiveObservationProjector
            .ParseObject(observation.PayloadJson)["metadata"]
            ?["creator"];
        var labels =
            creator is JArray array ? array.ToArray()
            : creator == null ? []
            : new[] { creator };
        if (labels.Any(value => value.Type != JTokenType.String))
        {
            return null;
        }
        var keys = labels
            .Select(value => ArtistRegistryService.NormalizeIdentityKey(value.Value<string>()!))
            .Distinct()
            .ToArray();
        if (keys.Length != 1)
        {
            return null;
        }
        var candidates = (await LoadCandidatesAsync())
            .Where(candidate =>
                candidate.is_existing_artist
                && candidate.artist_uuid != null
                && candidate.Keys.Contains(keys[0])
            )
            .ToArray();
        if (candidates.Length != 1)
        {
            return null;
        }
        return new(
            candidates[0].candidate_id,
            candidates[0].artist_uuid,
            "Reviewed existing artist; reuse exact native Archive source."
        );
    }

    private sealed record NativeResolutionTarget(Guid CandidateId, Guid? ArtistUuid, string Basis);
}
