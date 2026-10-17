using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

// Catalog assignments keep their identity across repeated automatic proposals. Reviewed
// decisions remain authoritative; added exact citations do not create a new claim revision.
public sealed partial class NormalizedVenueService
{
    private static Task WriteAutomaticAssignment(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        VenueAssignment assignment,
        bool isShow,
        Guid workUuid,
        CancellationToken ct
    )
    {
        if (assignment.Basis == "reviewed")
        {
            throw new ArgumentException("Automatic publication cannot claim reviewed authority.");
        }
        return WriteAssignment(c, t, assignment, isShow, workUuid, false, 0, ct);
    }

    private static async Task WriteAssignment(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        VenueAssignment assignment,
        bool isShow,
        Guid decisionUuid,
        bool reviewed,
        long expectedRevision,
        CancellationToken ct
    )
    {
        if (assignment.Evidence.Count == 0)
        {
            throw new ArgumentException("Venue assignments require evidence.");
        }
        assignment = assignment with
        {
            VenueUuid = await ResolveVenueUuidAsync(c, t, assignment.VenueUuid, ct),
        };
        // Only these two typed relations are selected here; callers never supply table or column names.
        var table = isShow ? "show_venue_assignments" : "imported_venue_assignments";
        var catalogColumn = isShow ? "show_uuid" : "imported_venue_uuid";
        var evidenceColumn = isShow ? "show_assignment_uuid" : "imported_assignment_uuid";
        await c.ExecuteAsync(
            new CommandDefinition(
                "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",
                new { key = $"normalization:{table}:{assignment.CatalogUuid}" },
                t,
                cancellationToken: ct
            )
        );
        var current = await c.QuerySingleOrDefaultAsync<AssignmentRow>(
            new CommandDefinition(
                $"SELECT uuid AS \"Uuid\",{catalogColumn} AS \"CatalogUuid\",revision AS \"Revision\",basis AS \"Basis\",state AS \"State\",canonical_venue_uuid AS \"VenueUuid\",reason_code AS \"ReasonCode\" FROM normalization.{table} WHERE uuid=@Uuid FOR UPDATE",
                assignment,
                t,
                cancellationToken: ct
            )
        );
        if (current != null && current.CatalogUuid != assignment.CatalogUuid)
        {
            throw new NormalizationConflictException(
                "An assignment UUID cannot be rebound to a different catalog object."
            );
        }
        var hasCallerAssignment = current != null;

        if (!reviewed)
        {
            var prior = await c.QuerySingleOrDefaultAsync<AssignmentRow>(
                new CommandDefinition(
                    $"""
                    SELECT a.uuid AS "Uuid",a.{catalogColumn} AS "CatalogUuid",a.revision AS "Revision",
                        a.basis AS "Basis",a.state AS "State",a.canonical_venue_uuid AS "VenueUuid",a.reason_code AS "ReasonCode"
                    FROM normalization.{table} a JOIN normalization.venue_identity_resolution r ON r.original_uuid=a.canonical_venue_uuid
                    WHERE a.{catalogColumn}=@CatalogUuid AND r.resolved_uuid=@VenueUuid AND a.state<>'superseded'
                    ORDER BY (a.basis='reviewed') DESC,(a.state='accepted') DESC,a.uuid LIMIT 1 FOR UPDATE OF a
                    """,
                    assignment,
                    t,
                    cancellationToken: ct
                )
            );
            if (prior?.Basis == "reviewed" || current?.Basis == "reviewed")
            {
                prior ??= current;
                var sameEvidence = await c.ExecuteScalarAsync<bool>(
                    new CommandDefinition(
                        $"SELECT EXISTS(SELECT 1 FROM normalization.venue_assignment_evidence WHERE {evidenceColumn}=@uuid AND input_revision_uuid=ANY(@revisions))",
                        new
                        {
                            uuid = prior!.Uuid,
                            revisions = assignment
                                .Evidence.Select(e => e.InputRevisionUuid)
                                .ToArray(),
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                if (sameEvidence)
                {
                    return;
                }
                assignment = assignment with
                {
                    Uuid = Guid.NewGuid(),
                    State = "pending",
                    ReasonCode = "changed_evidence_reviewed_claim",
                };
                current = null;
            }
            else if (current == null && prior != null)
            {
                // A known UUID identifies the caller's correction target. Only fresh UUIDs
                // coalesce with an existing automatic claim for the same resolved venue.
                assignment = assignment with
                {
                    Uuid = prior.Uuid,
                };
                current = prior;
            }
        }
        if (reviewed && (current?.Revision ?? 0) != expectedRevision)
        {
            throw new NormalizationConflictException(
                "Venue assignment changed after review was prepared."
            );
        }
        if (current == null)
        {
            var catalogTable = isShow ? "shows" : "venues";
            if (
                !await c.ExecuteScalarAsync<bool>(
                    new CommandDefinition(
                        $"SELECT EXISTS(SELECT 1 FROM {catalogTable} WHERE uuid=@CatalogUuid)",
                        assignment,
                        t,
                        cancellationToken: ct
                    )
                )
            )
            {
                throw new ArgumentException("Venue assignment catalog reference is unavailable.");
            }
        }
        var accepted = await c.QuerySingleOrDefaultAsync<AssignmentRow>(
            new CommandDefinition(
                $"SELECT uuid AS \"Uuid\",{catalogColumn} AS \"CatalogUuid\",revision AS \"Revision\",basis AS \"Basis\",state AS \"State\",canonical_venue_uuid AS \"VenueUuid\",reason_code AS \"ReasonCode\" FROM normalization.{table} WHERE {catalogColumn}=@CatalogUuid AND state='accepted' AND uuid<>@Uuid FOR UPDATE",
                assignment,
                t,
                cancellationToken: ct
            )
        );
        if (accepted != null && assignment.State == "accepted")
        {
            if (reviewed)
            {
                // Superseding retains the old row and its evidence for inspection and replay.
                await c.ExecuteAsync(
                    new CommandDefinition(
                        $"INSERT INTO normalization.{table}_history SELECT *,@decisionUuid FROM normalization.{table} WHERE uuid=@uuid; UPDATE normalization.{table} SET state='superseded',revision=revision+1,updated_at=clock_timestamp() WHERE uuid=@uuid",
                        new { uuid = accepted.Uuid, decisionUuid },
                        t,
                        cancellationToken: ct
                    )
                );
            }
            else
            {
                if (
                    accepted.Basis == "reviewed"
                    && await c.ExecuteScalarAsync<bool>(
                        new CommandDefinition(
                            $"SELECT EXISTS(SELECT 1 FROM normalization.venue_assignment_evidence WHERE {evidenceColumn}=@uuid AND input_revision_uuid=ANY(@revisions))",
                            new
                            {
                                uuid = accepted.Uuid,
                                revisions = assignment
                                    .Evidence.Select(e => e.InputRevisionUuid)
                                    .ToArray(),
                            },
                            t,
                            cancellationToken: ct
                        )
                    )
                )
                {
                    return;
                }
                assignment = assignment with
                {
                    State = "pending",
                    ReasonCode = "conflicting_evidence",
                };
            }
        }
        if (!reviewed && assignment.State == "pending" && (!hasCallerAssignment || current == null))
        {
            // Reviewed/conflicting claims may already have an equivalent pending proposal.
            // Reuse its identity; a new citation is support for that proposal, not a new claim.
            var pending = await c.QuerySingleOrDefaultAsync<AssignmentRow>(
                new CommandDefinition(
                    $"""
                    SELECT a.uuid AS "Uuid",a.{catalogColumn} AS "CatalogUuid",a.revision AS "Revision",
                        a.basis AS "Basis",a.state AS "State",a.canonical_venue_uuid AS "VenueUuid",a.reason_code AS "ReasonCode"
                    FROM normalization.{table} a JOIN normalization.venue_identity_resolution r ON r.original_uuid=a.canonical_venue_uuid
                    WHERE a.{catalogColumn}=@CatalogUuid AND r.resolved_uuid=@VenueUuid AND a.state='pending'
                        AND a.basis=@Basis AND a.reason_code=@ReasonCode
                    ORDER BY a.uuid LIMIT 1 FOR UPDATE OF a
                    """,
                    assignment,
                    t,
                    cancellationToken: ct
                )
            );
            if (pending != null)
            {
                assignment = assignment with { Uuid = pending.Uuid };
                current = pending;
            }
        }
        if (
            !reviewed
            && current != null
            && current.CatalogUuid == assignment.CatalogUuid
            && await ResolveVenueUuidAsync(c, t, current.VenueUuid, ct) == assignment.VenueUuid
            && current.State == assignment.State
            && current.Basis == assignment.Basis
            && current.ReasonCode == assignment.ReasonCode
        )
        {
            // Fresh work UUIDs and extractor-generated assertion UUIDs do not change meaning.
            // Keep the accepted revision/history stable; evidence inserts carry their own fence.
            await AppendAssignmentEvidence(
                c,
                t,
                table,
                evidenceColumn,
                current.Uuid,
                assignment.Evidence,
                ct
            );
            return;
        }
        if (current != null)
        {
            await c.ExecuteAsync(
                new CommandDefinition(
                    $"INSERT INTO normalization.{table}_history SELECT *,@decisionUuid FROM normalization.{table} WHERE uuid=@Uuid",
                    new { assignment.Uuid, decisionUuid },
                    t,
                    cancellationToken: ct
                )
            );
        }
        await c.ExecuteAsync(
            new CommandDefinition(
                $"""
                INSERT INTO normalization.{table}(uuid,{catalogColumn},canonical_venue_uuid,state,basis,reason_code,decision_uuid)
                VALUES(@Uuid,@CatalogUuid,@VenueUuid,@State,@Basis,@ReasonCode,@decisionUuid)
                ON CONFLICT(uuid) DO UPDATE SET canonical_venue_uuid=EXCLUDED.canonical_venue_uuid,state=EXCLUDED.state,basis=EXCLUDED.basis,reason_code=EXCLUDED.reason_code,
                    decision_uuid=EXCLUDED.decision_uuid,revision={table}.revision+1,updated_at=clock_timestamp()
                """,
                new
                {
                    assignment.Uuid,
                    assignment.CatalogUuid,
                    assignment.VenueUuid,
                    assignment.State,
                    assignment.Basis,
                    assignment.ReasonCode,
                    decisionUuid,
                },
                t,
                cancellationToken: ct
            )
        );
        await AppendAssignmentEvidence(
            c,
            t,
            table,
            evidenceColumn,
            assignment.Uuid,
            assignment.Evidence,
            ct
        );
    }

    private static async Task AppendAssignmentEvidence(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        string table,
        string evidenceColumn,
        Guid assignmentUuid,
        IReadOnlyList<NormalizedEvidenceReference> evidence,
        CancellationToken ct
    )
    {
        foreach (var citation in evidence.Distinct())
        {
            await c.ExecuteAsync(
                new CommandDefinition(
                    $"""
                    INSERT INTO normalization.venue_assignment_evidence({evidenceColumn},input_revision_uuid,json_pointer,start_code_point,end_code_point,quote,assignment_revision)
                    SELECT @assignmentUuid,@InputRevisionUuid,@JsonPointer,@StartCodePoint,@EndCodePoint,@Quote,a.revision
                    FROM normalization.{table} a WHERE a.uuid=@assignmentUuid AND NOT EXISTS(
                        SELECT 1 FROM normalization.venue_assignment_evidence e WHERE e.{evidenceColumn}=a.uuid AND e.assignment_revision=a.revision
                            AND e.input_revision_uuid=@InputRevisionUuid AND e.json_pointer=@JsonPointer
                            AND e.start_code_point IS NOT DISTINCT FROM @StartCodePoint::integer
                            AND e.end_code_point IS NOT DISTINCT FROM @EndCodePoint::integer
                            AND e.quote IS NOT DISTINCT FROM @Quote::text)
                    """,
                    new
                    {
                        assignmentUuid,
                        citation.InputRevisionUuid,
                        citation.JsonPointer,
                        citation.StartCodePoint,
                        citation.EndCodePoint,
                        citation.Quote,
                    },
                    t,
                    cancellationToken: ct
                )
            );
        }
    }

    private sealed class AssignmentRow
    {
        public Guid Uuid { get; init; }
        public Guid CatalogUuid { get; init; }
        public long Revision { get; init; }
        public Guid VenueUuid { get; init; }
        public string Basis { get; init; } = null!;
        public string State { get; init; } = null!;
        public string ReasonCode { get; init; } = null!;
    }
}
