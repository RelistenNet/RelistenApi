using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationEvidenceService
{
    /// <summary>
    /// Reads exact immutable revisions in one primary repeatable-read snapshot. Metadata and
    /// byte lengths are checked before loading any payload. Missing or mismatched references
    /// throw ArgumentException; exceeded bounds throw InvalidOperationException. No truncation.
    /// </summary>
    public Task<IReadOnlyList<NormalizationInputRevision>> GetInputsAsync(
        IReadOnlyList<NormalizationBindingInputReference> inputs,
        NormalizationInputReadLimits? limits = null,
        CancellationToken cancellationToken = default
    )
    {
        var readLimits = limits ?? new NormalizationInputReadLimits();
        var requested = FreezeReadReferences(inputs, readLimits);
        return db.WithWriteConnection(async connection =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var transaction = await (
                (NpgsqlConnection)connection
            ).BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            await connection.ExecuteAsync(
                new CommandDefinition(
                    "SET TRANSACTION READ ONLY",
                    transaction: transaction,
                    cancellationToken: cancellationToken
                )
            );
            var result = await ReadBoundedInputs(
                connection,
                transaction,
                requested,
                readLimits,
                cancellationToken
            );
            await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }

    /// <summary>
    /// Resolves current revision identities without selecting payload bytes. Up to 1,000 unique
    /// resources are read from one primary snapshot. A missing current input fails the whole read.
    /// The returned immutable references can be passed to GetInputsAsync with payload limits.
    /// </summary>
    public Task<IReadOnlyList<NormalizationBindingInputReference>> GetCurrentInputReferencesAsync(
        IReadOnlyList<NormalizationResourceKey> resources,
        CancellationToken cancellationToken = default
    )
    {
        var requested = resources.Distinct().ToArray();
        if (requested.Length > 1000)
        {
            throw new InvalidOperationException(
                "Current input reference read exceeds the resource limit."
            );
        }
        foreach (var resource in requested)
        {
            Validate(resource);
        }
        return db.WithWriteConnection<IReadOnlyList<NormalizationBindingInputReference>>(
            async connection =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var transaction = await (
                    (NpgsqlConnection)connection
                ).BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        "SET TRANSACTION READ ONLY",
                        transaction: transaction,
                        cancellationToken: cancellationToken
                    )
                );
                // Parallel arrays describe exact tuples; independent IN lists could select a different
                // provider's identically named key. No revision payload is projected here.
                var rows = (
                    await connection.QueryAsync<InputReadMetadata>(
                        new CommandDefinition(
                            """
                            SELECT p.current_revision_uuid AS "Uuid",p.provider AS "Provider",
                                p.resource_type AS "ResourceType",p.provider_key AS "ProviderKey"
                            FROM normalization.input_resources p
                            JOIN unnest(@Providers::text[],@Types::text[],@Keys::text[])
                                AS wanted(provider,resource_type,provider_key)
                                ON p.provider=wanted.provider AND p.resource_type=wanted.resource_type
                                AND p.provider_key=wanted.provider_key
                            WHERE p.current_revision_uuid IS NOT NULL
                            """,
                            new
                            {
                                Providers = requested.Select(key => key.Provider).ToArray(),
                                Types = requested.Select(key => key.ResourceType).ToArray(),
                                Keys = requested.Select(key => key.ProviderKey).ToArray(),
                            },
                            transaction,
                            cancellationToken: cancellationToken
                        )
                    )
                ).ToDictionary(row => row.Resource);
                if (rows.Count != requested.Length)
                {
                    throw new ArgumentException(
                        "A requested resource has no retained current input.",
                        nameof(resources)
                    );
                }
                await transaction.CommitAsync(cancellationToken);
                return requested
                    .Select(key => new NormalizationBindingInputReference(rows[key].Uuid, key))
                    .ToArray();
            }
        );
    }

    // Storage validation can share its existing transaction. Revisions are immutable, so the
    // metadata preflight remains valid even if that transaction uses Read Committed isolation.
    internal static Task<IReadOnlyList<NormalizationInputRevision>> GetInputsAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        IReadOnlyList<NormalizationBindingInputReference> inputs,
        NormalizationInputReadLimits? limits = null,
        CancellationToken cancellationToken = default
    )
    {
        var readLimits = limits ?? new NormalizationInputReadLimits();
        return ReadBoundedInputs(
            connection,
            transaction,
            FreezeReadReferences(inputs, readLimits),
            readLimits,
            cancellationToken
        );
    }

    private static NormalizationBindingInputReference[] FreezeReadReferences(
        IReadOnlyList<NormalizationBindingInputReference> inputs,
        NormalizationInputReadLimits limits
    )
    {
        limits.Validate();
        var requested = inputs.Distinct().ToArray();
        if (requested.Length > limits.MaximumInputs)
        {
            throw new InvalidOperationException(
                "Immutable input read exceeds the input count limit."
            );
        }
        foreach (var input in requested)
        {
            Validate(input.Resource);
            if (input.InputRevisionUuid == Guid.Empty)
            {
                throw new ArgumentException(
                    "An immutable input revision UUID is required.",
                    nameof(inputs)
                );
            }
        }
        if (
            requested.Select(input => input.InputRevisionUuid).Distinct().Count()
            != requested.Length
        )
        {
            throw new ArgumentException(
                "One input revision cannot identify different resources.",
                nameof(inputs)
            );
        }
        return requested;
    }

    private static async Task<IReadOnlyList<NormalizationInputRevision>> ReadBoundedInputs(
        IDbConnection connection,
        IDbTransaction transaction,
        NormalizationBindingInputReference[] requested,
        NormalizationInputReadLimits limits,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = requested.Select(input => input.InputRevisionUuid).ToArray();
        var metadata = (
            await connection.QueryAsync<InputReadMetadata>(
                new CommandDefinition(
                    """
                    SELECT r.uuid AS "Uuid",p.provider AS "Provider",p.resource_type AS "ResourceType",
                        p.provider_key AS "ProviderKey",octet_length(r.payload)::bigint AS "ByteCount"
                    FROM normalization.input_revisions r
                    JOIN normalization.input_resources p ON p.uuid=r.resource_uuid
                    WHERE r.uuid=ANY(@ids)
                    """,
                    new { ids },
                    transaction,
                    cancellationToken: cancellationToken
                )
            )
        ).ToDictionary(row => row.Uuid);
        long totalBytes = 0;
        foreach (var input in requested)
        {
            if (
                !metadata.TryGetValue(input.InputRevisionUuid, out var row)
                || row.Resource != input.Resource
            )
            {
                throw new ArgumentException(
                    "An immutable input reference is missing or belongs to a different resource.",
                    nameof(requested)
                );
            }
            if (
                row.ByteCount > limits.MaximumInputBytes
                || row.ByteCount > limits.MaximumTotalBytes - totalBytes
            )
            {
                throw new InvalidOperationException(
                    "Immutable input read exceeds the payload byte limits."
                );
            }
            totalBytes += row.ByteCount;
        }
        var revisions = (
            await connection.QueryAsync<RevisionRow>(
                new CommandDefinition(
                    $"""
                    SELECT {RevisionColumns} FROM normalization.input_revisions r
                    JOIN normalization.input_resources p ON p.uuid=r.resource_uuid WHERE r.uuid=ANY(@ids)
                    """,
                    new { ids },
                    transaction,
                    cancellationToken: cancellationToken
                )
            )
        ).ToDictionary(row => row.Uuid);
        return requested.Select(input => revisions[input.InputRevisionUuid].ToRevision()).ToArray();
    }

    private sealed class InputReadMetadata
    {
        public Guid Uuid { get; init; }
        public string Provider { get; init; } = null!;
        public string ResourceType { get; init; } = null!;
        public string ProviderKey { get; init; } = null!;
        public long ByteCount { get; init; }
        public NormalizationResourceKey Resource => new(Provider, ResourceType, ProviderKey);
    }
}
