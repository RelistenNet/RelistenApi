using System;
using System.Collections.Generic;
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
    private static async Task SaveAuthorship(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        CompositionAuthorshipChange assertion,
        Guid decisionUuid,
        bool reviewed,
        CancellationToken ct
    )
    {
        await Require(
            c,
            t,
            "SELECT NOT EXISTS(SELECT 1 FROM normalization.composition_authorship_assertions WHERE uuid=@Uuid AND composition_uuid<>@CompositionUuid)",
            assertion,
            "An author-list assertion cannot move to another composition.",
            ct
        );
        await RequireUnredirected(c, t, "compositions", assertion.CompositionUuid, ct);
        if (assertion.Authors.Count is 0 or > 1024)
        {
            throw new ArgumentException(
                "An author-list assertion requires between one and 1024 source members."
            );
        }

        var authors = new List<CompositionAuthorMember>();
        foreach (var member in assertion.Authors)
        {
            if (!CompositionAuthorshipAgreement.IsAuthorRole(member.Role))
            {
                throw new ArgumentException(
                    "Author-list roles are writer, composer or lyricist; retain arranger and performer credits separately."
                );
            }
            if (member.Evidence == null || !assertion.Evidence.Contains(member.Evidence))
            {
                throw new ArgumentException(
                    "Each author member requires its exact name citation among the assertion's evidence."
                );
            }
            await NormalizedPeopleService.ValidateNameEvidenceAsync(
                c,
                t,
                member.OriginalText,
                member.Evidence,
                ct
            );
            var person = member.PersonUuid is { } uuid
                ? await NormalizedPeopleService.ResolvePersonUuidAsync(c, t, uuid, ct)
                : (Guid?)null;
            authors.Add(member with { PersonUuid = person });
        }

        assertion = assertion with { Authors = authors };
        await Persist(
            c,
            t,
            "composition_authorship_assertions",
            assertion,
            "composition_uuid,complete_authorship,authors",
            "@CompositionUuid,@CompleteAuthorship,@authorsJson::jsonb",
            decisionUuid,
            reviewed,
            ct,
            new { authorsJson = JsonSerializer.Serialize(assertion.Authors) }
        );
    }

    private static async Task<IReadOnlySet<Guid>?> ReadCompleteCompositionAuthors(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid composition,
        CancellationToken ct
    )
    {
        // Merged ancestors remain part of candidate discovery. Resolve the same identity
        // closure here, so a conflicting retained account cannot disappear during acceptance.
        const string closure = """
            WITH RECURSIVE works(uuid) AS (
                SELECT uuid FROM normalization.compositions WHERE uuid=@composition AND NOT retracted
                UNION SELECT x.uuid FROM normalization.compositions x JOIN works survivor ON x.redirect_uuid=survivor.uuid WHERE NOT x.retracted
            )
            """;
        var assertions = (
            await c.QueryAsync<AuthorshipRow>(
                new CommandDefinition(
                    closure
                        + """
                        SELECT complete_authorship AS Complete,authors::text AS Authors
                        FROM normalization.composition_authorship_assertions
                        WHERE composition_uuid IN (SELECT uuid FROM works) AND NOT retracted LIMIT 5001
                        """,
                    new { composition },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (assertions.Length > 5000)
        {
            throw new ArgumentException(
                "The complete authorship assertion set exceeds the supported publication bound."
            );
        }

        var resolved = new List<(bool Complete, IReadOnlyList<Guid?> Authors)>();
        foreach (var assertion in assertions)
        {
            var members = JsonSerializer.Deserialize<CompositionAuthorMember[]>(assertion.Authors)!;
            var authors = new List<Guid?>();
            foreach (var member in members)
            {
                authors.Add(
                    member.PersonUuid is { } person
                        ? await NormalizedPeopleService.ResolvePersonUuidAsync(c, t, person, ct)
                        : null
                );
            }
            resolved.Add((assertion.Complete, authors));
        }

        var credits = (
            await c.QueryAsync<Guid?>(
                new CommandDefinition(
                    closure
                        + """
                        SELECT person_uuid FROM normalization.work_credits
                        WHERE composition_uuid IN (SELECT uuid FROM works) AND NOT retracted
                            AND role IN ('writer','composer','lyricist') LIMIT 5001
                        """,
                    new { composition },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (credits.Length > 5000)
        {
            throw new ArgumentException(
                "The individual author credit set exceeds the supported publication bound."
            );
        }

        var individualAuthors = new List<Guid?>();
        foreach (var person in credits)
        {
            individualAuthors.Add(
                person is { } uuid
                    ? await NormalizedPeopleService.ResolvePersonUuidAsync(c, t, uuid, ct)
                    : null
            );
        }
        return CompositionAuthorshipAgreement.CompleteAuthors(resolved, individualAuthors);
    }

    private sealed record AuthorshipRow(bool Complete, string Authors);
}
