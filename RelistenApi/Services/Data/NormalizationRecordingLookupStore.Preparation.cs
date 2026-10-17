using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRecordingLookupStore
{
    private static void ValidateRequest(NormalizationRecordingLookupRequest request)
    {
        if (!Enum.IsDefined(request.Mode) || request.ExpectedArtistUuid == Guid.Empty)
        {
            throw new ArgumentException(
                "A recording lookup requires a supported mode and artist scope."
            );
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Resource.ProviderKey);
        if (request.Mode == NormalizationRecordingLookupMode.LegacyCatalog)
        {
            if (
                request.Resource.Provider != "legacy_catalog"
                || request.Resource.ResourceType != "recording"
                || !Guid.TryParseExact(request.Resource.ProviderKey, "D", out var uuid)
                || uuid == Guid.Empty
                || request.Resource.ProviderKey != uuid.ToString("D")
                || request.Input != null
            )
            {
                throw new ArgumentException(
                    "A legacy lookup requires its canonical source UUID resource and no fabricated input."
                );
            }
            return;
        }
        var supported =
            request.Resource.Provider == "archive.org"
                && request.Resource.ResourceType == "item_metadata"
            || request.Resource.Provider == "phish.in"
                && request.Resource.ResourceType == "show"
                && long.TryParse(
                    request.Resource.ProviderKey,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var id
                )
                && id > 0
                && id.ToString(CultureInfo.InvariantCulture) == request.Resource.ProviderKey;
        if (
            !supported
            || request.Input == null
            || request.Input.Resource != request.Resource
            || request.Input.InputRevisionUuid == Guid.Empty
        )
        {
            throw new ArgumentException(
                "A provider lookup requires its exact supported recording input reference."
            );
        }
    }

    private static void ValidatePreparation(
        NormalizationRecordingLookupAttempt attempt,
        NormalizationRecordingLookupPreparation preparation
    )
    {
        if (
            (preparation.Binding == null) == (preparation.AbsenceProbe == null)
            || preparation.ExpectedConfigurationGeneration < 0
        )
        {
            throw new ArgumentException(
                "A lookup preparation requires exactly one binding or absent-source probe."
            );
        }
        if (preparation.AbsenceProbe is { } absence)
        {
            if (
                attempt.Request.Mode != NormalizationRecordingLookupMode.LegacyCatalog
                || absence.Exists
                || absence.Dependency.SourceUuid != Guid.Parse(attempt.Request.Resource.ProviderKey)
                || absence.Dependency.Generation < 0
                || preparation.FetchUuid != null
                || preparation.ProducerCatalogInputs != null
                || preparation.LegacyPreparation != null
                || preparation.ProcessingAttemptUuid != null
                || preparation.ProcessingVersion != null
            )
            {
                throw new ArgumentException(
                    "Only an exact observed legacy absence can prepare an input-free lookup."
                );
            }
            return;
        }

        var binding = preparation.Binding!;
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.Recording.Reason);
        if (!Enum.IsDefined(binding.Recording.Status))
        {
            throw new ArgumentException("Lookup binding has an unsupported result status.");
        }
        if (
            binding.Resource != attempt.Request.Resource
            || binding.InputRevisionUuid == Guid.Empty
            || (binding.Recording.Status == NormalizationCatalogBindingStatus.Bound)
                != (binding.Recording.Value != null)
        )
        {
            throw new ArgumentException("Lookup binding does not match its requested recording.");
        }
        var bound = binding.Recording.Value;
        if (
            bound != null
            && (
                bound.SourceUuid == Guid.Empty
                || bound.ArtistUuid == Guid.Empty
                || attempt.Request.ExpectedArtistUuid is { } expected
                    && expected != bound.ArtistUuid
            )
        )
        {
            throw new ArgumentException("Bound lookup does not match its artist scope.");
        }
        if (
            bound == null
            && (
                preparation.ProcessingAttemptUuid != null
                || preparation.ProcessingVersion != null
                || preparation.LegacyPreparation != null
            )
        )
        {
            throw new ArgumentException(
                "An unresolved lookup cannot prepare downstream recording processing."
            );
        }
        if (
            bound != null
            && (
                preparation.ProcessingAttemptUuid == null
                || preparation.ProcessingAttemptUuid == Guid.Empty
            )
        )
        {
            throw new ArgumentException(
                "A bound lookup requires its stable downstream attempt UUID."
            );
        }
        if (attempt.Request.Mode == NormalizationRecordingLookupMode.LegacyCatalog)
        {
            RequireLegacyPreparation(attempt, preparation);
        }
        else if (
            preparation.FetchUuid != null
            || preparation.ProducerCatalogInputs != null
            || preparation.LegacyPreparation != null
            || preparation.ProcessingVersion != null
            || binding.Inputs.Count != 1
            || binding.Inputs[0] != attempt.Request.Input
        )
        {
            throw new ArgumentException(
                "A retained provider lookup must retain its exact input without inventing a legacy consumer."
            );
        }
    }

    private static void RequireLegacyPreparation(
        NormalizationRecordingLookupAttempt attempt,
        NormalizationRecordingLookupPreparation preparation
    )
    {
        if (
            preparation.FetchUuid == null
            || preparation.FetchUuid == Guid.Empty
            || preparation.ProducerCatalogInputs is not { Count: > 0 }
            || preparation.ProducerCatalogInputs.Any(input =>
                input.ArtistUuid == Guid.Empty || input.Generation < 0
            )
            || preparation
                .ProducerCatalogInputs.GroupBy(input => input.ArtistUuid)
                .Any(group => group.Distinct().Count() != 1)
            || preparation.ProducerCatalogInputs.Any(input =>
                !preparation.Binding!.CatalogInputs.Contains(input)
            )
        )
        {
            throw new ArgumentException(
                "Legacy binding requires its admitted capture and original producer clocks."
            );
        }
        if (preparation.Binding!.Recording.Value is not { } bound)
        {
            return;
        }
        var downstream = preparation.LegacyPreparation;
        if (
            bound.SourceUuid != Guid.Parse(attempt.Request.Resource.ProviderKey)
            || downstream == null
            || string.IsNullOrWhiteSpace(preparation.ProcessingVersion)
            || downstream.ExpectedConfigurationGeneration is < 0
            || downstream.ExpectedContributionRevision < 0
            || downstream.FetchUuid != preparation.FetchUuid
            || !SameJson(
                JsonSerializer.Serialize(downstream.Binding),
                JsonSerializer.Serialize(preparation.Binding)
            )
            || !SameJson(
                JsonSerializer.Serialize(downstream.ProducerCatalogInputs),
                JsonSerializer.Serialize(preparation.ProducerCatalogInputs)
            )
        )
        {
            throw new ArgumentException(
                "A bound legacy handoff must retain the same prepared recording facts and capture."
            );
        }
    }

    private static async Task RequirePreparationInputs(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationRecordingLookupAttempt attempt,
        NormalizationRecordingLookupPreparation preparation,
        CancellationToken ct
    )
    {
        if (preparation.Binding == null)
        {
            return;
        }
        foreach (var input in preparation.Binding.Inputs)
        {
            await RequireInputReference(c, t, input, ct);
        }
        if (attempt.Request.Mode != NormalizationRecordingLookupMode.LegacyCatalog)
        {
            return;
        }
        var binding = preparation.Binding;
        var admitted = await c.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT EXISTS(SELECT 1 FROM normalization.input_fetches f
                    JOIN normalization.input_revisions r ON r.uuid=f.input_revision_uuid
                    JOIN normalization.input_resources p ON p.uuid=r.resource_uuid AND p.uuid=f.resource_uuid
                    WHERE f.fetch_uuid=@FetchUuid AND f.accepted_as_current AND r.uuid=@InputRevisionUuid
                        AND r.capture_version=@version AND p.provider=@Provider
                        AND p.resource_type=@ResourceType AND p.provider_key=@ProviderKey)
                """,
                new
                {
                    preparation.FetchUuid,
                    binding.InputRevisionUuid,
                    version = NormalizationLegacyInputService.RefreshCaptureVersion,
                    binding.Resource.Provider,
                    binding.Resource.ResourceType,
                    binding.Resource.ProviderKey,
                },
                t,
                cancellationToken: ct
            )
        );
        if (!admitted)
        {
            throw new NormalizationConflictException(
                "Lookup preparation does not identify its admitted immutable legacy capture."
            );
        }
    }

    private static async Task RequireInputReference(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationBindingInputReference input,
        CancellationToken ct
    )
    {
        var exists = await c.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT EXISTS(SELECT 1 FROM normalization.input_revisions r
                    JOIN normalization.input_resources p ON p.uuid=r.resource_uuid
                    WHERE r.uuid=@InputRevisionUuid AND p.provider=@Provider
                        AND p.resource_type=@ResourceType AND p.provider_key=@ProviderKey)
                """,
                new
                {
                    input.InputRevisionUuid,
                    input.Resource.Provider,
                    input.Resource.ResourceType,
                    input.Resource.ProviderKey,
                },
                t,
                cancellationToken: ct
            )
        );
        if (!exists)
        {
            throw new NormalizationConflictException(
                "Lookup input reference does not identify retained evidence."
            );
        }
    }
}
