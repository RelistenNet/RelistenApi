using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Relisten.Api.Models;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Import;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.PhishIn;
using Relisten.Vendor.Phishin;

namespace RelistenApiTests.Ingestion;

[TestFixture]
public class TestPhishInPagination
{
    [TestCase(null)]
    [TestCase(2026)]
    public async Task ShowPagesRespectChronologicalPaginationAndTheLowerYearBound(int? fromYear)
    {
        using var upstream = new PhishInListing();
        using var importer = CreateImporter();
        importer.UseIngestionHttpClient(new HttpClient(upstream));
        var context = new ProviderContext(
            new Artist(),
            new ArtistUpstreamSource(),
            new IngestionExecution("pagination-test", DateTimeOffset.UtcNow.AddDays(1)),
            null!,
            CancellationToken.None
        );
        var dates = new List<string>();
        PhishInPage? cursor = null;

        do
        {
            var page = await importer.ReadShowPageAsync(context, fromYear, cursor);
            dates.AddRange(page.Items.Select(item => item.Input.Date));
            cursor = page.Next;
        } while (cursor != null);

        var expected = upstream
            .Shows.Where(show => !fromYear.HasValue || show.date.StartsWith("2026-"))
            .Select(show => show.date);
        dates.Should().BeEquivalentTo(expected);
        dates.Should().OnlyHaveUniqueItems();
        upstream.Pages.Should().Equal(fromYear.HasValue ? [1, 3, 2, 1] : [1, 2, 3]);

        if (!fromYear.HasValue)
        {
            dates.Should().BeInAscendingOrder();
        }
    }

    private static PhishinImporter CreateImporter() =>
        // Listing needs HTTP and configuration only; catalog services are used by
        // preparation and writes, which this pagination contract does not invoke.
        new(
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            NullLogger<PhishinImporter>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection().Build(),
            null!,
            new NormalizationPhishInInputService(
                null!,
                new NormalizationProviderInputService(
                    null!,
                    Options.Create(new NormalizationProviderCaptureOptions())
                )
            )
        );

    private sealed class PhishInListing : HttpMessageHandler
    {
        // Without both sorting parameters, the current upstream leaves database
        // order intact. Its last page can contain only old shows despite newer
        // shows elsewhere, which previously made a filtered refresh stop early.
        public PhishinShow[] Shows { get; } =
            Enumerable
                .Range(1, 21)
                .Select(day => Show($"2026-01-{day:D2}"))
                .Concat(Enumerable.Range(1, 22).Select(day => Show($"2000-01-{day:D2}")))
                .ToArray();

        public List<int> Pages { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var query = QueryHelpers.ParseQuery(request.RequestUri!.Query);
            var page = int.Parse(query["page"]!);
            var pageSize = int.Parse(query["per_page"]!);
            Pages.Add(page);

            IEnumerable<PhishinShow> ordered = Shows;
            if (query["sort_attr"] == "date" && query["sort_dir"] == "asc")
            {
                ordered = ordered.OrderBy(show => show.date, StringComparer.Ordinal);
            }

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonConvert.SerializeObject(
                            new
                            {
                                success = true,
                                total_pages = (int)Math.Ceiling((double)Shows.Length / pageSize),
                                total_entries = Shows.Length,
                                data = ordered.Skip((page - 1) * pageSize).Take(pageSize),
                            }
                        )
                    ),
                }
            );
        }

        private static PhishinShow Show(string date) =>
            new()
            {
                date = date,
                venue = new PhishinSmallVenue(),
                tags = [],
                tracks = [],
            };
    }
}
