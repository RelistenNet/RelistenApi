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
    private static void ValidateCurrentPreparation(
        string processingVersion,
        NormalizationRecordingPreparation preparation
    )
    {
        if (
            processingVersion == NormalizationRecordingProcessor.Version
            && preparation.ExpectedContributions == null
        )
        {
            throw new ArgumentException(
                "Current recording processing requires its complete contribution membership receipt."
            );
        }
    }

    private static void ValidatePreparation(
        Guid sourceUuid,
        NormalizationRecordingPreparation preparation
    )
    {
        if (
            preparation.ExpectedConfigurationGeneration is < 0
            || preparation.Binding.Resource.Provider != "legacy_catalog"
                && preparation.Contributor == null
        )
        {
            throw new ArgumentException(
                "Recording preparation requires its original admission revisions and contributor identity."
            );
        }
        var membership = preparation.ExpectedContributions;
        if (
            membership != null
            && (
                membership.Any(value => value.Revision < 0)
                || membership.Select(value => value.Contributor).Distinct().Count()
                    != membership.Count
            )
        )
        {
            throw new ArgumentException(
                "Recording preparation requires consistent original contributor membership revisions."
            );
        }
        var inputs = NormalizationRecordingReadSet.Inputs(preparation);
        if (inputs.Select(input => input.Contributor).Distinct().Count() != inputs.Length)
        {
            throw new ArgumentException(
                "A complete recording preparation contains one entry per contributor."
            );
        }
        foreach (var input in inputs)
        {
            ValidatePreparedInput(sourceUuid, input);
        }
    }

    private static void ValidatePreparedInput(
        Guid sourceUuid,
        NormalizationRecordingPreparedInput input
    )
    {
        var binding = input.Binding;
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Contributor.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Contributor.ResourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Contributor.ProviderKey);
        if (
            input.ExpectedContributionRevision < 0
            || binding.InputRevisionUuid == Guid.Empty
            || binding.Inputs.Count != 1
        )
        {
            throw new ArgumentException(
                "Each recording contributor requires its exact single input and admission revision."
            );
        }
        if (binding.Resource.Provider == "legacy_catalog")
        {
            if (
                input.Contributor
                != new NormalizationResourceKey("relisten", "recording", sourceUuid.ToString("D"))
            )
            {
                throw new ArgumentException(
                    "Legacy recording inputs require their source's catalog contributor key."
                );
            }
            ValidateLegacyPreparedInput(sourceUuid, input);
        }
        else
        {
            var supported =
                binding.Resource.Provider == "archive.org"
                    && binding.Resource.ResourceType == "item_metadata"
                || binding.Resource.Provider == "phish.in"
                    && binding.Resource.ResourceType == "show";
            if (
                !supported
                || input.Contributor != binding.Resource
                || input.FetchUuid != null
                || (input.ProducerCatalogInputs?.Count ?? 0) != 0
            )
            {
                throw new ArgumentException(
                    "Retained provider inputs must not invent legacy capture receipts or producer clocks."
                );
            }
        }
        if (
            binding.Recording.Value is { } recording
            && (recording.SourceUuid != sourceUuid || recording.ArtistUuid == Guid.Empty)
        )
        {
            throw new ArgumentException("Recording preparation belongs to another catalog source.");
        }
    }

    private static void ValidateLegacyPreparedInput(
        Guid sourceUuid,
        NormalizationRecordingPreparedInput input
    )
    {
        var binding = input.Binding;
        if (
            input.FetchUuid == null
            || input.FetchUuid == Guid.Empty
            || binding.Resource.ResourceType != "recording"
            || !Guid.TryParse(binding.Resource.ProviderKey, out var resourceSource)
            || resourceSource != sourceUuid
        )
        {
            throw new ArgumentException(
                "Legacy recording preparation requires its original catalog recording input and capture receipt."
            );
        }
        var producer = input.ProducerCatalogInputs ?? [];
        if (
            producer.Count == 0
            || producer.Any(value => value.ArtistUuid == Guid.Empty || value.Generation < 0)
            || producer
                .GroupBy(value => value.ArtistUuid)
                .Any(group => group.Distinct().Count() != 1)
            || producer.Any(value => !binding.CatalogInputs.Contains(value))
        )
        {
            throw new ArgumentException(
                "Recording preparation must retain its producer catalog revisions in the binding read set."
            );
        }
    }

    private static async Task RequirePreparationReceipt(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NormalizationRecordingPreparation preparation,
        CancellationToken ct
    )
    {
        foreach (var input in NormalizationRecordingReadSet.Inputs(preparation))
        {
            var binding = input.Binding;
            var valid = await connection.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    """
                    SELECT EXISTS(SELECT 1 FROM normalization.input_revisions r
                        JOIN normalization.input_resources p ON p.uuid=r.resource_uuid
                        WHERE r.uuid=@InputRevisionUuid AND p.provider=@Provider AND p.resource_type=@ResourceType AND p.provider_key=@ProviderKey
                            AND (@legacy=false OR (r.capture_version=@version AND EXISTS(
                                SELECT 1 FROM normalization.input_fetches f WHERE f.fetch_uuid=@FetchUuid AND f.accepted_as_current
                                    AND f.resource_uuid=p.uuid AND f.input_revision_uuid=r.uuid))))
                    """,
                    new
                    {
                        binding.InputRevisionUuid,
                        binding.Resource.Provider,
                        binding.Resource.ResourceType,
                        binding.Resource.ProviderKey,
                        legacy = binding.Resource.Provider == "legacy_catalog",
                        version = NormalizationLegacyInputService.RefreshCaptureVersion,
                        input.FetchUuid,
                    },
                    transaction,
                    cancellationToken: ct
                )
            );
            if (!valid)
            {
                throw new NormalizationConflictException(
                    "Recording preparation does not identify its original retained input and genuine catalog receipt."
                );
            }
        }
    }
}
