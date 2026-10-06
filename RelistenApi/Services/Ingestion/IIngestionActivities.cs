using System.Threading.Tasks;
using Relisten.Import;
using Temporalio.Activities;

namespace Relisten.Ingestion;

/// <summary>Durable boundaries shared by full-artist and single-item workflow entry points.</summary>
public interface IIngestionActivities
{
    /// <summary>Resolves active provider mappings and creates the durable request record.</summary>
    [Activity("Ingestion.Prepare")]
    Task<ArtistImportPlan> Prepare(ArtistImportRequest request);

    /// <summary>Fetches at most one upstream page and stages its selected items.</summary>
    [Activity("Ingestion.Discover")]
    Task<ImportPage> Discover(ArtistImportRequest request, string provider, string? cursor);

    /// <summary>Fetches at most one item dependency; true means all dependencies are staged.</summary>
    [Activity("Ingestion.Fetch")]
    Task<bool> Fetch(ImportItemRef item, int part);

    /// <summary>Applies staged item data and its completion receipt in one transaction.</summary>
    [Activity("Ingestion.Apply")]
    Task<ImportStats> Apply(ImportItemRef item);

    /// <summary>Records an exhausted item failure without hiding other successful imports.</summary>
    [Activity("Ingestion.RecordFailure")]
    Task RecordFailure(ImportItemRef item, string reason);

    /// <summary>Returns bounded reconciliation work after discovery and item processing finish.</summary>
    [Activity("Ingestion.Candidates")]
    Task<string[]> Candidates(ArtistImportRequest request, string provider);

    /// <summary>Checks one candidate using direct upstream evidence and stages the result.</summary>
    [Activity("Ingestion.Verify")]
    Task Verify(ArtistImportRequest request, string provider, string key);

    /// <summary>Applies verified reconciliation and provider-specific finalization.</summary>
    [Activity("Ingestion.FinishProvider")]
    Task<ImportStats> FinishProvider(ArtistImportRequest request, string provider);

    /// <summary>Rebuilds catalog aggregates from committed items and marks the request complete or partial.</summary>
    [Activity("Ingestion.FinishArtist")]
    Task FinishArtist(ArtistImportRequest request, ImportResult result);
}
