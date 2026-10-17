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

public sealed partial class NormalizationWorkService
{
    private static NormalizationBindingInputReference[] NormalizeExpectedInputRevisions(
        IReadOnlyList<NormalizationBindingInputReference>? expected,
        IReadOnlyList<NormalizationResourceKey> ownedResources
    )
    {
        var normalized = (expected ?? []).Distinct().ToArray();
        foreach (var input in normalized)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(input.Resource.Provider);
            ArgumentException.ThrowIfNullOrWhiteSpace(input.Resource.ResourceType);
            ArgumentException.ThrowIfNullOrWhiteSpace(input.Resource.ProviderKey);
            if (input.InputRevisionUuid == Guid.Empty || !ownedResources.Contains(input.Resource))
            {
                throw new ArgumentException(
                    "Expected input revisions must identify a revision of an incoming owned resource.",
                    nameof(expected)
                );
            }
        }
        if (normalized.GroupBy(input => input.Resource).Any(group => group.Count() > 1))
        {
            throw new ArgumentException(
                "One input resource cannot require conflicting revisions.",
                nameof(expected)
            );
        }
        return normalized
            .OrderBy(input => input.Resource.Provider, StringComparer.Ordinal)
            .ThenBy(input => input.Resource.ResourceType, StringComparer.Ordinal)
            .ThenBy(input => input.Resource.ProviderKey, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Optional read expectations extend the durable request identity, not owner configuration.
    /// Keep omitted fields absent so work admitted by earlier releases still replays exactly.
    /// </summary>
    private static string HashWorkRequest(
        JsonElement request,
        NormalizationMappingRegistryDependency? mappingRegistry,
        NormalizationBindingInputReference[] expectedInputs,
        NormalizationImportedVenueDependency[] importedVenues,
        NormalizationCandidateUniverseDependency[] candidateUniverses,
        NormalizationSubjectObservation[] ownerObservations
    )
    {
        if (
            mappingRegistry == null
            && expectedInputs.Length == 0
            && importedVenues.Length == 0
            && candidateUniverses.Length == 0
            && ownerObservations.Length == 0
        )
        {
            return Hash(request);
        }
        var fields = request
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value);
        if (mappingRegistry != null)
        {
            fields.Add("mappingRegistry", JsonSerializer.SerializeToElement(mappingRegistry));
        }
        if (expectedInputs.Length > 0)
        {
            fields.Add("expectedInputRevisions", JsonSerializer.SerializeToElement(expectedInputs));
        }
        if (importedVenues.Length > 0)
        {
            fields.Add("importedVenues", JsonSerializer.SerializeToElement(importedVenues));
        }
        if (candidateUniverses.Length > 0)
        {
            fields.Add("candidateUniverses", JsonSerializer.SerializeToElement(candidateUniverses));
        }
        if (ownerObservations.Length > 0)
        {
            fields.Add(
                "expectedOwnerObservations",
                JsonSerializer.SerializeToElement(ownerObservations)
            );
        }
        return Hash(JsonSerializer.SerializeToElement(fields));
    }

    private static async Task RequireExpectedInputRevisions(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NormalizationBindingInputReference[] expected,
        CancellationToken ct
    )
    {
        if (expected.Length == 0)
        {
            return;
        }
        // LockInputs already holds these resources. Check each key/revision pair together:
        // a revision from another owned resource cannot satisfy an expectation accidentally.
        var matches = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                SELECT count(*) FROM normalization.input_resources r
                JOIN jsonb_to_recordset(@expected::jsonb) AS e("Provider" text,"ResourceType" text,"ProviderKey" text,"InputRevisionUuid" uuid)
                    ON r.provider=e."Provider" AND r.resource_type=e."ResourceType" AND r.provider_key=e."ProviderKey"
                    AND r.current_revision_uuid=e."InputRevisionUuid"
                """,
                new
                {
                    expected = JsonSerializer.Serialize(
                        expected.Select(input => new
                        {
                            input.Resource.Provider,
                            input.Resource.ResourceType,
                            input.Resource.ProviderKey,
                            input.InputRevisionUuid,
                        })
                    ),
                },
                transaction,
                cancellationToken: ct
            )
        );
        if (matches != expected.Length)
        {
            throw new NormalizationConflictException(
                "Input revisions changed after they were read; reload the binding before starting normalization work."
            );
        }
    }
}
