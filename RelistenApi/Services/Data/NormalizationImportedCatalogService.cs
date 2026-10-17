using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Retains current imported venue and repertoire facts with their original catalog clocks.
/// These snapshots establish catalog provenance, not provider identity or canonical matches.
/// </summary>
public sealed partial class NormalizationImportedCatalogService(
    DbService db,
    NormalizationEvidenceService evidence
)
{
    private const int MaximumPayloadBytes = 16 * 1024 * 1024;

    private static void RequireRefreshContext(Guid uuid, string parameterName, CancellationToken ct)
    {
        if (uuid == Guid.Empty)
        {
            throw new ArgumentException("A stored catalog UUID is required.", parameterName);
        }
        if (Transaction.Current != null)
        {
            throw new InvalidOperationException(
                "Refresh imported evidence outside the catalog transaction."
            );
        }
        ct.ThrowIfCancellationRequested();
    }

    private Task<T> WithReadSnapshotAsync<T>(
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> read,
        CancellationToken ct
    ) =>
        db.WithWriteConnection(async connection =>
        {
            ct.ThrowIfCancellationRequested();
            var primary = (NpgsqlConnection)connection;
            await using var transaction = await primary.BeginTransactionAsync(
                System.Data.IsolationLevel.RepeatableRead,
                ct
            );
            await primary.ExecuteAsync(
                new CommandDefinition(
                    "SET TRANSACTION READ ONLY",
                    transaction: transaction,
                    cancellationToken: ct
                )
            );
            var result = await read(primary, transaction, ct);
            await transaction.CommitAsync(ct);
            return result;
        });

    private async Task<NormalizationCaptureResult> CaptureSnapshotAsync(
        NormalizationResourceKey resource,
        string captureVersion,
        byte[] payload,
        long expectedGeneration,
        CancellationToken ct
    )
    {
        if (payload.Length > MaximumPayloadBytes)
        {
            throw new InvalidDataException("Imported catalog snapshot exceeds its byte limit.");
        }
        // The catalog snapshot transaction has closed. Retention survives later catalog
        // failure, while CAS prevents this refresh from replacing a concurrent winner.
        var capture = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                payload,
                "application/json; charset=utf-8",
                captureVersion,
                expectedGeneration,
                DateTimeOffset.UtcNow
            ),
            ct
        );
        if (!capture.Fetch.AcceptedAsCurrent)
        {
            throw new NormalizationStaleInputException(resource, capture);
        }
        ct.ThrowIfCancellationRequested();
        return capture;
    }
}
