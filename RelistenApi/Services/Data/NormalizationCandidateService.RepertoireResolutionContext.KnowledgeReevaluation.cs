using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    /// <summary>Publication authenticates authority retirement against retained history, independently of saved page JSON.</summary>
    internal static async Task RequireRepertoireExpertReevaluationAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid registration,
        RepertoireResolutionPageContext context,
        CancellationToken ct
    )
    {
        if (!context.Claims.Any(claim => claim.ExpertReevaluation != null))
        {
            return;
        }
        var artist = context.ArtistUuid!.Value;
        var current = await NormalizationExpertKnowledgeService.ReadRepertoireSnapshotAsync(
            c,
            t,
            artist,
            ct
        );
        if (JsonSerializer.Serialize(current) != JsonSerializer.Serialize(context.ExpertKnowledge))
        {
            throw new NormalizationConflictException(
                "The expert knowledge changed before its automatic effects could be re-evaluated."
            );
        }
        var history = new RepertoireKnowledgeHistory(c, t, artist, new(current), ct);
        foreach (var source in context.Claims.Where(source => source.ExpertReevaluation != null))
        {
            var original = source.ExpertReevaluation!.OriginalAssociation;
            if (original != null)
            {
                var stored = await c.ExecuteScalarAsync<bool>(
                    new CommandDefinition(
                        """
                        SELECT EXISTS(SELECT 1 FROM normalization.repertoire_resolution_associations
                            WHERE decision_uuid=@DecisionUuid AND registration_uuid=@registration
                                AND correspondence_key=@CorrespondenceKey AND attempt_uuid=@AttemptUuid
                                AND repertoire_song_uuid IS NOT DISTINCT FROM @RepertoireSongUuid::uuid
                            AND (@Authority::text IS NULL OR authority=@Authority))
                        """,
                        new
                        {
                            original.DecisionUuid,
                            registration,
                            original.CorrespondenceKey,
                            original.AttemptUuid,
                            original.RepertoireSongUuid,
                            original.Authority,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                if (
                    !stored
                    || original.RegistrationUuid != registration
                    || original.CorrespondenceKey != source.Claim.CorrespondenceKey
                )
                {
                    throw new NormalizationConflictException(
                        "The previous expert correspondence does not belong to this source account."
                    );
                }
            }
            var keys = source
                .Claim.Identities.Concat(source.Claim.AliasOf is { } alias ? [alias] : [])
                .Select(value => value.Identity)
                .ToHashSet();
            var external = source
                .ExistingExternalIdentities.Where(value =>
                    keys.Contains(new(value.Provider, value.ResourceType, value.ProviderKey))
                )
                .ToArray();
            var expected = await history.ReadAsync(original, source.ImportedAssignments, external);
            if (
                JsonSerializer.Serialize(expected.Effects)
                    != JsonSerializer.Serialize(source.ExpertReevaluation)
                || JsonSerializer.Serialize(expected.Association)
                    != JsonSerializer.Serialize(source.ExistingAssociation)
            )
            {
                throw new NormalizationConflictException(
                    "The saved repertoire re-evaluation differs from its immutable expert publication history."
                );
            }
        }
    }
}
