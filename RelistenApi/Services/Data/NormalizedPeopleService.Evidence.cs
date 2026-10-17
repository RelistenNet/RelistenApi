using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedPeopleService
{
    /// <summary>Validates against exact retained bytes. JSON Pointer escapes and Unicode scalar offsets are never normalized.</summary>
    public static async Task ValidateEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<NormalizedEvidenceReference> evidence,
        CancellationToken cancellationToken = default
    )
    {
        foreach (var revision in evidence.GroupBy(e => e.InputRevisionUuid))
        {
            var input = await connection.QuerySingleOrDefaultAsync<NormalizationInputRevision>(
                new CommandDefinition(
                    "SELECT uuid, payload, media_type AS \"MediaType\" FROM normalization.input_revisions WHERE uuid=@uuid",
                    new { uuid = revision.Key },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
            if (input == null)
            {
                throw new ArgumentException("Evidence input revision does not exist.");
            }

            using var document = NormalizationRetainedDocument.Open(input, cancellationToken);
            foreach (var reference in revision)
            {
                document.ValidateEvidence(reference);
            }
        }
    }

    /// <summary>A locator proves a name only when the cited string actually contains that wording.</summary>
    internal static async Task ValidateNameEvidenceAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        string wording,
        NormalizedEvidenceReference evidence,
        CancellationToken ct
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wording);
        string text;
        if (evidence.Quote != null)
        {
            text = evidence.Quote;
        }
        else
        {
            var input = await c.QuerySingleAsync<NormalizationInputRevision>(
                new CommandDefinition(
                    "SELECT uuid, payload, media_type AS \"MediaType\" FROM normalization.input_revisions WHERE uuid=@InputRevisionUuid",
                    evidence,
                    t,
                    cancellationToken: ct
                )
            );
            using var document = NormalizationRetainedDocument.Open(input, ct);
            var value = document.Resolve(evidence.JsonPointer);
            if (value.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException(
                    "A name assertion must cite a retained string containing its wording."
                );
            }
            text = value.GetString()!;
        }
        // Extractors may decode HTML entities for a displayed name while citations retain exact bytes.
        if (
            !text.Contains(wording, StringComparison.Ordinal)
            && !WebUtility.HtmlDecode(text).Contains(wording, StringComparison.Ordinal)
        )
        {
            throw new ArgumentException(
                "The cited name evidence does not contain the asserted wording."
            );
        }
    }

    internal static async Task ValidatePublicationEvidenceAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid workUuid,
        string domain,
        IReadOnlyList<NormalizedEvidenceReference> evidence,
        CancellationToken ct
    )
    {
        var json = await c.QuerySingleAsync<string>(
            new CommandDefinition(
                "SELECT r.ticket::text FROM normalization.work_runs r JOIN normalization.work_subjects s ON s.uuid=r.subject_uuid WHERE r.uuid=@workUuid AND s.domain=@domain",
                new { workUuid, domain },
                t,
                cancellationToken: ct
            )
        );
        var ticket = JsonSerializer.Deserialize<NormalizationWorkTicket>(json)!;
        var allowed = ticket.Inputs.Select(i => i.RevisionUuid).ToHashSet();
        if (evidence.Any(e => !allowed.Contains(e.InputRevisionUuid)))
        {
            throw new ArgumentException(
                "Publication cites evidence outside its consumed read set."
            );
        }
        await ValidateEvidenceAsync(c, t, evidence, ct);
    }

    internal static async Task ValidateReviewSubject(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid subjectUuid,
        string domain,
        CancellationToken ct
    )
    {
        if (
            !await c.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    "SELECT EXISTS(SELECT 1 FROM normalization.work_subjects WHERE uuid=@subjectUuid AND domain=@domain)",
                    new { subjectUuid, domain },
                    t,
                    cancellationToken: ct
                )
            )
        )
        {
            throw new ArgumentException("Review subject belongs to a different domain.");
        }
    }

    internal static async Task ValidateOwnerAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid subjectUuid,
        string domain,
        string kind,
        Guid entityUuid,
        CancellationToken ct
    )
    {
        var matches = await c.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM normalization.work_subjects WHERE uuid=@subjectUuid AND domain=@domain AND subject_kind=@kind AND subject_uuid=@entityUuid)",
                new
                {
                    subjectUuid,
                    domain,
                    kind,
                    entityUuid,
                },
                t,
                cancellationToken: ct
            )
        );
        if (!matches)
        {
            throw new ArgumentException(
                "Normalization owner does not match the changed domain subject."
            );
        }
    }

    internal static Task ValidateWorkOwnerAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid workUuid,
        string domain,
        string kind,
        Guid entityUuid,
        CancellationToken ct
    ) => ValidateOwnerForWork(c, t, workUuid, domain, kind, entityUuid, ct);

    private static async Task ValidateOwnerForWork(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid workUuid,
        string domain,
        string kind,
        Guid entityUuid,
        CancellationToken ct
    )
    {
        var subjectUuid = await c.ExecuteScalarAsync<Guid>(
            new CommandDefinition(
                "SELECT subject_uuid FROM normalization.work_runs WHERE uuid=@workUuid",
                new { workUuid },
                t,
                cancellationToken: ct
            )
        );
        await ValidateOwnerAsync(c, t, subjectUuid, domain, kind, entityUuid, ct);
    }
}
