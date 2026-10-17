using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Owns immutable upstream bytes and fetch history. Extraction never updates these rows.
/// Current input changes use a caller-supplied generation read before the upstream request,
/// so a slow older response cannot overwrite a committed newer response.
/// </summary>
public sealed partial class NormalizationEvidenceService(DbService db)
{
    private const string RevisionColumns = """
        r.uuid AS "Uuid",p.provider AS "Provider",p.resource_type AS "ResourceType",
        p.provider_key AS "ProviderKey",r.payload AS "Payload",r.sha256 AS "Sha256",
        r.media_type AS "MediaType",r.capture_version AS "CaptureVersion",r.captured_at AS "CapturedAt"
        """;

    private const string FetchColumns = """
        sequence AS "Sequence",fetch_uuid AS "FetchUuid",input_revision_uuid AS "InputRevisionUuid",
        expected_generation AS "ExpectedGeneration",resource_generation AS "ResourceGeneration",
        accepted_as_current AS "AcceptedAsCurrent",advanced_current AS "AdvancedCurrent",
        observed_at AS "ObservedAt",provider_revision AS "ProviderRevision",captured_at AS "CapturedAt"
        """;

    /// <summary>
    /// Retains one completed fetch atomically. Equal bytes/media/capture version reuse a revision.
    /// Stale generations still retain their evidence, but cannot change the current input.
    /// FetchUuid reuse with different request content throws InvalidOperationException.
    /// No caller checkpoint may advance until this task succeeds.
    /// </summary>
    public Task<NormalizationCaptureResult> CaptureInputAsync(
        NormalizationInputCapture capture,
        CancellationToken cancellationToken = default
    )
    {
        Validate(capture.Resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(capture.MediaType);
        ArgumentException.ThrowIfNullOrWhiteSpace(capture.CaptureVersion);
        ArgumentOutOfRangeException.ThrowIfNegative(capture.ExpectedGeneration);
        if (capture.FetchUuid == Guid.Empty)
        {
            throw new ArgumentException("A stable fetch UUID is required.", nameof(capture));
        }

        // Snapshot mutable caller bytes before the first await and before DbService can retry.
        var payload = capture.Payload.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(payload));

        // PostgreSQL timestamps retain microseconds. Canonicalize before replay comparison.
        var observedAt = new DateTime(
            capture.ObservedAt.UtcTicks - capture.ObservedAt.UtcTicks % 10,
            DateTimeKind.Utc
        );
        var parameters = new
        {
            capture.FetchUuid,
            capture.Resource.Provider,
            capture.Resource.ResourceType,
            capture.Resource.ProviderKey,
        };

        return db.WithWriteConnection(async connection =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var transaction = await (
                (NpgsqlConnection)connection
            ).BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            // Serialize FetchUuid before resource locking so replay with a different resource cannot
            // create a second receipt. A hash collision only serializes unrelated captures.
            await connection.ExecuteAsync(
                new CommandDefinition(
                    "SELECT pg_advisory_xact_lock(hashtextextended(@FetchUuid::text,0))",
                    parameters,
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
            var previous = await ReadFetch(
                connection,
                transaction,
                capture.FetchUuid,
                cancellationToken
            );
            if (previous != null)
            {
                var revision = await ReadRevision(
                    connection,
                    transaction,
                    previous.InputRevisionUuid,
                    cancellationToken
                );
                ValidateReplay(capture, payload, observedAt, revision!, previous);
                await transaction.CommitAsync(cancellationToken);
                return new NormalizationCaptureResult(revision!, previous);
            }

            var resource = await LockResource(
                connection,
                transaction,
                parameters,
                cancellationToken
            );
            var revisionUuid = await RetainRevision(
                connection,
                transaction,
                resource.Uuid,
                capture with
                {
                    Payload = payload,
                },
                sha256,
                cancellationToken
            );
            await RecordFetch(
                connection,
                transaction,
                resource,
                revisionUuid,
                capture,
                observedAt,
                cancellationToken
            );
            var input = await ReadRevision(
                connection,
                transaction,
                revisionUuid,
                cancellationToken
            );
            var fetch = await ReadFetch(
                connection,
                transaction,
                capture.FetchUuid,
                cancellationToken
            );
            await transaction.CommitAsync(cancellationToken);
            return new NormalizationCaptureResult(input!, fetch!);
        });
    }

    /// <summary>Returns retained bytes by immutable revision UUID, including historical or stale fetch inputs.</summary>
    public Task<NormalizationInputRevision?> GetInputAsync(
        Guid revisionUuid,
        CancellationToken cancellationToken = default
    ) =>
        db.WithWriteConnection(connection =>
            ReadRevision(connection, null, revisionUuid, cancellationToken)
        );

    /// <summary>
    /// Reads one committed fetch UUID with its original receipt and immutable input bytes.
    /// Historical and nonadmitted fetches retain their original admission result and generations;
    /// this lookup does not read or assert the resource's current input. Unknown UUIDs return null.
    /// </summary>
    public Task<NormalizationCaptureResult?> GetCaptureAsync(
        Guid fetchUuid,
        CancellationToken cancellationToken = default
    ) =>
        db.WithWriteConnection<NormalizationCaptureResult?>(async connection =>
        {
            var fetch = await ReadFetch(connection, null, fetchUuid, cancellationToken);
            if (fetch == null)
            {
                return null;
            }

            // Receipts and revisions are immutable and commit together. Following this receipt's
            // foreign key needs no current-head read or transaction spanning the two lookups.
            var revision = await ReadRevision(
                connection,
                null,
                fetch.InputRevisionUuid,
                cancellationToken
            );
            return new(revision!, fetch);
        });

    /// <summary>Reads the primary database; an absent resource has expected generation zero.</summary>
    public Task<NormalizationCurrentInput?> GetCurrentInputAsync(
        NormalizationResourceKey resource,
        CancellationToken cancellationToken = default
    )
    {
        Validate(resource);
        return db.WithWriteConnection(async connection =>
        {
            var row = await connection.QuerySingleOrDefaultAsync<RevisionRow>(
                new CommandDefinition(
                    $"""
                    SELECT {RevisionColumns},p.generation AS "Generation"
                    FROM normalization.input_resources p
                    JOIN normalization.input_revisions r ON r.uuid=p.current_revision_uuid
                    WHERE p.provider=@Provider AND p.resource_type=@ResourceType AND p.provider_key=@ProviderKey
                    """,
                    resource,
                    cancellationToken: cancellationToken
                )
            );
            return row == null
                ? null
                : new NormalizationCurrentInput(resource, row.Generation, row.ToRevision());
        });
    }

    /// <summary>
    /// Reads a complete, bounded generation snapshot for a provider resource type before a listing fetch.
    /// Response identities absent from this snapshot must use generation zero. Exceeding the bound
    /// fails instead of omitting existing resources and treating them as newly discovered identities.
    /// </summary>
    public Task<IReadOnlyDictionary<string, long>> GetResourceGenerationsAsync(
        string provider,
        string resourceType,
        int maxResources = 100000,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceType);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxResources, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxResources, 100000);

