using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

// Refreshed legacy facts and their producer clocks are one read set. Association-only
// inspection may use old evidence; this path must reject it after any consumed owner changes.
public sealed partial class NormalizationCatalogBindingService
{
    /// <summary>
    /// Binds the exact decoded facts of an admitted v2 catalog refresh. Producer revisions are
    /// checked with current associations in one primary snapshot and returned without advancing
    /// them. A conflict requires a complete refresh and decode, not another association-only bind.
    /// </summary>
    /// <remarks>
    /// Include every returned input revision and catalog dependency in work admission. A source
    /// can remain bound while its historical file/show context is unresolved. This operation
    /// verifies the returned positive associations and their consumed facts. Unbound raw context does
    /// not authorize venue or other entity acceptance; those binders need their own factual read sets.
    /// This operation does not accept extracted credits or titles.
    /// </remarks>
    public Task<NormalizationRecordingBindingSnapshot> BindRefreshedRecordingAsync(
        NormalizationLegacyRecordingSnapshot snapshot,
        ProviderRecordingObservation recording,
        Guid? expectedArtistUuid = null,
        CancellationToken cancellationToken = default
    )
    {
        ValidateRecordingRequest(recording, expectedArtistUuid, cancellationToken);
        var producerClocks = ValidateRefreshedRecording(snapshot, recording);
        // Hash before the first await: caller-owned byte arrays cannot change the receipt we check.
        var payloadHash = Convert.ToHexStringLower(
            SHA256.HashData(snapshot.Capture.Revision.Payload)
        );
        return WithCatalogSnapshotAsync(
            async (connection, transaction, ct) =>
            {
                await RequireRefreshReceiptAsync(
                    connection,
                    transaction,
                    snapshot.Capture,
                    payloadHash,
                    ct
                );
                await RequireProducerClocksAsync(connection, transaction, producerClocks, ct);
                var bound = await BindRecordingSnapshotAsync(
                    connection,
                    transaction,
                    recording,
                    expectedArtistUuid,
                    ct
                );
                var allClocks = producerClocks
                    .Concat(bound.CatalogInputs)
                    .Distinct()
                    .OrderBy(clock => clock.ArtistUuid)
                    .ToArray();
                if (allClocks.GroupBy(clock => clock.ArtistUuid).Any(group => group.Count() != 1))
                {
                    throw RefreshConflict();
                }
                return bound with { CatalogInputs = allClocks };
            },
            cancellationToken
        );
    }

    private static NormalizationCatalogDependency[] ValidateRefreshedRecording(
        NormalizationLegacyRecordingSnapshot snapshot,
        ProviderRecordingObservation recording
    )
    {
        var input = snapshot.Capture.Revision;
        if (
            input.CaptureVersion != NormalizationLegacyInputService.RefreshCaptureVersion
            || input.Resource.Provider != "legacy_catalog"
            || input.Resource.ResourceType != "recording"
        )
        {
            throw new ArgumentException("Checked legacy binding requires a v2 recording refresh.");
        }
        if (
            !snapshot.Capture.Fetch.AcceptedAsCurrent
            || snapshot.Capture.Fetch.InputRevisionUuid != input.Uuid
            || recording.Origin.Item != input.Resource
            || recording.Origin.Evidence.InputRevisionUuid != input.Uuid
        )
        {
            throw RefreshConflict();
        }
        var clocks = snapshot.CatalogInputs.Distinct().OrderBy(clock => clock.ArtistUuid).ToArray();
        if (
            clocks.Length == 0
            || clocks.Any(clock => clock.ArtistUuid == Guid.Empty || clock.Generation < 0)
            || clocks.GroupBy(clock => clock.ArtistUuid).Any(group => group.Count() != 1)
        )
        {
            throw RefreshConflict();
        }
        return clocks;
    }

    private static async Task RequireRefreshReceiptAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationCaptureResult capture,
        string payloadHash,
        CancellationToken ct
    )
    {
        var input = capture.Revision;
        var valid = await c.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT EXISTS (
                    SELECT 1 FROM normalization.input_fetches f
                    JOIN normalization.input_revisions r ON r.uuid=f.input_revision_uuid
                    JOIN normalization.input_resources p ON p.uuid=f.resource_uuid AND p.uuid=r.resource_uuid
                    WHERE f.fetch_uuid=@FetchUuid AND f.input_revision_uuid=@inputUuid
                        AND f.accepted_as_current AND f.resource_generation=@ResourceGeneration
                        AND f.expected_generation=@ExpectedGeneration
                        AND r.sha256=@payloadHash AND r.media_type=@MediaType AND r.capture_version=@CaptureVersion
                        AND p.provider=@Provider AND p.resource_type=@ResourceType AND p.provider_key=@ProviderKey)
                """,
                new
                {
                    capture.Fetch.FetchUuid,
                    inputUuid = input.Uuid,
                    capture.Fetch.ResourceGeneration,
                    capture.Fetch.ExpectedGeneration,
                    payloadHash,
                    input.MediaType,
                    input.CaptureVersion,
                    input.Resource.Provider,
                    input.Resource.ResourceType,
                    input.Resource.ProviderKey,
                },
                t,
                cancellationToken: ct
            )
        );
        if (!valid)
        {
            throw RefreshConflict();
        }
    }

    private static async Task RequireProducerClocksAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationCatalogDependency[] expected,
        CancellationToken ct
    )
    {
        var current = (
            await c.QueryAsync<NormalizationCatalogDependency>(
                new CommandDefinition(
                    """
                    SELECT artist_uuid AS "ArtistUuid",generation AS "Generation"
                    FROM normalization.catalog_artist_revisions WHERE artist_uuid=ANY(@artists)
                    """,
                    new { artists = expected.Select(clock => clock.ArtistUuid).ToArray() },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (current.Length != expected.Length || expected.Any(clock => !current.Contains(clock)))
        {
            throw RefreshConflict();
        }
    }

    private static NormalizationConflictException RefreshConflict() =>
        new(
            "Retained catalog facts or their catalog context changed. Refresh and decode the complete catalog resource again before binding."
        );
}
