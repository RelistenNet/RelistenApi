using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Ingestion;
using Relisten.Ingestion.Workflows;
using Relisten.Normalization;
using Relisten.Normalization.Temporal;
using Temporalio.Client;

namespace RelistenIngestionWorker;

// Translates CLI options into the same typed admission calls used by HTTP adapters.
// It returns a printable result; Program owns process lifetime and error reporting.
internal sealed class IngestionOperatorCommands(
    TemporalIngestionClient imports,
    NormalizationClient normalization,
    ITemporalClient temporal,
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration
)
{
    public async Task<string> RunAsync(string operation)
    {
        if (operation == "cancel")
        {
            return await CancelAsync(configuration[operation]!);
        }

        if (operation == "batch")
        {
            return await StartBatchAsync(configuration[operation]!);
        }

        // Venues run only for the artists switched on (features.norm_venues): --norm-venues=all
        // is every one of them; one artist must be switched on first.
        if (operation == "norm-venues")
        {
            var scope = configuration[operation]!;
            if (scope == "all")
            {
                return await normalization.BuildVenueCatalogueAsync(
                    new BuildVenueCatalogue(null, MaxChunks(), FlaggedOnly: true)
                );
            }

            var artist = (await ResolveArtistAsync(scope)).Value;
            using (var flagScope = scopeFactory.CreateScope())
            {
                if (
                    !await flagScope
                        .ServiceProvider.GetRequiredService<NormFlags>()
                        .VenuesOnAsync(artist)
                )
                {
                    throw new InvalidOperationException(
                        $"Venues are off for {scope}: switch features.norm_venues on first"
                    );
                }
            }

            return await normalization.BuildVenueCatalogueAsync(
                new BuildVenueCatalogue([artist], MaxChunks())
            );
        }

        var artistId = await ResolveArtistAsync(configuration[operation]!);

        return operation switch
        {
            "rebuild-artist" => (
                await imports.RebuildArtistCatalogAsync(new RebuildArtistCatalog(artistId))
            ).WorkflowId,
            "norm-songs" => await normalization.BuildSongCatalogueAsync(
                new BuildSongCatalogue(artistId.Value, MaxChunks(), ReaskTitles())
            ),
            "repair-jerry-venues" => (
                await imports.RepairJerryVenuesAsync(new RepairJerryVenues(artistId))
            ).WorkflowId,
            "import" => await ImportArtistAsync(artistId),
            _ => throw new InvalidOperationException($"Unknown operator command: {operation}"),
        };
    }

    /// --max-chunks caps a run's model calls; a value that isn't a positive number is an error,
    /// never an uncapped run.
    private int? MaxChunks()
    {
        var value = configuration["max-chunks"];
        if (value == null)
        {
            return null;
        }

        return int.TryParse(value, out var max) && max > 0
            ? max
            : throw new InvalidImportRequest(
                $"--max-chunks must be a positive number, not '{value}'."
            );
    }

    /// --reask="A|B" or --reask-file=path (one title per line): titles to ask about again.
    private IReadOnlyList<string>? ReaskTitles()
    {
        var titles = configuration["reask-file"] is { } file
            ? File.ReadAllLines(file)
            : (configuration["reask"] ?? "").Split('|');
        var list = titles.Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        return list.Count == 0 ? null : list;
    }

    private async Task<ArtistId> ResolveArtistAsync(string identifier)
    {
        // Batch and cancellation commands do not need an artist lookup. Resolve
        // catalog services here so those commands do not open a database scope.
        using var scope = scopeFactory.CreateScope();
        var artists = scope.ServiceProvider.GetRequiredService<ArtistService>();
        var artist =
            await artists.FindArtistForImport(identifier)
            ?? throw new InvalidImportRequest("Artist was not found.");

        return new ArtistId(artist.uuid);
    }

    private async Task<string> CancelAsync(string workflowId)
    {
        // Unlike an admission handle, this operator command intentionally selects
        // the currently active execution for the supplied workflow ID.
        await temporal.GetWorkflowHandle(workflowId).CancelAsync();
        return $"Cancellation requested for the currently active execution of {workflowId}";
    }

    private async Task<string> StartBatchAsync(string mode)
    {
        return mode switch
        {
            "full" => (await imports.RefreshCatalogAsync(new RefreshCatalog())).WorkflowId,
            "thin" => (
                await imports.RefreshRecentlyActiveArtistsAsync(new RefreshRecentlyActiveArtists())
            ).WorkflowId,
            "rebuild" => (await imports.RebuildCatalogAsync(new RebuildCatalog())).WorkflowId,
            "discover" => (
                await imports.DiscoverArchiveArtistsAsync(new DiscoverArchiveArtists())
            ).WorkflowId,
            _ => throw new InvalidImportRequest("Expected batch full, thin, rebuild, or discover."),
        };
    }

    private async Task<string> ImportArtistAsync(ArtistId artistId)
    {
        var provider = ReadProvider();
        var year = ReadYear();

        if (configuration["item"] is { } item)
        {
            return await ImportArchiveItemAsync(artistId, item, provider, year);
        }

        if (
            year != null
            && provider
                is not (
                    null
                    or UpstreamProvider.ArchiveOrg
                    or UpstreamProvider.Phishin
                    or UpstreamProvider.PhishNet
                )
        )
        {
            throw new InvalidImportRequest("This provider does not support a year filter.");
        }

        var request = CreateRefreshRequest(artistId, provider, year);
        return (await imports.RefreshAsync(request)).WorkflowId;
    }

    private async Task<string> ImportArchiveItemAsync(
        ArtistId artistId,
        string item,
        UpstreamProvider? provider,
        int? year
    )
    {
        if (year != null)
        {
            throw new InvalidImportRequest("A targeted item cannot also specify a year.");
        }

        if (provider is not (null or UpstreamProvider.ArchiveOrg))
        {
            throw new UnsupportedImportOperation("Only Archive supports targeted item imports.");
        }

        var request = new ImportArchiveItem(artistId, new ArchiveItemId(item));
        return (await imports.ImportArchiveItemAsync(request)).WorkflowId;
    }

    private UpstreamProvider? ReadProvider()
    {
        if (configuration["provider"] is not { } name)
        {
            return null;
        }

        try
        {
            return UpstreamProviders.Parse(name);
        }
        catch (ArgumentException error)
        {
            throw new InvalidImportRequest(error.Message);
        }
    }

    private int? ReadYear()
    {
        if (configuration["year"] is not { } text)
        {
            return null;
        }

        if (!int.TryParse(text, out var year) || year is < 1 or > 9999)
        {
            throw new InvalidImportRequest("Year must be between 1 and 9999.");
        }

        return year;
    }

    private static ArtistRefreshCommand CreateRefreshRequest(
        ArtistId artistId,
        UpstreamProvider? provider,
        int? year
    )
    {
        return provider switch
        {
            // An unscoped year retains the catalog's existing per-provider filter
            // semantics; the typed provider commands interpret their own year field.
            null => year.HasValue
                ? new RefreshFilteredArtist(artistId, year.Value)
                : new RefreshArtist(artistId),
            UpstreamProvider.ArchiveOrg => new RefreshArchiveArtist(artistId, year),
            UpstreamProvider.Phishin => new RefreshPhishInArtist(artistId, year),
            UpstreamProvider.PhishNet => new RefreshPhishNetArtist(artistId, year),
            UpstreamProvider.SetlistFm => new RefreshSetlistFmArtist(artistId),
            UpstreamProvider.PhantasyTour => new RefreshPhantasyTourArtist(artistId),
            UpstreamProvider.JerryGarcia => new RefreshJerryArtist(artistId),
            UpstreamProvider.Local => new RefreshLocalArtist(artistId),
            _ => throw new UnsupportedImportOperation(
                "This provider has no active ingestion implementation."
            ),
        };
    }
}
