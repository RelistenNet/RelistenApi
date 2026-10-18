using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRepertoireResolutionStore
{
    /// <summary>
    /// Called after the domain writes, before Work records its owner publication. The accepted
    /// writer already holds the fixed candidate scopes and artist owner through transaction commit.
    /// These effects explain only this stage's changes, never a later global-pool observation.
    /// </summary>
    private static async Task<RepertoireResolutionOwnEffects?> CaptureOwnEffectsAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        RepertoireResolutionStage stage,
        CancellationToken ct
    )
    {
        if (
            stage.Kind
            is not (
                RepertoireResolutionStageKind.AcceptedWrite
                or RepertoireResolutionStageKind.RetireArtistContribution
            )
        )
        {
            return null;
        }
        if (
            stage.Owner.Domain != "songs"
            || stage.Owner.Kind != "artist_repertoire"
            || stage.Owner.Uuid == Guid.Empty
        )
        {
            throw new NormalizationConflictException(
                "Repertoire own effects require the saved artist owner."
            );
        }

        var ticket = stage.Ticket!;
        var owner = new NormalizationSubjectObservation(
            ticket.SubjectUuid,
            ticket.DesiredGeneration,
            ticket.EditorialRevision,
            stage.WorkUuid
        );
        if (stage.Kind == RepertoireResolutionStageKind.RetireArtistContribution)
        {
            // Retirement owns only the artist watermark. It holds no candidate write scopes
            // and must not turn an unrelated pool change into an apparent effect of cleanup.
            return new([], owner);
        }

        var pools = (
            await c.QueryAsync<OwnEffectPool>(
                new CommandDefinition(
                    """
                    SELECT domain AS "Domain",scope_uuid AS "ScopeUuid",generation AS "Generation"
                    FROM normalization.candidate_universe_revisions
                    WHERE (domain='compositions' AND scope_uuid='00000000-0000-0000-0000-000000000000'::uuid)
                        OR (domain='repertoire' AND scope_uuid=@artistUuid)
                    ORDER BY domain,scope_uuid
                    """,
                    new { artistUuid = stage.Owner.Uuid },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (pools.Length != 2)
        {
            throw new NormalizationConflictException(
                "The accepted writer's fixed candidate scope watermarks are missing."
            );
        }
        return new(
            pools
                .Select(value => new NormalizationCandidateUniverseDependency(
                    new(value.Domain, value.ScopeUuid),
                    value.Generation
                ))
                .ToArray(),
            owner
        );
    }

    private sealed record OwnEffectPool(string Domain, Guid ScopeUuid, long Generation);
}
