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
    private static async Task ValidateStructureCorrespondence(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistStructureChange structure,
        bool reviewed,
        CancellationToken ct
    )
    {
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
            await ValidateSetlistTargetReuse(
                c,
                t,
                structure,
                "set",
                set.Uuid,
                source,
                map.SetIndex.ToString(CultureInfo.InvariantCulture),
                original.Identity?.Identity,
                reviewed,
                structure.SetCorrespondence.Count(other => other.SetUuid == set.Uuid) > 1,
                ct
            );
        }
        foreach (var map in structure.PassageCorrespondence)
        {
            var source = structure.Sources[map.SourceIndex];
            var original = source
                .Account.Passages.Single(passage => passage.ProposalUuid == map.ProposalUuid)
                .Passage;
            if (!reviewed && map.OccurrenceUuids.Count > 1)
            {
                throw new ArgumentException(
                    "Splitting a source passage into several accepted occurrences requires reviewed correspondence."
                );
            }
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
                        || (structure.Edition.Ordered && occurrence.Position != original.Ordinal)
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
                await ValidateSetlistTargetReuse(
                    c,
                    t,
                    structure,
                    "passage",
                    uuid,
                    source,
                    map.ProposalUuid.ToString("D"),
                    original.OccurrenceIdentity?.Identity,
                    reviewed,
                    structure.PassageCorrespondence.Count(other =>
                        other.OccurrenceUuids.Contains(uuid)
                    ) > 1,
                    ct
                );
            }
        }
        if (!reviewed)
        {
            await Require(
                c,
                t,
                """
                SELECT NOT EXISTS(SELECT 1 FROM normalization.setlist_sets WHERE uuid=ANY(@sets) AND NOT retracted)
                    AND NOT EXISTS(SELECT 1 FROM normalization.song_occurrences WHERE uuid=ANY(@passages) AND NOT retracted)
                """,
                new
                {
                    sets = structure
                        .Sets.Where(set => set.Retracted)
                        .Select(set => set.Uuid)
                        .ToArray(),
                    passages = structure
                        .Occurrences.Where(occurrence => occurrence.Retracted)
                        .Select(occurrence => occurrence.Uuid)
                        .ToArray(),
                },
                "Retiring accepted sets or occurrences requires explicit reviewed removal authority.",
                ct
            );
        }
    }

    private static async Task ValidateSetlistTargetReuse(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistStructureChange structure,
        string kind,
        Guid target,
        SetlistStructureSource source,
        string key,
        NormalizationResourceKey? identity,
        bool reviewed,
        bool sharedTarget,
        CancellationToken ct
    )
    {
        if (reviewed)
        {
            return;
        }
        // Resolve the latest accepted decision before considering a new target. Otherwise a
        // new UUID plus a tombstone could silently replace a stable provider identity. Older
        // reviewed reallocations do not authorize returning to an obsolete target.
        var targets = (
            await c.QueryAsync<Guid>(
                new CommandDefinition(
                    """
                    WITH claims AS (
                        SELECT link.target_uuid,r.revision FROM normalization.setlist_structure_correspondences link
                        JOIN normalization.music_revisions r ON r.uuid=link.revision_uuid
                        WHERE link.edition_uuid=@edition AND link.kind=@kind
                        AND ((link.input_revision_uuid=@InputRevisionUuid AND link.account_index=@AccountIndex AND link.source_key=@key
                                AND EXISTS(SELECT 1 FROM normalization.setlist_processing_attempts old_source
                                    JOIN normalization.setlist_processing_attempts new_source ON new_source.attempt_uuid=@ProposalAttemptUuid
                                    WHERE old_source.attempt_uuid=link.source_attempt_uuid AND old_source.processing_version=new_source.processing_version))
                            OR (@identity::jsonb IS NOT NULL AND link.provider_identity @> @identity::jsonb AND link.provider_identity=@identity::jsonb))
                    )
                    SELECT DISTINCT target_uuid FROM claims WHERE revision=(SELECT max(revision) FROM claims) LIMIT 2
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
            if (targets.Length != 1 || targets[0] != target)
            {
                throw new ArgumentException(
                    "The latest accepted source/provider correspondence requires its stable target UUID, or explicit reviewed reallocation."
                );
            }
            return;
        }
        var table = kind == "set" ? "setlist_sets" : "song_occurrences";
        var exists = await c.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                $"SELECT EXISTS(SELECT 1 FROM normalization.{table} WHERE uuid=@target)",
                new { target },
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
    }

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
        CancellationToken ct
    ) =>
        c.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO normalization.setlist_structure_correspondences(revision_uuid,source_index,source_attempt_uuid,input_revision_uuid,
                    account_index,kind,source_key,target_uuid,edition_uuid,provider_identity)
                VALUES(@revision,@sourceIndex,@ProposalAttemptUuid,@InputRevisionUuid,@AccountIndex,@kind,@key,@target,@edition,@identity::jsonb)
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
                    identity = identity == null ? null : JsonSerializer.Serialize(identity),
                },
                t,
                cancellationToken: ct
            )
        );
}