        return db.WithWriteConnection<IReadOnlyDictionary<string, long>>(async connection =>
        {
            var rows = (
                await connection.QueryAsync<ResourceGenerationRow>(
                    new CommandDefinition(
                        """
                        SELECT provider_key AS "ProviderKey",generation AS "Generation"
                        FROM normalization.input_resources
                        WHERE provider=@provider AND resource_type=@resourceType
                        ORDER BY provider_key LIMIT @limit
                        """,
                        new
                        {
                            provider,
                            resourceType,
                            limit = maxResources + 1,
                        },
                        cancellationToken: cancellationToken
                    )
                )
            ).AsList();
            if (rows.Count > maxResources)
            {
                throw new InvalidOperationException(
                    "The provider resource generation snapshot exceeds its configured bound."
                );
            }

            return rows.ToDictionary(
                row => row.ProviderKey,
                row => row.Generation,
                StringComparer.Ordinal
            );
        });
    }

    /// <summary>Pages committed fetch receipts by sequence; stale captures and unchanged fetches are included.</summary>
    public Task<IReadOnlyList<NormalizationInputFetch>> GetFetchHistoryAsync(
        NormalizationResourceKey resource,
        long afterSequence = 0,
        int limit = 100,
        CancellationToken cancellationToken = default
    )
    {
        Validate(resource);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Use a page size from 1 to 1000.");
        }

        return db.WithWriteConnection<IReadOnlyList<NormalizationInputFetch>>(async connection =>
            (
                await connection.QueryAsync<NormalizationInputFetch>(
                    new CommandDefinition(
                        $"""
                        SELECT {FetchColumns} FROM normalization.input_fetches
                        WHERE resource_uuid=(SELECT uuid FROM normalization.input_resources
                            WHERE provider=@Provider AND resource_type=@ResourceType AND provider_key=@ProviderKey)
                            AND sequence>@afterSequence ORDER BY sequence LIMIT @limit
                        """,
                        new
                        {
                            resource.Provider,
                            resource.ResourceType,
                            resource.ProviderKey,
                            afterSequence,
                            limit,
                        },
                        cancellationToken: cancellationToken
                    )
                )
            ).AsList()
        );
    }

    private static async Task<Guid> RetainRevision(
        IDbConnection connection,
        IDbTransaction transaction,
        Guid resourceUuid,
        NormalizationInputCapture capture,
        string sha256,
        CancellationToken cancellationToken
    )
    {
        var revisionUuid = await connection.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                """
                SELECT uuid FROM normalization.input_revisions WHERE resource_uuid=@resourceUuid
                    AND sha256=@sha256 AND media_type=@MediaType AND capture_version=@CaptureVersion
                """,
                new
                {
                    resourceUuid,
                    sha256,
                    capture.MediaType,
                    capture.CaptureVersion,
                },
                transaction,
                cancellationToken: cancellationToken
            )
        );
        if (revisionUuid == null)
        {
            revisionUuid = Guid.NewGuid();
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO normalization.input_revisions
                        (uuid,resource_uuid,payload,sha256,media_type,capture_version)
                    VALUES(@revisionUuid,@resourceUuid,@payload,@sha256,@MediaType,@CaptureVersion)
                    """,
                    new
                    {
                        revisionUuid,
                        resourceUuid,
                        payload = capture.Payload,
                        sha256,
                        capture.MediaType,
                        capture.CaptureVersion,
                    },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
        }

        return revisionUuid.Value;
    }

    private static async Task RecordFetch(
        IDbConnection connection,
        IDbTransaction transaction,
        ResourceRow resource,
        Guid revisionUuid,
        NormalizationInputCapture capture,
        DateTime observedAt,
        CancellationToken cancellationToken
    )
    {
        var accepted = resource.Generation == capture.ExpectedGeneration;
        var advanced = accepted && resource.CurrentRevisionUuid != revisionUuid;
        // Every admitted fetch fences earlier in-flight responses, even if its bytes are equal.
        // Content revisions remain deduplicated; generation records current observation order.
        var generation = resource.Generation + (accepted ? 1 : 0);
        if (accepted)
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE normalization.input_resources SET generation=@generation,
                        current_revision_uuid=@revisionUuid WHERE uuid=@resourceUuid
                    """,
                    new
                    {
                        resourceUuid = resource.Uuid,
                        generation,
                        revisionUuid,
                    },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
        }

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO normalization.input_fetches(fetch_uuid,resource_uuid,input_revision_uuid,
                    expected_generation,resource_generation,accepted_as_current,advanced_current,
                    observed_at,provider_revision)
                VALUES(@FetchUuid,@resourceUuid,@revisionUuid,@ExpectedGeneration,@generation,
                    @accepted,@advanced,@observedAt,@ProviderRevision)
                """,
                new
                {
                    capture.FetchUuid,
                    resourceUuid = resource.Uuid,
                    revisionUuid,
                    capture.ExpectedGeneration,
                    generation,
                    accepted,
                    advanced,
                    observedAt,
                    capture.ProviderRevision,
                },
                transaction,
                cancellationToken: cancellationToken
            )
        );
    }

    private static async Task<ResourceRow> LockResource(
        IDbConnection connection,
        IDbTransaction transaction,
        object parameters,
        CancellationToken cancellationToken
    )
    {
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO normalization.input_resources(uuid,provider,resource_type,provider_key)
                VALUES(gen_random_uuid(),@Provider,@ResourceType,@ProviderKey)
                ON CONFLICT(provider,resource_type,provider_key) DO NOTHING
                """,
                parameters,
                transaction,
                cancellationToken: cancellationToken
            )
        );
        return await connection.QuerySingleAsync<ResourceRow>(
            new CommandDefinition(
                """
                SELECT uuid AS "Uuid",generation AS "Generation",current_revision_uuid AS "CurrentRevisionUuid"
                FROM normalization.input_resources
                WHERE provider=@Provider AND resource_type=@ResourceType AND provider_key=@ProviderKey FOR UPDATE
                """,
                parameters,
                transaction,
                cancellationToken: cancellationToken
            )
        );
    }

    private static async Task<NormalizationInputRevision?> ReadRevision(
        IDbConnection connection,
        IDbTransaction? transaction,
        Guid revisionUuid,
        CancellationToken cancellationToken
    )
    {
        var row = await connection.QuerySingleOrDefaultAsync<RevisionRow>(
            new CommandDefinition(
                $"""
                SELECT {RevisionColumns} FROM normalization.input_revisions r
                JOIN normalization.input_resources p ON p.uuid=r.resource_uuid WHERE r.uuid=@revisionUuid
                """,
                new { revisionUuid },
                transaction,
                cancellationToken: cancellationToken
            )
        );
        return row?.ToRevision();
    }

    private static Task<NormalizationInputFetch?> ReadFetch(
        IDbConnection connection,
        IDbTransaction? transaction,
        Guid fetchUuid,
        CancellationToken cancellationToken
    ) =>
        connection.QuerySingleOrDefaultAsync<NormalizationInputFetch>(
            new CommandDefinition(
                $"SELECT {FetchColumns} FROM normalization.input_fetches WHERE fetch_uuid=@fetchUuid",
                new { fetchUuid },
                transaction,
                cancellationToken: cancellationToken
            )
        );

    private static void Validate(NormalizationResourceKey resource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.ResourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.ProviderKey);
    }

    private static void ValidateReplay(
        NormalizationInputCapture capture,
        byte[] payload,
        DateTime observedAt,
        NormalizationInputRevision revision,
        NormalizationInputFetch fetch
    )
    {
        if (
            revision.Resource != capture.Resource
            || revision.MediaType != capture.MediaType
            || revision.CaptureVersion != capture.CaptureVersion
            || !revision.Payload.SequenceEqual(payload)
            || fetch.ExpectedGeneration != capture.ExpectedGeneration
            || fetch.ObservedAt != observedAt
            || fetch.ProviderRevision != capture.ProviderRevision
        )
        {
            throw new InvalidOperationException(
                "Fetch UUID is already committed with different capture content."
            );
        }
    }

    private sealed class ResourceRow
    {
        public Guid Uuid { get; init; }
        public long Generation { get; init; }
        public Guid? CurrentRevisionUuid { get; init; }
    }

    private sealed class ResourceGenerationRow
    {
        public string ProviderKey { get; init; } = null!;
        public long Generation { get; init; }
    }

    private sealed class RevisionRow
    {
        public Guid Uuid { get; init; }
        public string Provider { get; init; } = null!;
        public string ResourceType { get; init; } = null!;
        public string ProviderKey { get; init; } = null!;
        public byte[] Payload { get; init; } = [];
        public string Sha256 { get; init; } = null!;
        public string MediaType { get; init; } = null!;
        public string CaptureVersion { get; init; } = null!;
        public DateTime CapturedAt { get; init; }
        public long Generation { get; init; }

        public NormalizationInputRevision ToRevision() =>
            new()
            {
                Uuid = Uuid,
                Resource = new(Provider, ResourceType, ProviderKey),
                Payload = Payload,
                Sha256 = Sha256,
                MediaType = MediaType,
                CaptureVersion = CaptureVersion,
                CapturedAt = CapturedAt,
            };
    }
}
