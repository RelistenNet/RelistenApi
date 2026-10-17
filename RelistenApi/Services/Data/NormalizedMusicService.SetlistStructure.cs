using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedMusicService
{
    private const int MaximumStructureBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Publishes a complete edition image under its existing setlist owner. Allocate new target
    /// UUIDs before admission and retain the exact image on retry. Sources carry original proposal
    /// receipts; this method never substitutes a fresh receipt or infers passage correspondence.
    /// </summary>
    public Task<NormalizationPublicationResult> PublishSetlistStructureAsync(
        Guid workUuid,
        SetlistStructureChange structure,
        CancellationToken cancellationToken = default
    ) => PublishAsync(workUuid, StructurePublication(structure), cancellationToken);

    /// <summary>
    /// Reviews the entire replacement image, including edition metadata and every supplied
    /// occurrence. Those reviewed rows block later automatic replacement. Correspondence-only
    /// authorization requires a separate command; this API does not provide that narrower authority.
    /// </summary>
    public Task<NormalizationReviewResult> ApplySetlistStructureDecisionAsync(
        Guid operationUuid,
        Guid subjectUuid,
        long expectedEditorialRevision,
        SetlistStructureChange structure,
        string reason,
        string trustedOperator,
        CancellationToken cancellationToken = default
    ) =>
        ApplyDecisionAsync(
            operationUuid,
            subjectUuid,
            expectedEditorialRevision,
            structure.Retracted ? "retract" : "replace",
            StructurePublication(structure),
            reason,
            trustedOperator,
            cancellationToken
        );

    /// <summary>Returns the accepted complete image, retaining separate original source claims and unresolved passages.</summary>
    public Task<MusicRecord<SetlistStructureChange>?> GetSetlistStructureAsync(
        Guid editionUuid,
        CancellationToken cancellationToken = default
    ) => GetStructureRecord(editionUuid, cancellationToken);

    private static MusicPublication FreezeStructurePublication(MusicPublication publication)
    {
        if (publication.SetlistStructure == null)
        {
            return publication;
        }
        // Generic public routes need the same synchronous snapshot as the convenience wrappers.
        // After the first await no caller-owned collection may change the accepted image or audit.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(publication);
        if (bytes.Length > 2 * MaximumStructureBytes)
        {
            throw new ArgumentException("The structure-bearing publication exceeds 32 MiB.");
        }
        var frozen = JsonSerializer.Deserialize<MusicPublication>(bytes)!;
        if (
            JsonSerializer.SerializeToUtf8Bytes(frozen.SetlistStructure).Length
            > MaximumStructureBytes
        )
        {
            throw new ArgumentException("The complete setlist structure exceeds 16 MiB.");
        }
        return frozen;
    }

    private static SetlistStructureChange FreezeStructure(SetlistStructureChange structure)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(structure);
        if (bytes.Length > MaximumStructureBytes)
        {
            throw new ArgumentException("The complete setlist structure exceeds 16 MiB.");
        }
        return JsonSerializer.Deserialize<SetlistStructureChange>(bytes)!;
    }

    private static MusicPublication StructurePublication(SetlistStructureChange structure) =>
        new()
        {
            Editions = [structure.Edition],
            Occurrences = structure.Occurrences,
            SetlistStructure = structure,
        };

    private static bool SameStructureJson<T>(T first, T second) =>
        JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(first),
            JsonSerializer.SerializeToNode(second)
        );

    private static async Task ValidateStructureRoute(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        MusicPublication publication,
        bool reviewed,
        CancellationToken ct
    )
    {
        if (publication.SetlistStructure is not { } structure)
        {
            var editions = publication
                .Editions.Select(edition => edition.Uuid)
                .Concat(publication.Occurrences.Select(occurrence => occurrence.EditionUuid))
                .Distinct()
                .ToArray();
            await Require(
                c,
                t,
                "SELECT NOT EXISTS(SELECT 1 FROM normalization.setlist_structures WHERE edition_uuid=ANY(@editions))",
                new { editions },
                "A structure-managed edition requires a complete structure publication.",
                ct
            );
            return;
        }
        ValidateStructureShape(publication, structure);
        ValidateStructureMembership(structure);
        await ValidatePriorStructure(c, t, structure, ct);
        await NormalizedPeopleService.ValidateEvidenceAsync(
            c,
            t,
            structure.Sets.SelectMany(set => set.Evidence).ToArray(),
            ct
        );
        await ValidateStructureSources(c, t, structure, reviewed, ct);
        await ValidateStructureCorrespondence(c, t, structure, reviewed, ct);
    }

    private static void ValidateStructureShape(
        MusicPublication publication,
        SetlistStructureChange structure
    )
    {
        if (
            publication.Editions.Count != 1
            || !SameStructureJson(publication.Editions[0], structure.Edition)
            || !SameStructureJson(publication.Occurrences, structure.Occurrences)
            || structure.Sources.Count is 0 or > 100
            || structure.Sets.Count > 10000
            || structure.Occurrences.Count > 10000
        )
        {
            throw new ArgumentException(
                "A structure publication requires its exact complete edition, sets, passages and bounded sources."
            );
        }
        if (
            structure.Retracted != structure.Edition.Retracted
            || (
                structure.Retracted
                && (
                    structure.Sets.Any(set => !set.Retracted)
                    || structure.Occurrences.Any(occurrence => !occurrence.Retracted)
                )
            )
        )
        {
            throw new ArgumentException(
                "Withdrawing an edition requires explicit set and occurrence tombstones in the same image."
            );
        }
        var allIds = new[] { structure.Uuid, structure.Edition.Uuid }
            .Concat(structure.Sets.Select(set => set.Uuid))
            .Concat(structure.Occurrences.Select(occurrence => occurrence.Uuid))
            .ToArray();
        if (allIds.Any(id => id == Guid.Empty) || allIds.Distinct().Count() != allIds.Length)
        {
            throw new ArgumentException(
                "Structure, edition, set and occurrence UUIDs must be nonempty and distinct."
            );
        }
        if (
            structure.Sets.Any(set =>
                set.Position is <= 0
                || set.EncoreNumber is <= 0
                || (!structure.Edition.Ordered && set.Position.HasValue)
                || set.Evidence.Count == 0
            )
            || structure.Occurrences.Any(occurrence =>
                occurrence.EditionUuid != structure.Edition.Uuid
            )
        )
        {
            throw new ArgumentException(
                "Sets and passages require valid ordering, evidence and matching edition ownership."
            );
        }
        var setPositions = structure
            .Sets.Where(set => !set.Retracted && set.Position.HasValue)
            .Select(set => set.Position)
            .ToArray();
        var rootPositions = structure
            .Occurrences.Where(occurrence =>
                !occurrence.Retracted
                && occurrence.ParentOccurrenceUuid == null
                && occurrence.Position.HasValue
            )
            .Select(occurrence => occurrence.Position)
            .ToArray();
        if (
            setPositions.Distinct().Count() != setPositions.Length
            || rootPositions.Distinct().Count() != rootPositions.Length
        )
        {
            throw new ArgumentException(
                "Active sets and root occurrences require distinct known positions."
            );
        }
    }

    private static void ValidateStructureMembership(SetlistStructureChange structure)
    {
        foreach (
            var occurrence in structure.Occurrences.Where(occurrence =>
                !occurrence.Retracted && occurrence.SetUuid.HasValue
            )
        )
        {
            if (!structure.Sets.Any(set => set.Uuid == occurrence.SetUuid && !set.Retracted))
            {
                throw new ArgumentException(
                    "An active occurrence needs an active set in this edition, or unknown set membership."
                );
            }
        }
        foreach (
            var component in structure.Occurrences.Where(occurrence =>
                !occurrence.Retracted && occurrence.ParentOccurrenceUuid.HasValue
            )
        )
        {
            var parent = structure.Occurrences.SingleOrDefault(occurrence =>
                occurrence.Uuid == component.ParentOccurrenceUuid
            );
            if (
                parent == null
                || parent.Retracted
                || parent.ParentOccurrenceUuid.HasValue
                || (
                    parent.SetUuid.HasValue
                    && component.SetUuid.HasValue
                    && parent.SetUuid != component.SetUuid
                )
            )
            {
                throw new ArgumentException(
                    "An active component needs an active root parent and cannot contradict its known set membership."
                );
            }
        }
    }

    private static async Task ValidatePriorStructure(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistStructureChange structure,
        CancellationToken ct
    )
    {
        await Require(
            c,
            t,
            "SELECT NOT EXISTS(SELECT 1 FROM normalization.setlist_editions WHERE uuid=@Uuid AND (provider<>@Provider OR resource_type<>@ResourceType OR provider_key<>@ProviderKey))",
            structure.Edition,
            "An accepted edition's qualified account identity is immutable; create another edition for another account.",
            ct
        );
        await Require(
            c,
            t,
            "SELECT NOT EXISTS(SELECT 1 FROM normalization.setlist_structures WHERE (uuid=@Uuid AND edition_uuid<>@edition) OR (edition_uuid=@edition AND uuid<>@Uuid))",
            new { structure.Uuid, edition = structure.Edition.Uuid },
            "A structure identity cannot move to another edition or replace its stable structure UUID.",
            ct
        );
        await Require(
            c,
            t,
            "SELECT NOT EXISTS(SELECT 1 FROM normalization.setlist_sets WHERE uuid=ANY(@ids) AND edition_uuid<>@edition)",
            new
            {
                ids = structure.Sets.Select(set => set.Uuid).ToArray(),
                edition = structure.Edition.Uuid,
            },
            "A set identity cannot move to another edition.",
            ct
        );
        await Require(
            c,
            t,
            "SELECT NOT EXISTS(SELECT 1 FROM normalization.setlist_sets WHERE edition_uuid=@edition AND NOT retracted AND NOT uuid=ANY(@ids))",
            new
            {
                edition = structure.Edition.Uuid,
                ids = structure.Sets.Select(set => set.Uuid).ToArray(),
            },
            "Every removed set requires an explicit tombstone.",
            ct
        );
        await Require(
            c,
            t,
            "SELECT NOT EXISTS(SELECT 1 FROM normalization.song_occurrences WHERE edition_uuid=@edition AND NOT retracted AND NOT uuid=ANY(@ids))",
            new
            {
                edition = structure.Edition.Uuid,
                ids = structure.Occurrences.Select(occurrence => occurrence.Uuid).ToArray(),
            },
            "Every removed occurrence requires an explicit tombstone.",
            ct
        );
        await Require(
            c,
            t,
            "SELECT NOT EXISTS(SELECT 1 FROM unnest(@ids::uuid[]) id WHERE NOT EXISTS(SELECT 1 FROM normalization.setlist_sets WHERE uuid=id AND edition_uuid=@edition))",
            new
            {
                ids = structure.Sets.Where(set => set.Retracted).Select(set => set.Uuid).ToArray(),
                edition = structure.Edition.Uuid,
            },
            "A set tombstone must refer to a previously accepted set in this edition.",
            ct
        );
        await Require(
            c,
            t,
            "SELECT NOT EXISTS(SELECT 1 FROM unnest(@ids::uuid[]) id WHERE NOT EXISTS(SELECT 1 FROM normalization.song_occurrences WHERE uuid=id AND edition_uuid=@edition))",
            new
            {
                ids = structure
                    .Occurrences.Where(occurrence => occurrence.Retracted)
                    .Select(occurrence => occurrence.Uuid)
                    .ToArray(),
                edition = structure.Edition.Uuid,
            },
            "An occurrence tombstone must refer to a previously accepted passage in this edition.",
            ct
        );
    }

    private static async Task ApplyStructure(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistStructureChange structure,
        Guid decision,
        bool reviewed,
        CancellationToken ct
    )
    {
        if (reviewed)
        {
            structure = structure with
            {
                Basis = "reviewed",
                Edition = structure.Edition with { Basis = "reviewed" },
                Occurrences = structure
                    .Occurrences.Select(occurrence => occurrence with { Basis = "reviewed" })
                    .ToArray(),
            };
        }
        if (!structure.Edition.Retracted)
        {
            await SaveEdition(c, t, structure.Edition, decision, reviewed, ct);
        }
        await Persist(
            c,
            t,
            "setlist_structures",
            structure,
            "edition_uuid",
            "@editionUuid",
            decision,
            reviewed,
            ct,
            new { editionUuid = structure.Edition.Uuid }
        );
        var revision = await c.QuerySingleAsync<Guid>(
            new CommandDefinition(
                "SELECT revision_uuid FROM normalization.setlist_structures WHERE uuid=@Uuid",
                structure,
                t,
                cancellationToken: ct
            )
        );
        foreach (var set in structure.Sets)
        {
            await c.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO normalization.setlist_sets(uuid,edition_uuid,position,label,encore_number,provider_identity,retracted,revision_uuid)
                    VALUES(@Uuid,@edition,@Position,@Label,@EncoreNumber,@providerIdentity::jsonb,@Retracted,@revision)
                    ON CONFLICT(uuid) DO UPDATE SET position=EXCLUDED.position,label=EXCLUDED.label,encore_number=EXCLUDED.encore_number,
                        provider_identity=EXCLUDED.provider_identity,retracted=EXCLUDED.retracted,revision_uuid=EXCLUDED.revision_uuid
                    """,
                    new
                    {
                        set.Uuid,
                        edition = structure.Edition.Uuid,
                        set.Position,
                        set.Label,
                        set.EncoreNumber,
                        providerIdentity = set.ProviderIdentity == null
                            ? null
                            : JsonSerializer.Serialize(set.ProviderIdentity),
                        set.Retracted,
                        revision,
                    },
                    t,
                    cancellationToken: ct
                )
            );
        }
        foreach (
            var occurrence in structure.Occurrences.OrderBy(occurrence =>
                occurrence.ParentOccurrenceUuid.HasValue
            )
        )
        {
            await SaveOccurrence(c, t, occurrence, decision, reviewed, ct);
        }
        if (structure.Edition.Retracted)
        {
            await SaveEdition(c, t, structure.Edition, decision, reviewed, ct);
        }
        await AppendStructureCorrespondence(c, t, structure, revision, ct);
    }
}
