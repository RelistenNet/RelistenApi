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
    private const string SameAliasCitation = """
        input_revision_uuid=@InputRevisionUuid AND json_pointer=@JsonPointer
        AND start_code_point IS NOT DISTINCT FROM @StartCodePoint::integer
        AND end_code_point IS NOT DISTINCT FROM @EndCodePoint::integer
        AND quote IS NOT DISTINCT FROM @Quote::text
        """;

    /// <summary>
    /// Writes canonical assertions only. The caller authenticates publication evidence and
    /// holds People/Compositions candidate scopes and the original person owner in Work.
    /// Support attachment/retirement requires a separate authenticated publication step.
    /// </summary>
    internal static async Task<CanonicalPersonAliasWriteOutcome> WriteAutomaticAliasAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workUuid,
        PersonAlias alias,
        IReadOnlySet<Guid> createdPeople,
        CancellationToken ct
    )
    {
        ValidateAutomaticMetadataBasis(alias.Basis);
        await ValidateNameEvidenceAsync(connection, transaction, alias.Wording, alias.Evidence, ct);

        var args = AliasArguments(alias);
        var current = await connection.QuerySingleOrDefaultAsync<AutomaticAliasBinding>(
            new CommandDefinition(
                $"""
                SELECT person_uuid AS "PersonUuid",
                    (person_uuid=@PersonUuid AND wording=@Wording AND basis=@Basis AND {SameAliasCitation}) AS "SameAssertion"
                FROM normalization.person_aliases WHERE uuid=@Uuid
                """,
                args,
                transaction,
                cancellationToken: ct
            )
        );
        if (!createdPeople.Contains(alias.PersonUuid) && current == null)
        {
            await ValidateWorkOwnerAsync(
                connection,
                transaction,
                workUuid,
                "people",
                "person",
                alias.PersonUuid,
                ct
            );
        }

        // Review owns the normalized-name claim across UUIDs and citations. The raw
        // history remains authoritative even after all managed support has disappeared.
        var reviewed = (
            await connection.QueryAsync<ReviewedAliasClaim>(
                new CommandDefinition(
                    $"""
                    SELECT uuid AS "Uuid",({SameAliasCitation}) AS "SameCitation"
                    FROM normalization.person_aliases
                    WHERE person_uuid=@PersonUuid AND basis='reviewed' AND (name_key=@NameKey OR uuid=@Uuid)
                    UNION ALL
                    SELECT uuid AS "Uuid",({SameAliasCitation}) AS "SameCitation"
                    FROM normalization.person_aliases_history
                    WHERE person_uuid=@PersonUuid AND (name_key=@NameKey OR uuid=@Uuid)
                    """,
                    args,
                    transaction,
                    cancellationToken: ct
                )
            )
        ).ToArray();

        if (
            current != null
            && (
                current.PersonUuid != alias.PersonUuid
                || (reviewed.Length == 0 && !current.SameAssertion)
            )
        )
        {
            return new(CanonicalPersonAliasWriteDisposition.ConflictingUuid, alias.Uuid);
        }
        var protectedClaim = reviewed.FirstOrDefault(claim => claim.SameCitation);
        if (protectedClaim != null)
        {
            return new(
                CanonicalPersonAliasWriteDisposition.ProtectedReviewedNaturalClaim,
                protectedClaim.Uuid
            );
        }

        var state = reviewed.Length == 0 ? "accepted" : "pending";
        args.Add("state", state);
        var duplicate = await connection.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                $"""
                SELECT uuid FROM normalization.person_aliases
                WHERE person_uuid=@PersonUuid AND wording=@Wording AND basis=@Basis
                    AND state=@state AND {SameAliasCitation}
                ORDER BY uuid LIMIT 1
                """,
                args,
                transaction,
                cancellationToken: ct
            )
        );
        if (duplicate.HasValue)
        {
            return new(
                state == "accepted"
                    ? CanonicalPersonAliasWriteDisposition.ExactDuplicate
                    : CanonicalPersonAliasWriteDisposition.Pending,
                duplicate.Value
            );
        }

        // A changed citation under a reviewed claim is a new pending assertion; it
        // cannot rewrite the reviewed UUID or acquire authority for support replacement.
        var actualUuid = state == "pending" ? Guid.NewGuid() : alias.Uuid;
        args.Add("Uuid", actualUuid);
        var inserted = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO normalization.person_aliases(uuid,person_uuid,wording,name_key,input_revision_uuid,json_pointer,start_code_point,end_code_point,quote,state,basis)
                VALUES(@Uuid,@PersonUuid,@Wording,@NameKey,@InputRevisionUuid,@JsonPointer,@StartCodePoint,@EndCodePoint,@Quote,@state,@Basis)
                ON CONFLICT(uuid) DO NOTHING
                """,
                args,
                transaction,
                cancellationToken: ct
            )
        );
        if (inserted == 0)
        {
            // A retained non-accepted row cannot become accepted by reusing its UUID.
            // Candidate scopes serialize ordinary writers, so no duplicate was missed above.
            return new(CanonicalPersonAliasWriteDisposition.ConflictingUuid, actualUuid);
        }
        return new(
            state == "accepted"
                ? CanonicalPersonAliasWriteDisposition.Accepted
                : CanonicalPersonAliasWriteDisposition.Pending,
            actualUuid,
            AcceptedFactChanged: state == "accepted",
            AssertionInserted: true
        );
    }

    private static DynamicParameters AliasArguments(PersonAlias alias) =>
        new(
            new
            {
                alias.Uuid,
                alias.PersonUuid,
                alias.Wording,
                alias.Basis,
                NameKey = NameKey(alias.Wording),
                alias.Evidence.InputRevisionUuid,
                alias.Evidence.JsonPointer,
                alias.Evidence.StartCodePoint,
                alias.Evidence.EndCodePoint,
                alias.Evidence.Quote,
            }
        );

    private sealed class AutomaticAliasBinding
    {
        public Guid PersonUuid { get; init; }
        public bool SameAssertion { get; init; }
    }

    private sealed class ReviewedAliasClaim
    {
        public Guid Uuid { get; init; }
        public bool SameCitation { get; init; }
    }
}
