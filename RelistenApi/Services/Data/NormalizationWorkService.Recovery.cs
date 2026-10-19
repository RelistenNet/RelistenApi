using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationWorkService
{
    /// <summary>
    /// Finds recoverable run UUIDs for one owner kind using the shared bounded pending
    /// scan. This is discovery only; consumers must revalidate through their services.
    /// No new run or replacement facts are created by reading this page.
    /// </summary>
    public async Task<NormalizationPendingWorkPage> GetPendingRunsAsync(
        string domain,
        string kind,
        Guid? afterUuid = null,
        int limit = 100,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var page = await GetPendingSubjectsAsync(afterUuid, limit, ct);
        var selected = page.Where(value =>
                value.Subject.Domain == domain && value.Subject.Kind == kind
            )
            .ToArray();
        if (selected.Length == 0)
        {
            return new([], page.Count == limit ? page[^1].SubjectUuid : null);
        }

        var runs = await db.WithWriteConnection(async c =>
            (
                await c.QueryAsync<PendingRunRow>(
                    new CommandDefinition(
                        """
                        SELECT s.uuid AS "SubjectUuid",
                            CASE WHEN sum(octet_length(run.ticket::text)) OVER()<=33554432
                                THEN run.ticket::text END AS "Ticket",run.state AS "State"
                        FROM normalization.work_subjects s
                        JOIN LATERAL (
                            SELECT ticket,state FROM normalization.work_runs
                            WHERE subject_uuid=s.uuid ORDER BY created_at DESC,uuid DESC LIMIT 1
                        ) run ON true WHERE s.uuid=ANY(@ids)
                        """,
                        new { ids = selected.Select(value => value.SubjectUuid).ToArray() },
                        cancellationToken: ct
                    )
                )
            ).ToDictionary(value => value.SubjectUuid)
        );
        if (runs.Values.Any(value => value.Ticket == null))
        {
            throw new InvalidOperationException(
                "Pending work exceeds 32 MiB; request a smaller page."
            );
        }
        return new(
            selected
                .Where(value => runs.ContainsKey(value.SubjectUuid))
                .Select(value =>
                {
                    var run = runs[value.SubjectUuid];
                    return new NormalizationPendingWorkRun(
                        value,
                        JsonSerializer.Deserialize<NormalizationWorkTicket>(run.Ticket!)!,
                        run.State switch
                        {
                            "running" => NormalizationWorkRunState.Running,
                            "published" => NormalizationWorkRunState.Published,
                            "stale" => NormalizationWorkRunState.Stale,
                            _ => throw new InvalidOperationException(
                                "Unsupported normalization work state."
                            ),
                        }
                    );
                })
                .ToArray(),
            page.Count == limit ? page[^1].SubjectUuid : null
        );
    }

    private sealed record PendingRunRow(Guid SubjectUuid, string? Ticket, string State);
}
