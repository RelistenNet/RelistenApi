using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationSetlistProposalService
{
    private static void ValidateRequest(NormalizationSetlistProcessingRequest request)
    {
        RequireUuid(request.Input.InputRevisionUuid);
        ValidateResource(request.Input.Resource);
        ValidateResource(request.AccountIdentity);
        var supported = request.AccountIdentity switch
        {
            { Provider: "jerrygarcia.com", ResourceType: "mirror_show_page" } => true,
            { Provider: "setlist.fm", ResourceType: "setlist" } => true,
            { Provider: "phantasytour.com", ResourceType: "show" } => true,
            { Provider: "phish.net", ResourceType: "show" or "setlist_page" } => true,
            _ => false,
        };
        if (
            !supported
            || request.AccountIdentity.Provider != request.Input.Resource.Provider
            || request.ExpectedArtistUuid == Guid.Empty
            || request.AccountIdentity.Provider == "phish.net" && request.ExpectedArtistUuid == null
        )
        {
            throw new ArgumentException(
                "Setlist processing requires a supported qualified account and its explicit artist scope when required."
            );
        }
    }

    private static void ValidateResource(NormalizationResourceKey resource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.ResourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.ProviderKey);
    }

    private static async Task<NormalizationSetlistInputExtraction[]> ValidatePreparation(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationSetlistProcessingAttempt attempt,
        NormalizationSetlistPreparation preparation,
        CancellationToken ct
    )
    {
        if (
            preparation.ExpectedConfigurationGeneration is < 0
            || preparation.Inputs.Count == 0
            || preparation.Inputs.Select(input => input.Contributor).Distinct().Count()
                != preparation.Inputs.Count
            || preparation.Inputs.Select(input => input.Input.InputRevisionUuid).Distinct().Count()
                != preparation.Inputs.Count
            || !preparation.Inputs.Any(input => input.Input == attempt.Request.Input)
            || preparation.ExpectedContributions.Any(input => input.Revision < 0)
            || preparation
                .ExpectedContributions.Select(input => input.Contributor)
                .Distinct()
                .Count() != preparation.ExpectedContributions.Count
        )
        {
            throw new ArgumentException(
                "Setlist preparation requires the complete original input and contributor membership snapshot."
            );
        }
        foreach (var contribution in preparation.ExpectedContributions)
        {
            ValidateResource(contribution.Contributor);
        }
        // Metadata and aggregate byte limits are checked before any payload is projected.
        // Immutable revisions keep that preflight valid in this attempt transaction.
        var inputs = (
            await NormalizationEvidenceService.GetInputsAsync(
                c,
                t,
                preparation.Inputs.Select(group => group.Input).ToArray(),
                cancellationToken: ct
            )
        ).ToDictionary(input => input.Uuid);
        var extractions = new List<NormalizationSetlistInputExtraction>();
        foreach (var group in preparation.Inputs)
        {
            ct.ThrowIfCancellationRequested();
            ValidateResource(group.Contributor);
            if (
                group.ExpectedContributionRevision < 0
                || group.Contributor != group.Input.Resource
                || group.Accounts.Count == 0
            )
            {
                throw new ArgumentException(
                    "Each setlist input must retain its resource contributor and requested account observations."
                );
            }
            var observed = preparation.ExpectedContributions.SingleOrDefault(item =>
                item.Contributor == group.Contributor
            );
            if ((observed?.Revision ?? 0) != group.ExpectedContributionRevision)
            {
                throw new ArgumentException(
                    "Setlist input contribution revisions must match the original membership snapshot."
                );
            }
            var input = inputs[group.Input.InputRevisionUuid];
            var decoded = new NormalizationProviderDecoder().Decode(input, ct);
            var accounts = decoded
                .Setlists.Where(account =>
                    account.Origin.Item == attempt.Registration.AccountIdentity
                )
                .ToArray();
            var originalClaims = accounts
                .Select(account => JsonSerializer.Serialize(account))
                .Order(StringComparer.Ordinal);
            var preparedClaims = group
                .Accounts.Select(account => JsonSerializer.Serialize(account.Account))
                .Order(StringComparer.Ordinal);
            if (!originalClaims.SequenceEqual(preparedClaims, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    "Preparation must retain every original observation of the requested account, including duplicates."
                );
            }
            foreach (var account in group.Accounts)
            {
                var binding = account.Binding;
                if (
                    binding.InputRevisionUuid != input.Uuid
                    || binding.Resource != input.Resource
                    || binding.Account != account.Account.Origin
                    || binding.ArtistScopeUuid
                        != (
                            attempt.Registration.AccountIdentity.Provider == "phish.net"
                                ? attempt.Registration.ExpectedArtistUuid
                                : null
                        )
                )
                {
                    throw new ArgumentException(
                        "Setlist bindings must describe their exact retained account and requested artist scope."
                    );
                }
            }
            // The pure extractor also checks all nested account citations against the original
            // retained document. It neither refreshes catalog facts nor accepts identities.
            extractions.Add(new NormalizationSetlistExtractor().Extract(input, decoded, group, ct));
        }
        return extractions.ToArray();
    }

    private static async Task RequireInputReference(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationBindingInputReference reference,
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
                    reference.InputRevisionUuid,
                    reference.Resource.Provider,
                    reference.Resource.ResourceType,
                    reference.Resource.ProviderKey,
                },
                t,
                cancellationToken: ct
            )
        );
        if (!exists)
        {
            throw new ArgumentException(
                "Setlist processing requires the actual retained input revision and its own resource."
            );
        }
    }
}
