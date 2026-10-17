using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRecordingProposalService
{
    private static void ValidatePreparation(
        Guid sourceUuid,
        NormalizationRecordingPreparation preparation
    )
    {
        var binding = preparation.Binding;
        if (
            preparation.FetchUuid == Guid.Empty
            || binding.InputRevisionUuid == Guid.Empty
            || binding.Resource.Provider != "legacy_catalog"
            || binding.Resource.ResourceType != "recording"
            || !Guid.TryParse(binding.Resource.ProviderKey, out var resourceSource)
            || resourceSource != sourceUuid
            || preparation.ExpectedConfigurationGeneration is < 0
            || preparation.ExpectedContributionRevision < 0
        )
        {
            throw new ArgumentException(
                "Recording preparation requires its original catalog recording input and admission revisions."
            );
        }
        if (
            preparation.ProducerCatalogInputs.Count == 0
            || preparation.ProducerCatalogInputs.Any(input =>
                input.ArtistUuid == Guid.Empty || input.Generation < 0
            )
            || preparation
                .ProducerCatalogInputs.GroupBy(input => input.ArtistUuid)
                .Any(group => group.Distinct().Count() != 1)
            || preparation.ProducerCatalogInputs.Any(input =>
                !binding.CatalogInputs.Contains(input)
            )
        )
        {
            throw new ArgumentException(
                "Recording preparation must retain its producer catalog revisions in the binding read set."
            );
        }
        if (
            binding.Recording.Value is { } recording
            && (recording.SourceUuid != sourceUuid || recording.ArtistUuid == Guid.Empty)
        )
        {
            throw new ArgumentException("Recording preparation belongs to another catalog source.");
        }
    }

    private static async Task RequirePreparationReceipt(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NormalizationRecordingPreparation preparation,
        CancellationToken ct
    )
    {
        var input = preparation.Binding;
        var valid = await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT EXISTS(SELECT 1 FROM normalization.input_fetches f
                    JOIN normalization.input_revisions r ON r.uuid=f.input_revision_uuid
                    JOIN normalization.input_resources p ON p.uuid=f.resource_uuid AND p.uuid=r.resource_uuid
                    WHERE f.fetch_uuid=@FetchUuid AND f.accepted_as_current AND r.uuid=@InputRevisionUuid
                        AND r.capture_version=@version
                        AND p.provider=@Provider AND p.resource_type=@ResourceType AND p.provider_key=@ProviderKey)
                """,
                new
                {
                    preparation.FetchUuid,
                    input.InputRevisionUuid,
                    version = NormalizationLegacyInputService.RefreshCaptureVersion,
                    input.Resource.Provider,
                    input.Resource.ResourceType,
                    input.Resource.ProviderKey,
                },
                transaction,
                cancellationToken: ct
            )
        );
        if (!valid)
        {
            throw new NormalizationConflictException(
                "Recording preparation does not identify an admitted immutable catalog capture."
            );
        }
    }
}
