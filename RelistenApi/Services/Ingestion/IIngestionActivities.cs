using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Import;
using Temporalio.Activities;

namespace Relisten.Ingestion;

/// <summary>Durable domain operations used by artist and manual-item workflows.</summary>
public interface IIngestionActivities
{
    /// <summary>Resolves enabled provider mappings.</summary>
    [Activity("Ingestion.PrepareArtist")]
    Task<ArtistImportPlan> PrepareArtist(ArtistImportRequest request);

    /// <summary>Imports prerequisite catalogs before item discovery.</summary>
    [Activity("Ingestion.PrepareProvider")]
    Task<ImportStats> PrepareProvider(ArtistImportRequest request, UpstreamProvider provider);

    /// <summary>Returns a bounded slice of a stable provider discovery snapshot.</summary>
    [Activity("Ingestion.DiscoverItems")]
    Task<ImportPage> DiscoverItems(
        ArtistImportRequest request,
        UpstreamProvider provider,
        DiscoveryCursor? cursor
    );

    /// <summary>Fetches and commits one item. A null item means the request names a manual reimport.</summary>
    [Activity("Ingestion.ImportItem")]
    Task<ItemImportResult> ImportItem(
        ArtistImportRequest request,
        UpstreamProvider provider,
        ImportItem? item
    );

    /// <summary>Runs provider finalization after complete discovery, including verified Archive reconciliation.</summary>
    [Activity("Ingestion.ReconcileProvider")]
    Task<ProviderResult> ReconcileProvider(
        ArtistImportRequest request,
        UpstreamProvider provider,
        string[] ineligibleItems,
        bool hasFailures
    );

    /// <summary>Rebuilds catalog aggregates from committed imports and returns the final counts.</summary>
    [Activity("Ingestion.FinishArtist")]
    Task<ImportResult> FinishArtist(ArtistImportRequest request, ImportResult result);
}
