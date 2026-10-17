using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCatalogBindingService
{
    /// <summary>
    /// Verifies the immutable page once, then binds every matching account in one primary
    /// snapshot. Duplicate observations remain separate, without repeatedly loading or
    /// decoding a page for each member. This does not choose the latest account edition.
    /// </summary>
    internal async Task<
        IReadOnlyList<NormalizationPreparedSetlistAccount>
    > BindSetlistAccountGroupAsync(
        NormalizationInputRevision input,
        NormalizationResourceKey accountIdentity,
        Guid? expectedArtistUuid,
        CancellationToken cancellationToken
    )
    {
        var retained = (
            await new NormalizationEvidenceService(db).GetInputsAsync(
                [new(input.Uuid, input.Resource)],
                cancellationToken: cancellationToken
            )
        ).Single();
        if (
            retained.MediaType != input.MediaType
            || retained.CaptureVersion != input.CaptureVersion
            || !retained.Payload.AsSpan().SequenceEqual(input.Payload)
        )
        {
            throw new ArgumentException(
                "The setlist page differs from its immutable retained input."
            );
        }
        var decoded = new NormalizationProviderDecoder().Decode(retained, cancellationToken);
        if (
            decoded.Setlists.Count > NormalizationSetlistExtractor.MaximumAccountsPerInput
            || decoded.Setlists.Sum(account => (long)account.Passages.Count)
                > NormalizationSetlistExtractor.MaximumPassagesPerInput
        )
        {
            throw new ArgumentException(
                "The setlist page exceeds account or passage processing limits."
            );
        }
        var accounts = decoded
            .Setlists.Where(account => account.Origin.Item == accountIdentity)
            .ToArray();
        return await WithCatalogSnapshotAsync<IReadOnlyList<NormalizationPreparedSetlistAccount>>(
            async (connection, transaction, ct) =>
            {
                var registry =
                    await connection.QuerySingleAsync<NormalizationMappingRegistryDependency>(
                        new CommandDefinition(
                            "SELECT generation AS Generation FROM normalization.catalog_mapping_registry_revision WHERE singleton",
                            transaction: transaction,
                            cancellationToken: ct
                        )
                    );
                var results = new List<NormalizationPreparedSetlistAccount>();
                foreach (var account in accounts)
                {
                    var binding = retained.Resource.Provider switch
                    {
                        "jerrygarcia.com" => await BindJerrySetlistSnapshotAsync(
                            connection,
                            transaction,
                            retained,
                            account,
                            registry,
                            expectedArtistUuid,
                            ct
                        ),
                        "setlist.fm" => await BindSetlistFmSnapshotAsync(
                            connection,
                            transaction,
                            retained,
                            account,
                            registry,
                            expectedArtistUuid,
                            ct
                        ),
                        "phantasytour.com" => await BindPhantasyTourSetlistSnapshotAsync(
                            connection,
                            transaction,
                            retained,
                            account,
                            registry,
                            expectedArtistUuid,
                            ct
                        ),
                        "phish.net" => await BindPhishNetSetlistSnapshotAsync(
                            connection,
                            transaction,
                            retained,
                            account,
                            registry,
                            expectedArtistUuid,
                            ct
                        ),
                        _ => throw new NotSupportedException(
                            "This provider has no setlist binding contract."
                        ),
                    };
                    results.Add(new(account, binding));
                }
                return results;
            },
            cancellationToken
        );
    }
}
