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
    private static async Task<MusicAuthorshipRow[]> ReadCompositionAuthorshipRows(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        MusicFactIdentity[] identities,
        Guid candidate,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        var rows = (
            await c.QueryAsync<MusicAuthorshipRow>(
                new CommandDefinition(
                    $"""
                    SELECT x.uuid AS "Uuid",x.composition_uuid AS "StoredIdentityUuid",r.revision AS "Revision",r.basis AS "Basis",
                        r.uuid AS "MusicRevisionUuid",x.complete_authorship AS "CompleteAuthorship",
                        CASE WHEN jsonb_typeof(x.authors)='array' THEN
                            (SELECT COALESCE(jsonb_agg(a.value ORDER BY a.ordinal),'[]'::jsonb)
                             FROM (SELECT value,ordinality AS ordinal FROM jsonb_array_elements(x.authors) WITH ORDINALITY LIMIT @authorTake) a)
                            ELSE '[]'::jsonb END::text AS "AuthorsJson",
                        jsonb_typeof(x.authors) IS DISTINCT FROM 'array' AS "InvalidAuthors",{MusicEvidenceProjection}
                    FROM normalization.composition_authorship_assertions x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                    WHERE x.composition_uuid=ANY(@ids) AND NOT x.retracted ORDER BY x.uuid LIMIT @take
                    """,
                    new
                    {
                        ids = identities.Select(identity => identity.Uuid).ToArray(),
                        take = limits.MaximumAssertionsPerKind + 1,
                        authorTake = limits.MaximumAssertionsPerKind + 1,
                        evidenceTake = limits.MaximumEvidencePerAssertion + 1,
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        rows = BoundMusicRows(
            rows,
            limits.MaximumAssertionsPerKind,
            context,
            candidate,
            "authorship_assertions"
        );
        foreach (var row in rows)
        {
            try
            {
                row.Authors =
                    JsonSerializer.Deserialize<CompositionAuthorMember[]>(row.AuthorsJson) ?? [];
            }
            catch (JsonException)
            {
                context.AddIssue(
                    CandidateFactsIssueKind.InvalidStoredFact,
                    candidate,
                    "authorship_members"
                );
            }
            if (
                row.InvalidAuthors
                || row.Authors.Length == 0
                || row.Authors.Any(author => !ValidAuthorMember(author))
            )
            {
                context.AddIssue(
                    CandidateFactsIssueKind.InvalidStoredFact,
                    candidate,
                    "authorship_members"
                );
                row.Authors = row.Authors.Where(ValidAuthorMember).ToArray();
            }
            row.Authors = BoundMusicRows(
                row.Authors,
                limits.MaximumAssertionsPerKind,
                context,
                candidate,
                "authorship_members"
            );
        }
        return rows;
    }

    private static bool ValidAuthorMember(CompositionAuthorMember? author) =>
        author != null
        && CompositionAuthorshipAgreement.IsAuthorRole(author.Role)
        && !string.IsNullOrWhiteSpace(author.OriginalText)
        && author.Evidence != null
        && author.PersonUuid != Guid.Empty;

    private static CompositionAuthorshipFact[] AuthorshipFacts(
        MusicAuthorshipRow[] rows,
        MusicFactIdentity[] identities,
        CandidatePersonCreditTarget[] people,
        Guid candidate,
        CandidateFactsLimits limits,
        CandidateFactsContext context
    ) =>
        rows.Select(row =>
            {
                var provenance = MusicProvenance(
                    row,
                    identities
                        .Single(identity => identity.Uuid == row.StoredIdentityUuid)
                        .OwnerSubjectUuid,
                    candidate,
                    limits,
                    context,
                    "authorship_assertion"
                );
                if (row.Authors.Any(author => !provenance.Evidence.Contains(author.Evidence!)))
                {
                    context.AddIssue(
                        CandidateFactsIssueKind.InvalidStoredFact,
                        candidate,
                        "authorship_member_evidence"
                    );
                }
                return new CompositionAuthorshipFact(
                    provenance,
                    row.StoredIdentityUuid,
                    row.CompleteAuthorship,
                    row.Authors.Select(author => new CompositionAuthorFact(
                            author.Role,
                            author.OriginalText,
                            author.PersonUuid,
                            author.PersonUuid is { } person
                                ? people
                                    .Single(target => target.StoredPersonUuid == person)
                                    .ResolvedPersonUuid
                                : null,
                            author.Evidence
                        ))
                        .ToArray()
                );
            })
            .ToArray();

    private sealed class MusicAuthorshipRow : MusicAssertionRow
    {
        public bool CompleteAuthorship { get; init; }
        public string AuthorsJson { get; init; } = null!;
        public bool InvalidAuthors { get; init; }
        public CompositionAuthorMember[] Authors { get; set; } = [];
    }
}
