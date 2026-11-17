using System;
using System.Threading.Tasks;
using Hangfire.Server;
using Relisten.Api.Models;

namespace Relisten;

/// <summary>
/// Retired Hangfire ingestion entrypoints. Preserve their signatures so saved
/// jobs can still deserialize, but never restart a second catalog writer when
/// an old job is retried. New ingestion is admitted only through Temporal.
/// </summary>
public class ScheduledService
{
    public Task IndexArchiveOrgArtists(PerformContext? context, bool allowedInDev = false) =>
        Task.CompletedTask;

    public Task RefreshAllArtists(PerformContext? context, bool allowedInDev = false) =>
        Task.CompletedTask;

    public Task RebuildAllArtists(PerformContext? context) => Task.CompletedTask;

    public Task RefreshArtist(string idOrSlug, bool deleteOldContent, PerformContext? ctx) =>
        Task.CompletedTask;

    public Task RefreshPhishFromPhishinOnly(PerformContext? ctx) => Task.CompletedTask;

    public Task RefreshArtist(
        string idOrSlug,
        string? specificShowId,
        bool deleteOldContent,
        PerformContext? ctx
    ) => Task.CompletedTask;

    public Task RefreshArtist(
        string idOrSlug,
        string? specificShowId,
        bool deleteOldContent,
        Func<ArtistUpstreamSource, bool>? filterUpstreamSources,
        PerformContext? ctx
    ) => Task.CompletedTask;

    public Task BackfillJerryGarciaVenues(string artistSlug, PerformContext? ctx) =>
        Task.CompletedTask;

    public Task RefreshActiveArtists(PerformContext? context, bool allowedInDev = false) =>
        Task.CompletedTask;

    public Task RefreshArtistThinScrape(string idOrSlug, PerformContext? ctx) => Task.CompletedTask;
}
