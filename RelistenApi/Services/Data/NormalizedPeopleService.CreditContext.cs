using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedPeopleService
{
    public const string CreditContextVersion = "recording-credit-context-v1";

    private const int MaximumContextClaims = 10_000;
    private const int MaximumContextCitations = 50_000;

    /// <summary>
    /// Reads current credits and retained editorial exclusions together with their original
    /// owner and candidate-pool observations. Oversize context fails rather than omitting a
    /// contradictory claim. A missing recording owner remains protected by PeopleUniverse.
    /// </summary>
    public Task<RecordingCreditContext> ReadCreditContextAsync(
        Guid sourceUuid,
        CancellationToken cancellationToken = default
    ) =>
        db.WithWriteConnection(async connection =>
        {
            var c = (NpgsqlConnection)connection;
            await using var t = await c.BeginTransactionAsync(
                System.Data.IsolationLevel.RepeatableRead,
                cancellationToken
            );
            await c.ExecuteAsync(
                new CommandDefinition(
                    "SET TRANSACTION READ ONLY",
                    transaction: t,
                    cancellationToken: cancellationToken
                )
            );
            var rows = await ReadContextCreditRows(c, t, sourceUuid, cancellationToken);
            var evidence = await ReadContextCitations(
                c,
                t,
                rows.Select(row => row.Uuid).Distinct().ToArray(),
                cancellationToken
            );
            var evidenceInputs = await ReadContextEvidenceInputs(
                c,
                t,
                evidence.Select(citation => citation.InputRevisionUuid).Distinct().ToArray(),
                cancellationToken
            );
            var personUuids = rows.Where(row => row.PersonUuid.HasValue)
                .Select(row => row.PersonUuid!.Value)
                .Distinct()
                .ToArray();
            var redirects = await ReadContextRedirects(c, t, personUuids, cancellationToken);
            var allPeople = personUuids
                .Concat(redirects.Select(redirect => redirect.SurvivorUuid))
                .Distinct()
                .ToArray();
            var observations = await ReadContextOwners(
                c,
                t,
                sourceUuid,
                allPeople,
                cancellationToken
            );
            var epoch = await c.QuerySingleAsync<long>(
                new CommandDefinition(
                    "SELECT generation FROM normalization.candidate_universe_revisions WHERE domain='people' AND scope_uuid='00000000-0000-0000-0000-000000000000'",
                    transaction: t,
                    cancellationToken: cancellationToken
                )
            );
            var result = CreateCreditContext(
                sourceUuid,
                epoch,
                rows,
                evidence,
                evidenceInputs,
                redirects,
                observations
            );
            await t.CommitAsync(cancellationToken);
            return result;
        });

    private static async Task<ContextCreditRow[]> ReadContextCreditRows(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid sourceUuid,
        CancellationToken ct
    )
    {
        var rows = (
            await c.QueryAsync<ContextCreditRow>(
                new CommandDefinition(
                    $"""
                    SELECT {CreditColumns},decision_uuid AS "DecisionUuid",NULL::uuid AS "ReviewOperationUuid",NULL::text AS "Action",false AS "Historical"
                    FROM normalization.recording_credits WHERE source_uuid=@sourceUuid
                    UNION ALL
                    SELECT h.uuid,h.revision,h.basis,h.state,h.source_uuid,h.scope_track_uuid,h.person_uuid,h.role,h.original_text,h.reason_code,
                        h.decision_uuid,r.uuid,r.action,true
                    FROM normalization.recording_credit_history h
                    JOIN normalization.review_operations r ON r.uuid=h.superseding_decision_uuid
                    WHERE h.source_uuid=@sourceUuid
                    LIMIT {MaximumContextClaims + 1}
                    """,
                    new { sourceUuid },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (rows.Length > MaximumContextClaims)
        {
            throw new InvalidOperationException(
                "Recording credit context exceeds its claim limit."
            );
        }
        return rows;
    }

    private static async Task<ContextCitationRow[]> ReadContextCitations(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid[] uuids,
        CancellationToken ct
    )
    {
        var evidence = (
            await c.QueryAsync<ContextCitationRow>(
                new CommandDefinition(
                    $"""
                    SELECT credit_uuid AS "CreditUuid",credit_revision AS "CreditRevision",input_revision_uuid AS "InputRevisionUuid",
                        json_pointer AS "JsonPointer",start_code_point AS "StartCodePoint",end_code_point AS "EndCodePoint",quote AS "Quote"
                    FROM normalization.recording_credit_evidence WHERE credit_uuid=ANY(@uuids)
                    ORDER BY credit_uuid,credit_revision,input_revision_uuid,json_pointer
                    LIMIT {MaximumContextCitations + 1}
                    """,
                    new { uuids },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (evidence.Length > MaximumContextCitations)
        {
            throw new InvalidOperationException(
                "Recording credit context exceeds its citation limit."
            );
        }
        return evidence;
    }

    private static async Task<NormalizationBindingInputReference[]> ReadContextEvidenceInputs(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid[] revisions,
        CancellationToken ct
    )
    {
        var evidenceInputs = (
            await c.QueryAsync<ContextInputRow>(
                new CommandDefinition(
                    """
                    SELECT i.uuid AS "Uuid",r.provider AS "Provider",r.resource_type AS "ResourceType",r.provider_key AS "ProviderKey"
                    FROM normalization.input_revisions i JOIN normalization.input_resources r ON r.uuid=i.resource_uuid
                    WHERE i.uuid=ANY(@revisions) ORDER BY i.uuid
                    """,
                    new { revisions },
                    t,
                    cancellationToken: ct
                )
            )
        ).Select(row => new NormalizationBindingInputReference(row.Uuid, new(row.Provider, row.ResourceType, row.ProviderKey))).ToArray();
        return evidenceInputs;
    }

    private static async Task<CandidateRedirectFact[]> ReadContextRedirects(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid[] personUuids,
        CancellationToken ct
    )
    {
        var redirects = (
            await c.QueryAsync<CandidateRedirectFact>(
                new CommandDefinition(
                    $"""
                    WITH RECURSIVE chain AS (
                        SELECT r.retired_uuid,r.survivor_uuid,r.decision_uuid,ARRAY[r.retired_uuid] AS path
                        FROM normalization.person_redirects r WHERE r.retired_uuid=ANY(@personUuids)
                        UNION ALL
                        SELECT r.retired_uuid,r.survivor_uuid,r.decision_uuid,c.path||r.retired_uuid
                        FROM chain c JOIN normalization.person_redirects r ON r.retired_uuid=c.survivor_uuid
                        WHERE NOT r.retired_uuid=ANY(c.path)
                    ) SELECT DISTINCT retired_uuid AS "OriginalUuid",survivor_uuid AS "SurvivorUuid",decision_uuid AS "DecisionUuid",NULL::uuid AS "MusicRevisionUuid"
                      FROM chain LIMIT {MaximumContextClaims + 1}
                    """,
                    new { personUuids },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (redirects.Length > MaximumContextClaims)
        {
            throw new InvalidOperationException(
                "Recording credit context exceeds its redirect limit."
            );
        }
        return redirects;
    }

    private static async Task<ContextOwnerRow[]> ReadContextOwners(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid sourceUuid,
        Guid[] allPeople,
        CancellationToken ct
    )
    {
        var observations = (
            await c.QueryAsync<ContextOwnerRow>(
                new CommandDefinition(
                    """
                    SELECT s.uuid AS "SubjectUuid",s.desired_generation AS "DesiredGeneration",s.editorial_revision AS "EditorialRevision",
                        s.published_work_uuid AS "PublishedWorkUuid",s.subject_kind AS "Kind"
                    FROM normalization.work_subjects s WHERE s.domain='people'
                        AND ((s.subject_kind='recording' AND s.subject_uuid=@sourceUuid) OR (s.subject_kind='person' AND s.subject_uuid=ANY(@allPeople)))
                    ORDER BY s.uuid
                    """,
                    new { sourceUuid, allPeople },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        return observations;
    }

    private static RecordingCreditContext CreateCreditContext(
        Guid sourceUuid,
        long epoch,
        ContextCreditRow[] rows,
        ContextCitationRow[] evidence,
        NormalizationBindingInputReference[] evidenceInputs,
        CandidateRedirectFact[] redirects,
        ContextOwnerRow[] observations
    )
    {
        var citations = evidence.ToLookup(e => (e.CreditUuid, e.CreditRevision));
        RecordingCreditContextClaim Claim(ContextCreditRow row) =>
            new(
                new(
                    row.Uuid,
                    row.SourceUuid,
                    row.Role,
                    row.OriginalText,
                    row.PersonUuid,
                    row.ScopeTrackUuid,
                    citations[(row.Uuid, row.Revision)]
                        .Select(e => new NormalizedEvidenceReference(
                            e.InputRevisionUuid,
                            e.JsonPointer,
                            e.StartCodePoint,
                            e.EndCodePoint,
                            e.Quote
                        ))
                        .ToArray(),
                    row.State,
                    row.Basis,
                    row.ReasonCode
                ),
                row.Revision,
                row.DecisionUuid
            );
        NormalizationSubjectObservation Observation(ContextOwnerRow row) =>
            new(
                row.SubjectUuid,
                row.DesiredGeneration,
                row.EditorialRevision,
                row.PublishedWorkUuid
            );
        var recording = observations.SingleOrDefault(r => r.Kind == "recording");
        return new RecordingCreditContext(
            sourceUuid,
            recording == null ? null : Observation(recording),
            new(NormalizationCandidateUniverseKey.People, epoch),
            rows.Where(r => !r.Historical).Select(Claim).ToArray(),
            rows.Where(r => r.ReviewOperationUuid.HasValue)
                .Select(r => new RecordingCreditReviewedExclusion(
                    Claim(r),
                    r.ReviewOperationUuid!.Value,
                    r.Action!
                ))
                .Concat(
                    rows.Where(r =>
                            !r.Historical
                            && r.Basis == "reviewed"
                            && r.State is "rejected" or "superseded"
                        )
                        .Select(r => new RecordingCreditReviewedExclusion(
                            Claim(r),
                            r.DecisionUuid,
                            r.State == "rejected" ? "reject" : "retract"
                        ))
                )
                .ToArray(),
            redirects,
            observations.Where(r => r.Kind == "person").Select(Observation).ToArray(),
            evidenceInputs
        );
    }

    private sealed class ContextCreditRow
    {
        public Guid Uuid { get; init; }
        public long Revision { get; init; }
        public string Basis { get; init; } = null!;
        public string State { get; init; } = null!;
        public Guid SourceUuid { get; init; }
        public Guid? ScopeTrackUuid { get; init; }
        public Guid? PersonUuid { get; init; }
        public string Role { get; init; } = null!;
        public string OriginalText { get; init; } = null!;
        public string ReasonCode { get; init; } = null!;
        public bool Historical { get; init; }
        public Guid DecisionUuid { get; init; }
        public Guid? ReviewOperationUuid { get; init; }
        public string? Action { get; init; }
    }

    private sealed class ContextCitationRow
    {
        public Guid CreditUuid { get; init; }
        public long CreditRevision { get; init; }
        public Guid InputRevisionUuid { get; init; }
        public string JsonPointer { get; init; } = null!;
        public int? StartCodePoint { get; init; }
        public int? EndCodePoint { get; init; }
        public string? Quote { get; init; }
    }

    private sealed class ContextInputRow
    {
        public Guid Uuid { get; init; }
        public string Provider { get; init; } = null!;
        public string ResourceType { get; init; } = null!;
        public string ProviderKey { get; init; } = null!;
    }

    private sealed class ContextOwnerRow
    {
        public Guid SubjectUuid { get; init; }
        public long DesiredGeneration { get; init; }
        public long EditorialRevision { get; init; }
        public Guid? PublishedWorkUuid { get; init; }
        public string Kind { get; init; } = null!;
    }
}
