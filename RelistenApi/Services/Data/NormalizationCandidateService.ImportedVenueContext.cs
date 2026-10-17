using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    public const string ImportedVenueContextVersion = "imported-venue-resolution-context-v1";

    /// <summary>
    /// Reads all stored imported assignments, historical reviewed rejections and complete
    /// current-revision citations with global candidate facts. Assigned/rejected targets are
    /// read even when omitted by name recall. Assignment or citation bounds fail the whole
    /// call; ordinary candidate/fact bounds retain their explicit blocking issues. No inputs,
    /// owners, accepted facts or clocks are created or refreshed.
    /// </summary>
    public async Task<NormalizationImportedVenueResolutionContext> FindImportedVenueResolutionContextAsync(
        Guid importedVenueUuid,
        NormalizationCandidateQuery query,
        CandidateFactsLimits? limits = null,
        int maximumAssignments = 1000,
        CancellationToken cancellationToken = default
    )
    {
        if (importedVenueUuid == Guid.Empty || query.Domain != IdentityMatchingDomain.Venues)
        {
            throw new ArgumentException(
                "Imported venue resolution requires a stable catalog UUID and a global venue query."
            );
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAssignments, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumAssignments, 10_000);
        var frozen = FreezeFactsQuery(query);
        var names = ValidateQuery(frozen);
        var bounds = ValidateFactsLimits(limits ?? new());

        await using var connection = db.CreateConnection(false, false);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken
        );
        await connection.ExecuteAsync(
            new CommandDefinition(
                "SET TRANSACTION READ ONLY",
                transaction: transaction,
                cancellationToken: cancellationToken
            )
        );
        var context = new CandidateFactsContext();
        var owner = await ReadImportedAssignmentOwner(
            connection,
            transaction,
            importedVenueUuid,
            cancellationToken
        );
        context.AddOwner(owner?.SubjectUuid);
        var assignments = await ReadImportedAssignmentFacts(
            connection,
            transaction,
            importedVenueUuid,
            maximumAssignments,
            bounds.MaximumEvidencePerAssertion,
            context,
            cancellationToken
        );
        var selection = await FindInSnapshotAsync(
            connection,
            transaction,
            frozen,
            names,
            cancellationToken
        );
        var selected = selection;
        if (selection.Items.Count > bounds.MaximumCandidates)
        {
            context.AddIssue(CandidateFactsIssueKind.CandidateLimit);
            selected = selection with
            {
                Items = selection.Items.Take(bounds.MaximumCandidates).ToArray(),
            };
        }
        var facts = await ReadVenueFactsAsync(
            connection,
            transaction,
            selected,
            bounds,
            context,
            cancellationToken
        );
        var targets = await ReadImportedAssignmentTargets(
            connection,
            transaction,
            assignments,
            facts,
            bounds,
            context,
            cancellationToken
        );
        var universes = await ReadFactsUniversesAsync(
            connection,
            transaction,
            frozen,
            cancellationToken
        );
        var readSet = await context.ReadReadSetAsync(
            connection,
            transaction,
            universes,
            cancellationToken
        );
        var candidates = new NormalizationCandidateFactsSnapshot<VenueCandidateFacts>(
            FactsVersion,
            FactsQueryHash(frozen, bounds),
            frozen,
            bounds,
            selection,
            facts,
            readSet,
            context.Issues
        );
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        version = ImportedVenueContextVersion,
                        importedVenueUuid,
                        queryHash = candidates.QueryHash,
                        maximumAssignments,
                    }
                )
            )
        );
        await transaction.CommitAsync(cancellationToken);
        return new(
            ImportedVenueContextVersion,
            hash,
            importedVenueUuid,
            owner,
            assignments,
            targets,
            candidates
        );
    }

    private static async Task<NormalizationSubjectState?> ReadImportedAssignmentOwner(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid importedVenueUuid,
        CancellationToken ct
    )
    {
        var row = await c.QuerySingleOrDefaultAsync<ImportedAssignmentOwner>(
            new CommandDefinition(
                """
                SELECT uuid AS "SubjectUuid",desired_generation AS "DesiredGeneration",editorial_revision AS "EditorialRevision",
                    policy_version AS "PolicyVersion",published_generation AS "PublishedGeneration",published_work_uuid AS "PublishedWorkUuid"
                FROM normalization.work_subjects WHERE domain='venues' AND subject_kind='imported_venue' AND subject_uuid=@importedVenueUuid
                """,
                new { importedVenueUuid },
                t,
                cancellationToken: ct
            )
        );
        return row == null
            ? null
            : new(
                row.SubjectUuid,
                new("venues", "imported_venue", importedVenueUuid),
                row.DesiredGeneration,
                row.EditorialRevision,
                row.PolicyVersion,
                row.PublishedGeneration,
                row.PublishedWorkUuid
            );
    }

    private static async Task<ImportedVenueAssignmentFact[]> ReadImportedAssignmentFacts(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid importedVenueUuid,
        int maximumAssignments,
        int maximumEvidence,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        var rows = (
            await c.QueryAsync<ImportedAssignmentRow>(
                new CommandDefinition(
                    """
                    WITH assignments AS (
                        SELECT uuid,revision,canonical_venue_uuid,state,basis,reason_code,decision_uuid,
                            false AS historical,NULL::uuid AS superseding_decision_uuid
                        FROM normalization.imported_venue_assignments WHERE imported_venue_uuid=@importedVenueUuid
                        UNION ALL
                        SELECT uuid,revision,canonical_venue_uuid,state,basis,reason_code,decision_uuid,
                            true AS historical,superseding_decision_uuid
                        FROM normalization.imported_venue_assignments_history
                        WHERE imported_venue_uuid=@importedVenueUuid AND state='rejected' AND basis='reviewed'
                    )
                    SELECT a.uuid AS "AssignmentUuid",a.revision AS "Revision",a.canonical_venue_uuid AS "StoredVenueUuid",
                        r.resolved_uuid AS "ResolvedVenueUuid",a.state AS "State",a.basis AS "Basis",a.reason_code AS "ReasonCode",
                        a.decision_uuid AS "DecisionUuid",a.historical AS "IsHistorical",a.superseding_decision_uuid AS "SupersedingDecisionUuid"
                    FROM assignments a LEFT JOIN normalization.venue_identity_resolution r ON r.original_uuid=a.canonical_venue_uuid
                    ORDER BY a.uuid,a.revision,a.historical LIMIT @take
                    """,
                    new { importedVenueUuid, take = maximumAssignments + 1 },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (rows.Length > maximumAssignments)
        {
            throw new InvalidOperationException(
                "Imported venue resolution exceeds its assignment and reviewed-rejection history limit."
            );
        }
        var evidence = await ReadImportedAssignmentEvidence(c, t, rows, maximumEvidence, ct);
        return rows.Select(row =>
            {
                if (row.ResolvedVenueUuid == null)
                {
                    context.AddIssue(
                        CandidateFactsIssueKind.MissingRelatedIdentity,
                        row.StoredVenueUuid,
                        "assignment_target"
                    );
                }
                if (evidence[(row.AssignmentUuid, row.Revision)].Length == 0)
                {
                    context.AddIssue(
                        CandidateFactsIssueKind.InvalidStoredFact,
                        row.StoredVenueUuid,
                        "assignment_evidence"
                    );
                }
                return new ImportedVenueAssignmentFact(
                    row.AssignmentUuid,
                    row.Revision,
                    row.StoredVenueUuid,
                    row.ResolvedVenueUuid,
                    row.State,
                    row.Basis,
                    row.ReasonCode,
                    row.DecisionUuid,
                    row.IsHistorical,
                    row.SupersedingDecisionUuid,
                    evidence[(row.AssignmentUuid, row.Revision)]
                );
            })
            .ToArray();
    }

    private static async Task<
        Dictionary<(Guid Uuid, long Revision), NormalizedEvidenceReference[]>
    > ReadImportedAssignmentEvidence(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        ImportedAssignmentRow[] rows,
        int maximumEvidence,
        CancellationToken ct
    )
    {
        var revisions = rows.Select(row => (row.AssignmentUuid, row.Revision)).Distinct().ToArray();
        var evidence = (
            await c.QueryAsync<ImportedAssignmentEvidence>(
                new CommandDefinition(
                    """
                    SELECT wanted.uuid AS "AssignmentUuid",wanted.revision AS "Revision",e.input_revision_uuid AS "InputRevisionUuid",
                        e.json_pointer AS "JsonPointer",e.start_code_point AS "StartCodePoint",e.end_code_point AS "EndCodePoint",e.quote AS "Quote"
                    FROM unnest(@ids::uuid[],@revisions::bigint[]) wanted(uuid,revision)
                    JOIN LATERAL (
                        SELECT input_revision_uuid,json_pointer,start_code_point,end_code_point,quote
                        FROM normalization.venue_assignment_evidence
                        WHERE imported_assignment_uuid=wanted.uuid AND assignment_revision=wanted.revision
                        ORDER BY input_revision_uuid,json_pointer,start_code_point NULLS FIRST,end_code_point NULLS FIRST,quote NULLS FIRST
                        LIMIT @take
                    ) e ON true
                    """,
                    new
                    {
                        ids = revisions.Select(row => row.AssignmentUuid).ToArray(),
                        revisions = revisions.Select(row => row.Revision).ToArray(),
                        take = maximumEvidence + 1,
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToLookup(row => (row.AssignmentUuid, row.Revision));
        var result = new Dictionary<(Guid, long), NormalizedEvidenceReference[]>();
        foreach (var revision in revisions)
        {
            var citations = evidence[revision].ToArray();
            if (citations.Length > maximumEvidence)
            {
                throw new InvalidOperationException(
                    "Imported venue resolution exceeds its assignment evidence limit."
                );
            }
            result.Add(
                revision,
                citations
                    .Select(row => new NormalizedEvidenceReference(
                        row.InputRevisionUuid,
                        row.JsonPointer,
                        row.StartCodePoint,
                        row.EndCodePoint,
                        row.Quote
                    ))
                    .ToArray()
            );
        }
        return result;
    }

    private static async Task<VenueCandidateFacts[]> ReadImportedAssignmentTargets(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        ImportedVenueAssignmentFact[] assignments,
        VenueCandidateFacts[] existing,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        var targets = assignments
            .Select(assignment => assignment.ResolvedVenueUuid)
            .OfType<Guid>()
            .Distinct()
            .Order()
            .ToArray();
        var missing = targets.Except(existing.Select(fact => fact.Header.Uuid)).ToArray();
        var hits = (
            await c.QueryAsync<TargetRow>(
                new CommandDefinition(
                    """
                    SELECT v.uuid AS "OriginUuid",v.uuid AS "Uuid",v.revision AS "Revision",v.display_name AS "DisplayText",owner.uuid AS "OwnerSubjectUuid"
                    FROM normalization.canonical_venues v LEFT JOIN normalization.work_subjects owner
                        ON owner.domain='venues' AND owner.subject_kind='venue' AND owner.subject_uuid=v.uuid
                    WHERE v.uuid=ANY(@missing) ORDER BY v.uuid
                    """,
                    new { missing },
                    t,
                    cancellationToken: ct
                )
            )
        ).Select(row => new NormalizationCandidateHit(row.Uuid, row.Revision, row.DisplayText, row.OwnerSubjectUuid, 0, ["assignment_target"])).ToArray();
        foreach (var target in missing.Except(hits.Select(hit => hit.Uuid)))
        {
            context.AddIssue(
                CandidateFactsIssueKind.MissingRelatedIdentity,
                target,
                "assignment_target"
            );
        }
        var extra = await ReadVenueFactsAsync(
            c,
            t,
            new(IdentityMatchingDomain.Venues, hits, false),
            limits,
            context,
            ct
        );
        var targetSet = targets.ToHashSet();
        return existing
            .Concat(extra)
            .Where(fact => targetSet.Contains(fact.Header.Uuid))
            .OrderBy(fact => fact.Header.Uuid)
            .ToArray();
    }

    private sealed record ImportedAssignmentOwner(
        Guid SubjectUuid,
        long DesiredGeneration,
        long EditorialRevision,
        string PolicyVersion,
        long PublishedGeneration,
        Guid? PublishedWorkUuid
    );

    private sealed record ImportedAssignmentRow(
        Guid AssignmentUuid,
        long Revision,
        Guid StoredVenueUuid,
        Guid? ResolvedVenueUuid,
        string State,
        string Basis,
        string ReasonCode,
        Guid DecisionUuid,
        bool IsHistorical,
        Guid? SupersedingDecisionUuid
    );

    private sealed record ImportedAssignmentEvidence(
        Guid AssignmentUuid,
        long Revision,
        Guid InputRevisionUuid,
        string JsonPointer,
        int? StartCodePoint,
        int? EndCodePoint,
        string? Quote
    );
}
