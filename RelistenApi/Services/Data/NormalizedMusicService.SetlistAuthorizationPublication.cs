using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedMusicService
{
    private static async Task<Guid?> ReadDeclaredCorrespondenceAuthorization(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid edition,
        SetlistStructureSource source,
        SetlistClaimReference claim,
        IReadOnlyList<Guid> targets,
        Guid? declared,
        CancellationToken ct
    )
    {
        var head = await ReadSetlistAuthorizationHead(c, t, edition, ClaimKey(source, claim), ct);
        if (head == null)
        {
            // A different exact source can carry provenance only through the existing stable
            // claim/provider-identity proof. The caller verifies that history before accepting it.
            return null;
        }
        var approval = JsonSerializer.Deserialize<SetlistCorrespondenceApproval>(head.Payload)!;
        if (
            head.Action != "authorize"
            || head.AuthorizationUuid != declared
            || !approval.Targets.Select(target => target.Uuid).SequenceEqual(targets)
        )
        {
            throw new NormalizationConflictException(
                "The active correspondence approval, block or revocation requires a new exact authorization decision."
            );
        }
        foreach (var target in approval.Targets)
        {
            var consumed = await c.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    """
                    SELECT EXISTS(SELECT 1 FROM normalization.setlist_structures s
                        JOIN normalization.setlist_structure_correspondences link ON link.revision_uuid=s.revision_uuid
                        WHERE s.edition_uuid=@edition AND link.authorization_uuid=@declared AND link.target_uuid=@Uuid)
                    """,
                    new
                    {
                        edition,
                        declared,
                        target.Uuid,
                    },
                    t,
                    cancellationToken: ct
                )
            );
            if (!consumed)
            {
                await ValidateAuthorizationTarget(
                    c,
                    t,
                    edition,
                    claim.SetIndex.HasValue
                        ? SetlistRemovalKind.Set
                        : SetlistRemovalKind.Occurrence,
                    target,
                    ct
                );
            }
        }
        return head.AuthorizationUuid;
    }

    private static Task<SetlistAuthorizationRow?> ReadSetlistAuthorizationHead(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid edition,
        string key,
        CancellationToken ct
    ) =>
        c.QuerySingleOrDefaultAsync<SetlistAuthorizationRow>(
            new CommandDefinition(
                """
                SELECT a.uuid AS AuthorizationUuid,a.edition_uuid AS EditionUuid,a.operation_uuid AS OperationUuid,
                    a.authorization_key AS AuthorizationKey,a.kind AS Kind,a.action AS Action,a.payload::text AS Payload,
                    a.source_tickets::text AS SourceTickets,a.created_at AS CreatedAt,true AS Active
                FROM normalization.setlist_authorization_heads h JOIN normalization.setlist_authorization_events a ON a.uuid=h.authorization_uuid
                WHERE h.edition_uuid=@edition AND h.authorization_key=@key
                """,
                new { edition, key },
                t,
                cancellationToken: ct
            )
        );

    private static async Task ValidateInheritedCorrespondenceAuthorization(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid edition,
        Guid authorization,
        IReadOnlyList<Guid> targets,
        CancellationToken ct
    )
    {
        var payload = await c.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(
                """
                SELECT a.payload::text FROM normalization.setlist_authorization_heads h
                    JOIN normalization.setlist_authorization_events a ON a.uuid=h.authorization_uuid
                    WHERE h.edition_uuid=@edition AND a.uuid=@authorization AND a.kind='correspondence' AND a.action='authorize'
                """,
                new { edition, authorization },
                t,
                cancellationToken: ct
            )
        );
        if (
            payload == null
            || !JsonSerializer
                .Deserialize<SetlistCorrespondenceApproval>(payload)!
                .Targets.Select(target => target.Uuid)
                .SequenceEqual(targets)
        )
        {
            throw new NormalizationConflictException(
                "An inherited correspondence approval was blocked, revoked or superseded; accepted history cannot restore it."
            );
        }
    }

    private static async Task ValidateStructureRemovalAuthorizations(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistStructureChange structure,
        bool reviewed,
        CancellationToken ct
    )
    {
        var references = structure.RemovalAuthorizations ?? [];
        if (
            references.Count > 100
            || references.Select(item => (item.Kind, item.TargetUuid)).Distinct().Count()
                != references.Count
        )
        {
            throw new ArgumentException(
                "Removal authorization references must be bounded and unique per target."
            );
        }
        foreach (var reference in references)
        {
            if (!StructureRetracts(structure, reference.Kind, reference.TargetUuid))
            {
                throw new ArgumentException(
                    "Removal authorization references must name explicit tombstones in this image."
                );
            }
            await ValidateRemovalAuthorization(c, t, structure, reference, ct);
        }
        if (reviewed)
        {
            return;
        }
        var targets = structure
            .Sets.Where(set => set.Retracted)
            .Select(set => (Kind: SetlistRemovalKind.Set, set.Uuid))
            .Concat(
                structure
                    .Occurrences.Where(item => item.Retracted)
                    .Select(item => (Kind: SetlistRemovalKind.Occurrence, item.Uuid))
            );
        if (structure.Edition.Retracted)
        {
            targets = targets.Append((SetlistRemovalKind.Edition, structure.Edition.Uuid));
        }
        foreach (var target in targets)
        {
            var table = RemovalKind(target.Kind) switch
            {
                "set" => "setlist_sets",
                "occurrence" => "song_occurrences",
                _ => "setlist_editions",
            };
            var active = await c.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    $"SELECT EXISTS(SELECT 1 FROM normalization.{table} WHERE uuid=@Uuid AND NOT retracted)",
                    new { target.Uuid },
                    t,
                    cancellationToken: ct
                )
            );
            if (
                active
                && !references.Any(reference =>
                    reference.Kind == target.Kind && reference.TargetUuid == target.Uuid
                )
            )
            {
                throw new ArgumentException(
                    "Retiring accepted sets or occurrences requires explicit reviewed removal authority."
                );
            }
        }
    }

    private static bool StructureRetracts(
        SetlistStructureChange structure,
        SetlistRemovalKind kind,
        Guid uuid
    ) =>
        kind switch
        {
            SetlistRemovalKind.Set => structure.Sets.Any(set => set.Uuid == uuid && set.Retracted),
            SetlistRemovalKind.Occurrence => structure.Occurrences.Any(item =>
                item.Uuid == uuid && item.Retracted
            ),
            SetlistRemovalKind.Edition => structure.Edition.Uuid == uuid
                && structure.Edition.Retracted,
            _ => false,
        };

    private static async Task ValidateRemovalAuthorization(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistStructureChange structure,
        SetlistRemovalAuthorization reference,
        CancellationToken ct
    )
    {
        var head = await ReadSetlistAuthorizationHead(
            c,
            t,
            structure.Edition.Uuid,
            RemovalKey(reference.Kind, reference.TargetUuid),
            ct
        );
        if (
            head == null
            || head.AuthorizationUuid != reference.AuthorizationUuid
            || head.Action != "authorize"
        )
        {
            throw new NormalizationConflictException(
                "The exact removal authorization is missing, blocked, revoked or superseded."
            );
        }
        var approval = JsonSerializer.Deserialize<SetlistRemovalApproval>(head.Payload)!;
        if (
            approval.Sources.Any(source =>
                !structure.Sources.Any(actual => SameStructureJson(source, actual))
            )
        )
        {
            throw new NormalizationConflictException(
                "Removal approval cannot transfer to another source proposal or account."
            );
        }
        // Removal is revision-specific. Retaining a consumed tombstone is harmless, but an old
        // approval cannot delete a target that was subsequently reactivated or changed.
        var table = RemovalKind(reference.Kind) switch
        {
            "set" => "setlist_sets",
            "occurrence" => "song_occurrences",
            _ => "setlist_editions",
        };
        var current = await c.QuerySingleAsync<RemovalTargetRow>(
            new CommandDefinition(
                $"SELECT r.revision AS Revision,x.retracted AS Retracted FROM normalization.{table} x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid WHERE x.uuid=@uuid",
                new { uuid = reference.TargetUuid },
                t,
                cancellationToken: ct
            )
        );
        var retainedConsumption =
            current.Retracted
            && await c.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    """
                    SELECT EXISTS(SELECT 1 FROM normalization.setlist_removal_authorizations_used u
                        JOIN normalization.setlist_structures s ON s.revision_uuid=u.revision_uuid
                        WHERE s.edition_uuid=@edition AND u.authorization_uuid=@AuthorizationUuid AND u.kind=@kind AND u.target_uuid=@TargetUuid)
                    """,
                    new
                    {
                        edition = structure.Edition.Uuid,
                        reference.AuthorizationUuid,
                        reference.TargetUuid,
                        kind = RemovalKind(reference.Kind),
                    },
                    t,
                    cancellationToken: ct
                )
            );
        if (!retainedConsumption && current.Revision != approval.Target.ExpectedRevision)
        {
            throw new NormalizationConflictException(
                "The removal target changed after its exact revision was approved."
            );
        }
    }

    private static async Task AppendStructureRemovalAuthority(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistStructureChange structure,
        Guid revision,
        CancellationToken ct
    )
    {
        foreach (var reference in structure.RemovalAuthorizations ?? [])
        {
            await c.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO normalization.setlist_removal_authorizations_used(revision_uuid,authorization_uuid,kind,target_uuid)
                    VALUES(@revision,@AuthorizationUuid,@kind,@TargetUuid)
                    """,
                    new
                    {
                        revision,
                        reference.AuthorizationUuid,
                        kind = RemovalKind(reference.Kind),
                        reference.TargetUuid,
                    },
                    t,
                    cancellationToken: ct
                )
            );
        }
    }

    private sealed record RemovalTargetRow(long Revision, bool Retracted);
}
