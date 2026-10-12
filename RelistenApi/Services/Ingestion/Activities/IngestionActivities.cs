using System;
using System.Linq;
using System.Threading.Tasks;
using Relisten.Data;
using Relisten.Import;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace Relisten.Ingestion;

/// <summary>Bridges Temporal activities to provider operations; only database commits use retry receipts.</summary>
public sealed class IngestionActivities(
    ArtistService artists,
    ImporterService importers,
    IngestionStore store,
    IngestionHttpClientFactory httpClients
) : IIngestionActivities
{
    [Activity("Ingestion.PrepareArtist")]
    public async Task<ArtistImportPlan> PrepareArtist(ArtistImportRequest request)
    {
        using var heartbeat = new ActivityHeartbeat();
        IngestionStore.RequireActive(request.Run!);
        var artist =
            await artists.FindArtistWithIdOrSlug(request.Artist)
            ?? throw new ApplicationFailureException("Artist not found.", nonRetryable: true);
        var providers = artist
            .upstream_sources.Select(source => source.upstream_source.name)
            .Where(IngestionQueues.Providers.ContainsKey)
            .Where(provider => request.Provider == null || provider == request.Provider)
            .ToArray();

        if (request.Provider != null && providers.Length == 0)
        {
            throw new ApplicationFailureException(
                "Artist has no enabled mapping for the requested provider.",
                nonRetryable: true
            );
        }

        return new ArtistImportPlan(artist.uuid.ToString(), providers);
    }

    [Activity("Ingestion.PrepareProvider")]
    public async Task<ImportStats> PrepareProvider(ArtistImportRequest request, string provider)
    {
        using var heartbeat = new ActivityHeartbeat();
        var saved = await store.ReadReceiptAsync<ImportStats>(request.Run!, provider + "/prepare");
        if (saved != null)
        {
            return saved;
        }

        var (context, importer) = await Provider(request, provider, "prepare");
        return await importer.PrepareAsync(context);
    }

    [Activity("Ingestion.DiscoverItems")]
    public async Task<ImportPage> DiscoverItems(
        ArtistImportRequest request,
        string provider,
        DiscoveryCursor? cursor
    )
    {
        using var heartbeat = new ActivityHeartbeat();
        var position = cursor ?? new DiscoveryCursor();
        var name = DiscoveryName(provider, position.Upstream);
        var snapshot = await store.SnapshotAsync(
            request.Run!,
            name,
            async () =>
            {
                var (context, importer) = await Provider(request, provider, "discover");
                return await importer.DiscoverAsync(context, position.Upstream);
            }
        );
        var items = snapshot
            .Items.Skip(position.Offset)
            .Take(100)
            .Select((item, index) => new ImportItem(item.Identifier, name, position.Offset + index))
            .ToArray();
        var nextOffset = position.Offset + items.Length;
        DiscoveryCursor? next = null;

        if (nextOffset < snapshot.Items.Length)
        {
            next = position with { Offset = nextOffset };
        }
        else if (snapshot.NextCursor != null)
        {
            if (snapshot.NextCursor == position.Upstream)
            {
                throw new ApplicationFailureException(
                    "Provider discovery cursor did not advance.",
                    nonRetryable: true
                );
            }

            next = new DiscoveryCursor(snapshot.NextCursor);
        }

        return new ImportPage(items, next);
    }

    [Activity("Ingestion.ImportItem")]
    public async Task<ItemImportResult> ImportItem(
        ArtistImportRequest request,
        string provider,
        ImportItem? item
    )
    {
        using var heartbeat = new ActivityHeartbeat();
        var identifier =
            item?.Identifier
            ?? request.Item
            ?? throw new ArgumentException("An item identifier is required.");
        var operation = "item/" + identifier;
        var saved = await store.ReadReceiptAsync<ItemImportResult>(
            request.Run!,
            provider + "/" + operation
        );
        if (saved != null)
        {
            return saved;
        }

        var input =
            item == null
                ? new DiscoveredItem(identifier, "")
                : await store.ReadItemAsync(request.Run!, item);
        var (context, importer) = await Provider(request, provider, operation);
        return await importer.ImportItemAsync(context, input);
    }

    [Activity("Ingestion.ReconcileProvider")]
    public async Task<ProviderResult> ReconcileProvider(
        ArtistImportRequest request,
        string provider,
        string[] ineligibleItems,
        bool hasFailures
    )
    {
        using var heartbeat = new ActivityHeartbeat();
        var saved = await store.ReadReceiptAsync<ProviderResult>(
            request.Run!,
            provider + "/reconcile"
        );
        if (saved != null)
        {
            return saved;
        }

        var (context, importer) = await Provider(request, provider, "reconcile");
        return await importer.ReconcileAsync(context, ineligibleItems, hasFailures);
    }

    [Activity("Ingestion.FinishArtist")]
    public async Task<ImportResult> FinishArtist(ArtistImportRequest request, ImportResult result)
    {
        using var heartbeat = new ActivityHeartbeat();
        var artist =
            await artists.FindArtistWithIdOrSlug(request.Artist)
            ?? throw new ApplicationFailureException("Artist not found.", nonRetryable: true);

        IngestionStore.RequireActive(request.Run!);
        // Rebuild SQL owns its transactions and is safe to repeat. Wrapping it in
        // an item receipt transaction would let its COMMIT close that transaction.
        // Always rebuild: a cancelled earlier import may have committed items only.
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        await importers.Rebuild(artist, null);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    internal static string DiscoveryName(string provider, string? cursor) =>
        provider + "/discovery/" + (cursor ?? "first");

    private async Task<(ProviderContext Context, IProviderIngestion Importer)> Provider(
        ArtistImportRequest request,
        string provider,
        string operation
    )
    {
        IngestionStore.RequireActive(request.Run!);
        var artist =
            await artists.FindArtistWithIdOrSlug(request.Artist)
            ?? throw new ApplicationFailureException("Artist not found.", nonRetryable: true);
        var source = artist.upstream_sources.Single(source =>
            source.upstream_source.name == provider
        );
        var importer = source.upstream_source.importer!;
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        importer.UseIngestionHttpClient(httpClients.Create(cancellationToken));
        importer.CurrentImportOptions = new ImportOptions { OnlyYear = request.Year };
        var context = new ProviderContext(
            artist,
            source,
            request,
            store,
            operation,
            cancellationToken
        );
        return (context, (IProviderIngestion)importer);
    }
}
