using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Ingestion;
using Temporalio.Client;

namespace RelistenIngestionWorker;

// Translates CLI options into the same typed admission calls used by HTTP adapters.
// It returns a printable result; Program owns process lifetime and error reporting.
internal sealed class IngestionOperatorCommands(
    TemporalIngestionClient imports,
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

        var artistId = await ResolveArtistAsync(configuration[operation]!);

        return operation switch
        {
            "rebuild-artist" => (
                await imports.RebuildArtistCatalogAsync(new RebuildArtistCatalog(artistId))
            ).WorkflowId,
            "repair-jerry-venues" => (
                await imports.RepairJerryVenuesAsync(new RepairJerryVenues(artistId))
            ).WorkflowId,
            "import" => await ImportArtistAsync(artistId),
            _ => throw new InvalidOperationException($"Unknown operator command: {operation}"),
        };
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
