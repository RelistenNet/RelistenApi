using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Newtonsoft.Json;
using Relisten.Api.Models;
using Relisten.Api.Models.Normalization;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.Jerry;
using Relisten.Vendor;

namespace Relisten.Import;

// The ordinary refresh and venue repair share page parsing, but select opposite
// catalog membership sets and have different cleanup policies.
public partial class JerryGarciaComImporter
{
    /// <summary>Freezes the mirror index for either missing-page import or existing-page repair.</summary>
    public async Task<JerrySelection> SelectPagesAsync(ProviderContext context, bool existing)
    {
        using var response = await http.GetAsync(
            ShowPagesListingUrl(context.Source),
            context.CancellationToken
        );
        response.EnsureSuccessStatusCode();
        var files =
            JsonConvert.DeserializeObject<string[]>(
                await response.Content.ReadAsStringAsync(context.CancellationToken)
            )
            ?? throw new System.IO.InvalidDataException(
                "Jerry mirror returned an invalid page index."
            );

        await PreloadData(context.Artist);

        return new JerrySelection(
            BuildFileMetaObjects(files)
                .Where(f => existingSetlistShows.ContainsKey(f.Identifier) == existing)
                .Select(f => new DiscoveredItem<JerryShowId, FileMetaObject>(new(f.Identifier), f))
                .ToArray()
        );
    }

    /// <summary>Imports one missing page and extends the shared tour date range.</summary>
    public Task<JerryItemResult> ImportPageAsync(
        ProviderContext context,
        DiscoveredItem<JerryShowId, FileMetaObject> item
    ) => ApplySelectedPageAsync(context, item, repairVenue: false);

    /// <summary>Repairs one existing setlist's venue without changing its songs or identity.</summary>
    public Task<JerryItemResult> RepairVenueAsync(
        ProviderContext context,
        DiscoveredItem<JerryShowId, FileMetaObject> item
    ) => ApplySelectedPageAsync(context, item, repairVenue: true);

    /// <summary>Refreshes tour bounds after repairs; orphan removal requires every selected repair to succeed.</summary>
    public Task<int> FinishVenueRepairAsync(ProviderContext context, bool removeOrphans) =>
        context.CommitAsync(async () =>
        {
            await _tourService.RefreshDatesFromSetlists(context.Artist);
            return removeOrphans ? await _venueService.RemoveOrphansForArtist(context.Artist) : 0;
        });

    private async Task<JerryItemResult> ApplySelectedPageAsync(
        ProviderContext context,
        DiscoveredItem<JerryShowId, FileMetaObject> item,
        bool repairVenue
    )
    {
        var page = await FetchShowPageAsync(context.Source, item.Input, context.CancellationToken);
        if (page == null)
        {
            return new(item.Id, null, ItemDisposition.Skipped, JerrySkipReason.MissingShowDetails);
        }

        return await context.CommitAsync(async () =>
        {
            // Fresh importer dictionaries belong to this activity attempt. Reload
            // in the transaction before testing catalog membership.
            await PreloadData(context.Artist);

            return repairVenue
                ? await ApplyVenueRepairAsync(context, item, page.Html, page.ModifiedAt)
                : await ApplyMissingPageAsync(context, item, page.Html, page.ModifiedAt);
        });
    }

    private async Task<JerryItemResult> ApplyVenueRepairAsync(
        ProviderContext context,
        DiscoveredItem<JerryShowId, FileMetaObject> item,
        string html,
        DateTime modifiedAt
    )
    {
        var previous = existingSetlistShows.GetValue(item.Id.Value);
        if (previous == null)
        {
            return new(
                item.Id,
                null,
                ItemDisposition.Skipped,
                JerrySkipReason.MissingCatalogSetlist
            );
        }

        var stats = new ImportStats();
        await ApplyVenueBackfill(stats, context.Artist, item.Input, html, modifiedAt);

        return new(
            item.Id,
            new(previous.id),
            stats.Created + stats.Updated == 0 ? ItemDisposition.Unchanged : ItemDisposition.Applied
        );
    }

    private async Task<JerryItemResult> ApplyMissingPageAsync(
        ProviderContext context,
        DiscoveredItem<JerryShowId, FileMetaObject> item,
        string html,
        DateTime modifiedAt
    )
    {
        var previous = existingSetlistShows.GetValue(item.Id.Value);
        if (previous != null)
        {
            return new(item.Id, new(previous.id), ItemDisposition.Unchanged);
        }

        await ProcessPage(new ImportStats(), context.Artist, item.Input, html, modifiedAt, null);

        if (context.Artist.features.tours && tourToStartDate.Count > 0)
        {
            await UpdateTourStartEndDates(context.Artist);
        }

        return new(item.Id, new(existingSetlistShows[item.Id.Value]!.id), ItemDisposition.Applied);
    }

    // Native refresh, Temporal import and venue repair consume the same retained response.
    // The mirror prefix distinguishes provider catalogs; the show ID stays stable if a file moves.
    private async Task<FetchedShowPage?> FetchShowPageAsync(
        ArtistUpstreamSource source,
        FileMetaObject item,
        CancellationToken ct = default
    )
    {
        var resource = new NormalizationResourceKey(
            "jerrygarcia.com",
            "mirror_show_page",
            $"{source.upstream_identifier}/{item.Identifier}"
        );

        try
        {
            return await normalizationInputs.FetchAsync(
                resource,
                "jerry-mirror-html:1",
                async token =>
                {
                    using var response = await http.GetAsync(
                        ShowPageUrl(source, item.FilePath),
                        token
                    );
                    response.EnsureSuccessStatusCode();
                    var bytes = await response.Content.ReadAsByteArrayAsync(token);
                    // HttpContent buffers its original bytes and applies the provider's declared charset.
                    var html = await response.Content.ReadAsStringAsync(token);
                    ValidateShowDetails(html);

                    var modified = response.Content.Headers.LastModified;
                    var etag = response.Headers.ETag?.ToString();
                    var revision = string.Join(
                        ";",
                        new[]
                        {
                            etag == null ? null : $"etag:{etag}",
                            modified == null ? null : $"last-modified:{modified.Value:R}",
                        }.Where(value => value != null)
                    );

                    return new NormalizationProviderResponse<FetchedShowPage>(
                        new(html, modified?.UtcDateTime ?? DateTime.UtcNow),
                        bytes,
                        response.Content.Headers.ContentType?.ToString() ?? "text/html",
                        revision.Length == 0 ? null : revision
                    );
                },
                ct
            );
        }
        catch (MissingShowDetailsException)
        {
            // A mirror placeholder is a supported skip, not a current show input.
            return null;
        }
    }

    private static void ValidateShowDetails(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var root = document.DocumentNode;
        var names = root.DescendantsWithClass("venue-name").ToArray();
        if (names.Length == 0)
        {
            throw new MissingShowDetailsException();
        }

        // Validate the existing parser's required fields before advancing the evidence generation.
        // Song nodes remain untouched, including links, set order, repeats and segue markers.
        if (
            string.IsNullOrWhiteSpace(names[0].InnerText)
            || root.DescendantsWithClass("venue-address").Count() != 1
            || root.DescendantsWithClass("venue-country").Count() != 1
        )
        {
            throw new InvalidDataException("Jerry mirror returned incomplete show details.");
        }
    }

    private sealed record FetchedShowPage(string Html, DateTime ModifiedAt);

    private sealed class MissingShowDetailsException : Exception { }
}
