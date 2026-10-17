using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

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
            var payload = await connection.QuerySingleOrDefaultAsync<byte[]>(
                new CommandDefinition(
                    "SELECT payload FROM normalization.input_revisions WHERE uuid=@uuid",
                    new { uuid = revision.Key },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
            if (payload == null)
            {
                throw new ArgumentException("Evidence input revision does not exist.");
            }
            using var document = JsonDocument.Parse(payload);
            foreach (var reference in revision)
            {
                var value = ResolvePointer(document.RootElement, reference.JsonPointer);
                if (
                    reference.StartCodePoint == null
                    && reference.EndCodePoint == null
                    && reference.Quote == null
                )
                {
                    continue;
                }
                if (
                    value.ValueKind != JsonValueKind.String
                    || reference.StartCodePoint == null
                    || reference.EndCodePoint == null
                    || reference.Quote == null
                )
                {
                    throw new ArgumentException(
                        "Text evidence requires a string value, both offsets and an exact quote."
                    );
                }
                var runes = value.GetString()!.EnumerateRunes().ToArray();
                var start = reference.StartCodePoint.Value;
                var end = reference.EndCodePoint.Value;
                if (
                    start < 0
                    || end < start
                    || end > runes.Length
                    || string.Concat(runes[start..end].Select(r => r.ToString())) != reference.Quote
                )
                {
                    throw new ArgumentException(
                        "Evidence quote does not match its Unicode code-point span."
                    );
                }
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
            var payload = await c.QuerySingleAsync<byte[]>(
                new CommandDefinition(
                    "SELECT payload FROM normalization.input_revisions WHERE uuid=@InputRevisionUuid",
                    evidence,
                    t,
                    cancellationToken: ct
                )
            );
            using var document = JsonDocument.Parse(payload);
            var value = ResolvePointer(document.RootElement, evidence.JsonPointer);
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

    private static JsonElement ResolvePointer(JsonElement root, string pointer)
    {
        if (pointer.Length == 0)
        {
            return root;
        }
        if (!pointer.StartsWith('/'))
        {
            throw new ArgumentException("Evidence locator must be a JSON Pointer.");
        }
        foreach (var encoded in pointer[1..].Split('/'))
        {
            var token = encoded.Replace("~1", "/").Replace("~0", "~");
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(token, out var value))
            {
                root = value;
            }
            else if (
                root.ValueKind == JsonValueKind.Array
                && int.TryParse(token, out var index)
                && index >= 0
                && index < root.GetArrayLength()
            )
            {
                root = root[index];
            }
            else
            {
                throw new ArgumentException(
                    "Evidence JSON Pointer does not identify a retained value."
                );
            }
        }
        return root;
    }
}
