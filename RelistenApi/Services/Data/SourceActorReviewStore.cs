using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Owns immutable first review preparations, not authority or mutable workflow results.
/// Only the trusted service can save them. Work's immutable review receipt owns apply replay.
/// </summary>
public sealed class SourceActorReviewStore(DbService db)
{
    public const int MaximumPreparationBytes = 32 * 1024 * 1024;

    /// <summary>Reads the retained preparation without refreshing its original observations.</summary>
    public Task<SourceActorReviewPreparation?> GetPreparationAsync(
        Guid preparationUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection(async c =>
        {
            var json = await c.QuerySingleOrDefaultAsync<string>(
                new CommandDefinition(
                    "SELECT preparation::text FROM normalization.source_actor_review_preparations WHERE preparation_uuid=@preparationUuid",
                    new { preparationUuid },
                    cancellationToken: ct
                )
            );
            return json == null
                ? null
                : JsonSerializer.Deserialize<SourceActorReviewPreparation>(json)!;
        });

    internal Task<SourceActorReviewPreparation> SaveAsync(
        SourceActorReviewPreparation preparation,
        CancellationToken ct
    ) =>
        db.WithWriteConnection(async c =>
        {
            var json = JsonSerializer.Serialize(preparation);
            if (Encoding.UTF8.GetByteCount(json) > MaximumPreparationBytes)
            {
                throw new SourceActorReviewCapacityException(
                    "source_review_preparation_capacity_exceeded"
                );
            }
            var request = JsonSerializer.Serialize(preparation.Request);
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request)));
            await using var t = await ((NpgsqlConnection)c).BeginTransactionAsync(ct);
            try
            {
                await c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO normalization.source_actor_review_preparations(
                            preparation_uuid,source_uuid,source_owner_uuid,extraction_attempt_uuid,processing_version,request_hash,request,preparation)
                        VALUES(@PreparationUuid,@source,@owner,@extraction,@ProcessingVersion,@hash,@request::jsonb,@json::jsonb)
                        ON CONFLICT(preparation_uuid) DO NOTHING
                        """,
                        new
                        {
                            preparation.PreparationUuid,
                            source = preparation.Request.Claim.SourceUuid,
                            owner = preparation.SourceOwner.SubjectUuid,
                            extraction = preparation.Request.ExtractionAttemptUuid,
                            preparation.ProcessingVersion,
                            hash,
                            request,
                            json,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
            }
            catch (PostgresException error)
                when (error.SqlState == "23514"
                    && error.ConstraintName == "source_actor_review_preparation_size"
                )
            {
                throw new SourceActorReviewCapacityException(
                    "source_review_preparation_capacity_exceeded"
                );
            }
            var winner = await c.QuerySingleAsync<string>(
                new CommandDefinition(
                    "SELECT preparation::text FROM normalization.source_actor_review_preparations WHERE preparation_uuid=@PreparationUuid",
                    preparation,
                    t,
                    cancellationToken: ct
                )
            );
            var saved = JsonSerializer.Deserialize<SourceActorReviewPreparation>(winner)!;
            RequireRequest(saved, preparation.Request);
            await t.CommitAsync(ct);
            return saved;
        });

    internal static void RequireRequest(
        SourceActorReviewPreparation preparation,
        SourceActorReviewRequest request
    )
    {
        if (preparation.Request != request)
        {
            throw new NormalizationConflictException(
                "Review preparation UUID already belongs to another request."
            );
        }
    }
}
