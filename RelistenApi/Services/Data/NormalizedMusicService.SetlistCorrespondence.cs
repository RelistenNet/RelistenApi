using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedMusicService
{
    private static async Task<SetlistStructureChange> ValidateStructureCorrespondence(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistStructureChange structure,
        bool reviewed,
        CancellationToken ct
    )
    {
        var normalizedSets = new List<SetlistSetCorrespondence>();
        var normalizedPassages = new List<SetlistPassageCorrespondence>();
        var sets = structure.Sets.ToDictionary(set => set.Uuid);
        var occurrences = structure.Occurrences.ToDictionary(occurrence => occurrence.Uuid);
        var expectedSets = structure
            .Sources.SelectMany(
                (source, index) =>
                    source.Account.Account.Sets.Select(
                        (set, setIndex) => (Source: index, Set: setIndex)
                    )
            )
            .ToHashSet();
        var mappedSets = structure
            .SetCorrespondence.Select(map => (Source: map.SourceIndex, Set: map.SetIndex))
            .ToArray();
        if (
            mappedSets.Distinct().Count() != mappedSets.Length
            || !expectedSets.SetEquals(mappedSets)
            || structure.SetCorrespondence.Any(map => !sets.ContainsKey(map.SetUuid))
            || sets.Values.Where(set => !set.Retracted)
                .Any(set => !structure.SetCorrespondence.Any(map => map.SetUuid == set.Uuid))
        )
        {
            throw new ArgumentException(
                "Every source set, including empty sets, requires explicit correspondence to a set in the complete image."
            );
        }
        var expectedPassages = structure
            .Sources.SelectMany(
                (source, index) =>
                    source.Account.Passages.Select(passage =>
                        (Source: index, Proposal: passage.ProposalUuid)
                    )
            )
            .ToHashSet();
        var accounted = structure
            .PassageCorrespondence.Select(map =>
                (Source: map.SourceIndex, Proposal: map.ProposalUuid)
            )
            .Concat(
                structure.UnresolvedPassages.Select(map =>
                    (Source: map.SourceIndex, Proposal: map.ProposalUuid)
                )
            )
            .ToArray();
        if (
            accounted.Distinct().Count() != accounted.Length
            || !expectedPassages.SetEquals(accounted)
            || structure.UnresolvedPassages.Any(passage =>
                string.IsNullOrWhiteSpace(passage.Reason)
            )
        )
        {
            throw new ArgumentException(
                "Every source passage requires explicit correspondence or an unresolved reason; omission does not erase a claim."
            );
        }
        if (
            structure.PassageCorrespondence.Any(map =>
                map.OccurrenceUuids.Count == 0
                || map.OccurrenceUuids.Distinct().Count() != map.OccurrenceUuids.Count
                || map.OccurrenceUuids.Any(id => !occurrences.ContainsKey(id))
            )
            || occurrences
                .Values.Where(occurrence => !occurrence.Retracted)
                .Any(occurrence =>
                    !structure.PassageCorrespondence.Any(map =>
                        map.OccurrenceUuids.Contains(occurrence.Uuid)
                    )
                )
        )
        {
            throw new ArgumentException(
                "Accepted active passages require explicit source correspondence and matching target UUIDs."
            );
        }
        foreach (var map in structure.SetCorrespondence)
        {
            var source = structure.Sources[map.SourceIndex];
            var original = source.Account.Account.Sets[map.SetIndex];
            var set = sets[map.SetUuid];
            if (!structure.Retracted && set.Retracted)
            {
                throw new ArgumentException(
                    "A current source set cannot map to a retracted target."
                );
            }
            if (
                !reviewed
                && (
                    set.Label != original.Label?.Value
                    || set.EncoreNumber != original.EncoreNumber?.Value
                    || set.ProviderIdentity != original.Identity?.Identity
                    || (structure.Edition.Ordered && set.Position != original.Ordinal)
                    || !set.Evidence.Contains(original.Evidence)
                )
            )
            {
                throw new ArgumentException(
                    "Automatic set metadata must reproduce the supplied source assertion."
                );
            }
            var authority = await ResolveSetlistCorrespondenceAuthority(
                c,
                t,
                structure,
                source,
                new(SetIndex: map.SetIndex),
                [map.SetUuid],
                original.Identity?.Identity,
                reviewed,
                structure.SetCorrespondence.Count(other => other.SetUuid == set.Uuid) > 1,
                map.AuthorizationUuid,
                ct
            );
            normalizedSets.Add(map with { AuthorizationUuid = authority });
        }
        foreach (var map in structure.PassageCorrespondence)
        {
            var source = structure.Sources[map.SourceIndex];
            var original = source
                .Account.Passages.Single(passage => passage.ProposalUuid == map.ProposalUuid)
                .Passage;
            var passageAuthority = await ResolveSetlistCorrespondenceAuthority(
                c,
                t,
                structure,
                source,
                new(ProposalUuid: map.ProposalUuid),
                map.OccurrenceUuids,
                original.OccurrenceIdentity?.Identity,
                reviewed,
                structure.PassageCorrespondence.Any(other =>
                    (other.SourceIndex != map.SourceIndex || other.ProposalUuid != map.ProposalUuid)
                    && other.OccurrenceUuids.Intersect(map.OccurrenceUuids).Any()
                ),
                map.AuthorizationUuid,
                ct
            );
            foreach (var uuid in map.OccurrenceUuids)
            {
                var occurrence = occurrences[uuid];
                if (!structure.Retracted && occurrence.Retracted)
                {
                    throw new ArgumentException(
                        "A current source passage cannot map to a retracted target."
                    );
                }
                if (!reviewed)
                {
                    var sourceSet = original.SetOrdinal is { } ordinal
                        ? source
                            .Account.Account.Sets.Select((set, index) => (set, index))
                            .Where(item => item.set.Ordinal == ordinal)
                            .ToArray()
                        : [];
                    var setUuid =
                        sourceSet.Length == 1
                            ? structure
                                .SetCorrespondence.Single(link =>
                                    link.SourceIndex == map.SourceIndex
                                    && link.SetIndex == sourceSet[0].index
                                )
                                .SetUuid
                            : (Guid?)null;
                    var transition =
                        original.Segue?.Value == true
                        || (
                            original.TransitionMarker is { } marker
                            && Regex.IsMatch(
                                marker.Value.Trim(),
                                @"\A(?:-?>+|→)\z",
                                RegexOptions.CultureInvariant
                            )
                        )
                            ? "segue"
                            : "unknown";
                    if (
                        occurrence.SetLabel != original.SetLabel?.Value
                        || occurrence.TransitionAfter != transition
                        || occurrence.OriginalText != original.Title.Value
                        || !occurrence.Evidence.Contains(original.Title.Evidence)
                        || occurrence.SetUuid != setUuid
                        || (
                            structure.Edition.Ordered
                            && occurrence.ParentOccurrenceUuid == null
                            && occurrence.Position != original.Ordinal
                        )
                        || (
                            occurrence.ProviderItemKey != null
                            && occurrence.ProviderItemKey
                                != original.OccurrenceIdentity?.Identity.ProviderKey
                        )
                    )
                    {
                        throw new ArgumentException(
                            "Automatic passage acceptance requires its exact source wording, known order and set membership."
                        );
                    }
                }
            }
            normalizedPassages.Add(map with { AuthorizationUuid = passageAuthority });
        }
        await ValidateStructureRemovalAuthorizations(c, t, structure, reviewed, ct);
        return structure with
        {
            SetCorrespondence = normalizedSets,
            PassageCorrespondence = normalizedPassages,
        };
    }

    private static async Task<Guid?> ResolveSetlistCorrespondenceAuthority(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistStructureChange structure,
        SetlistStructureSource source,
        SetlistClaimReference claim,
        IReadOnlyList<Guid> completeTargets,
        NormalizationResourceKey? identity,
        bool reviewed,
        bool sharedTarget,
        Guid? declaredAuthorization,
        CancellationToken ct
    )
    {
        var kind = claim.SetIndex.HasValue ? "set" : "passage";
        var key = claim.SetIndex.HasValue
            ? claim.SetIndex.Value.ToString(CultureInfo.InvariantCulture)
            : claim.ProposalUuid!.Value.ToString("D");
        if (reviewed && declaredAuthorization == null)
        {
            return null;
        }
        var declared = await ReadDeclaredCorrespondenceAuthorization(
            c,
            t,
            structure.Edition.Uuid,
            source,
            claim,
            completeTargets,
            declaredAuthorization,
            ct
        );
        if (declared.HasValue)
        {
            return declared;
        }
        // Resolve the latest accepted decision before considering a new target. Otherwise a
        // new UUID plus a tombstone could silently replace a stable provider identity. Older
        // reviewed reallocations do not authorize returning to an obsolete target.
        var targets = (
            await c.QueryAsync<AcceptedSetlistTarget>(
                new CommandDefinition(
                    """
                    WITH claims AS (
                        SELECT link.target_uuid,link.authorization_uuid,r.revision,
                            h.authorization_uuid AS head_uuid,a.action AS head_action
                        FROM normalization.setlist_structure_correspondences link
                        JOIN normalization.music_revisions r ON r.uuid=link.revision_uuid
                        LEFT JOIN normalization.setlist_authorization_heads h ON h.edition_uuid=link.edition_uuid
                            AND h.authorization_key='claim/'||link.source_attempt_uuid::text||'/'||link.input_revision_uuid::text
                                ||'/'||link.account_index::text||'/'||link.kind||'/'||link.source_key
                        LEFT JOIN normalization.setlist_authorization_events a ON a.uuid=h.authorization_uuid
                        WHERE link.edition_uuid=@edition AND link.kind=@kind
                        AND ((link.input_revision_uuid=@InputRevisionUuid AND link.account_index=@AccountIndex AND link.source_key=@key
                                AND EXISTS(SELECT 1 FROM normalization.setlist_processing_attempts old_source
                                    JOIN normalization.setlist_processing_attempts new_source ON new_source.attempt_uuid=@ProposalAttemptUuid
                                    WHERE old_source.attempt_uuid=link.source_attempt_uuid AND old_source.processing_version=new_source.processing_version))
                            OR (@identity::jsonb IS NOT NULL AND link.provider_identity @> @identity::jsonb AND link.provider_identity=@identity::jsonb))
                    )
                    SELECT target_uuid AS TargetUuid,authorization_uuid AS AuthorizationUuid,
                        bool_or(head_uuid IS NOT NULL AND (head_action<>'authorize' OR head_uuid IS DISTINCT FROM authorization_uuid)) AS Superseded
                    FROM claims WHERE revision=(SELECT max(revision) FROM claims)
                    GROUP BY target_uuid,authorization_uuid LIMIT 101
                    """,
                    new
                    {
                        edition = structure.Edition.Uuid,
                        kind,
                        source.InputRevisionUuid,
                        source.AccountIndex,
                        source.ProposalAttemptUuid,
                        key,
                        identity = identity == null ? null : JsonSerializer.Serialize(identity),
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (targets.Length > 0)
        {
            if (targets.Any(item => item.Superseded))
            {
                throw new NormalizationConflictException(
                    "The historical correspondence was blocked, revoked or superseded; accepted history cannot restore it."
                );
            }
            if (
                declaredAuthorization.HasValue
                && targets[0].AuthorizationUuid != declaredAuthorization
            )
            {
                throw new NormalizationConflictException(
                    "The supplied authorization does not support this accepted correspondence history."
                );
            }
            var targetIds = targets.Select(item => item.TargetUuid).ToHashSet();
            var authorities = targets.Select(item => item.AuthorizationUuid).Distinct().ToArray();
            if (
                !targetIds.SetEquals(completeTargets)
                || authorities.Length != 1
                || (completeTargets.Count > 1 && authorities[0] == null)
            )
            {
                throw new ArgumentException(
                    "The latest accepted source/provider correspondence requires its stable target UUID, or explicit reviewed reallocation."
                );
            }
            if (targets[0].AuthorizationUuid is { } authority)
            {
                await ValidateInheritedCorrespondenceAuthorization(
                    c,
                    t,
                    structure.Edition.Uuid,
                    authority,
                    completeTargets,
                    ct
                );
            }
            return targets[0].AuthorizationUuid;
        }
        var table = kind == "set" ? "setlist_sets" : "song_occurrences";
        if (completeTargets.Count > 1)
        {
            throw new ArgumentException(
                "Splitting a source passage into several accepted occurrences requires reviewed correspondence."
            );
        }
        if (declaredAuthorization.HasValue)
        {
            throw new NormalizationConflictException(
                "The supplied correspondence authorization is not active for this exact source claim."
            );
        }
        var exists = await c.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                $"SELECT EXISTS(SELECT 1 FROM normalization.{table} WHERE uuid=ANY(@completeTargets))",
                new { completeTargets = completeTargets.ToArray() },
                t,
                cancellationToken: ct
            )
        );
        if (exists || sharedTarget)
        {
            throw new ArgumentException(
                "Reusing an accepted set or occurrence needs retained correspondence, a supplied qualified occurrence/set identity, or explicit review; titles and positions do not establish identity."
            );
        }
        return null;
    }

    private sealed record AcceptedSetlistTarget(
        Guid TargetUuid,
        Guid? AuthorizationUuid,
        bool Superseded
    );

    private static async Task AppendStructureCorrespondence(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistStructureChange structure,
        Guid revision,
        CancellationToken ct
    )
    {
        foreach (var map in structure.SetCorrespondence)
        {
            var source = structure.Sources[map.SourceIndex];
            await AppendCorrespondence(
                c,
                t,
                structure.Edition.Uuid,
                revision,
                map.SourceIndex,
                source,
                "set",
                map.SetIndex.ToString(CultureInfo.InvariantCulture),
                map.SetUuid,
                source.Account.Account.Sets[map.SetIndex].Identity?.Identity,
                map.AuthorizationUuid,
                ct
            );
        }
        foreach (var map in structure.PassageCorrespondence)
        {
            var source = structure.Sources[map.SourceIndex];
            var identity = source
                .Account.Passages.Single(passage => passage.ProposalUuid == map.ProposalUuid)
                .Passage.OccurrenceIdentity?.Identity;
            foreach (var target in map.OccurrenceUuids)
            {
                await AppendCorrespondence(
                    c,
                    t,
                    structure.Edition.Uuid,
                    revision,
                    map.SourceIndex,
                    source,
                    "passage",
                    map.ProposalUuid.ToString("D"),
                    target,
                    identity,
                    map.AuthorizationUuid,
                    ct
                );
            }
        }
    }

    private static Task AppendCorrespondence(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid edition,
        Guid revision,
        int sourceIndex,
        SetlistStructureSource source,
        string kind,
        string key,
        Guid target,
        NormalizationResourceKey? identity,
        Guid? authorizationUuid,
        CancellationToken ct
    ) =>
        c.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO normalization.setlist_structure_correspondences(revision_uuid,source_index,source_attempt_uuid,input_revision_uuid,
                    account_index,kind,source_key,target_uuid,edition_uuid,provider_identity,authorization_uuid)
                VALUES(@revision,@sourceIndex,@ProposalAttemptUuid,@InputRevisionUuid,@AccountIndex,@kind,@key,@target,@edition,@identity::jsonb,@authorizationUuid)
                """,
                new
                {
                    revision,
                    sourceIndex,
                    source.ProposalAttemptUuid,
                    source.InputRevisionUuid,
                    source.AccountIndex,
                    kind,
                    key,
                    target,
                    edition,
                    authorizationUuid,
                    identity = identity == null ? null : JsonSerializer.Serialize(identity),
                },
                t,
                cancellationToken: ct
            )
        );
}
