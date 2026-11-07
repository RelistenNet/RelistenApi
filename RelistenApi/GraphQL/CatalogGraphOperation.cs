using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GreenDonut;
using HotChocolate;
using Microsoft.Extensions.DependencyInjection;
using Relisten.Api.Models.Api.V4;
using Relisten.Catalog;

namespace Relisten.GraphQL;

public sealed record CatalogEntityLoad(
    CatalogReferenceKind Kind,
    string Id,
    bool Recording = false
);

public sealed record CatalogEntityResult(
    object? Resource,
    CatalogReferenceAvailability Availability,
    string? Reason
);

public sealed record CatalogPageLoad(
    CatalogCollection Collection,
    CatalogPageRequest Page,
    bool Recordings = false,
    bool SongShows = false,
    bool Statistics = false
);

/// <summary>One operation's loader dependencies. Readers share the request's refresh collector;
/// each fetch owns a short snapshot and parallel fetches have bounded connection concurrency.</summary>
public sealed class CatalogGraphOperation(
    DbService db,
    CatalogReferenceLookup lookup,
    CatalogEntityReader entities,
    CatalogCollectionReader collections,
    CatalogSongMatchReader songMatches,
    CatalogGraphOptions options
)
{
    private static readonly ActivitySource Trace = new("Relisten.GraphQL");
    private static readonly Meter Meter = new("Relisten.GraphQL");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "graphql.catalog.read.duration",
        "ms"
    );
    private static readonly Histogram<int> Batches = Meter.CreateHistogram<int>(
        "graphql.catalog.batch.size"
    );

    private static readonly UpDownCounter<int> ActiveReads = Meter.CreateUpDownCounter<int>(
        "graphql.catalog.active_reads"
    );
    private static readonly Counter<long> Rows = Meter.CreateCounter<long>(
        "graphql.catalog.materialized_rows"
    );

    private readonly SemaphoreSlim connections = new(options.MaxConcurrentReads);

    private int materializedRows;

    internal void ChargeRows(int count)
    {
        if (count < 0)
        {
            throw new FormatException("The requested row bound is invalid.");
        }
        Rows.Add(count);
        if (Interlocked.Add(ref materializedRows, count) > options.MaxMaterializedRows)
        {
            throw new GraphQLException(
                ErrorBuilder
                    .New()
                    .SetCode("catalog_row_limit")
                    .SetMessage("The operation exceeds the materialized catalog row limit.")
                    .Build()
            );
        }
    }

    private SourceResource ProjectRecording(CatalogSource source)
    {
        ChargeRows(
            1
                + source.Sets.Count
                + source.Links.Count
                + source.Sets.SelectMany(s => s.Tracks).Sum(t => 1 + t.Normalization.Parts.Count)
        );
        return V4CatalogProjection.Source(source);
    }

    internal async Task<T> ExecuteReader<T>(Func<Task<T>> read, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        await connections.WaitAsync(token);
        using var activity = Trace.StartActivity("catalog.read");
        ActiveReads.Add(1);
        try
        {
            return await read();
        }
        finally
        {
            ActiveReads.Add(-1);
            connections.Release();
            Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    public Task<T> Read<T>(Func<CatalogReadSession, Task<T>> read, CancellationToken token) =>
        ExecuteReader(
            async () =>
            {
                await using var session = await CatalogReadSession.Open(db, token);
                return await read(session);
            },
            token
        );

    public Task<IReadOnlyDictionary<CatalogEntityLoad, CatalogEntityResult>> Entities(
        IReadOnlyList<CatalogEntityLoad> keys,
        CancellationToken token
    ) =>
        Read(
            async session =>
            {
                Batches.Record(keys.Count);
                var result = new Dictionary<CatalogEntityLoad, CatalogEntityResult>();
                foreach (var group in keys.Distinct().GroupBy(k => (k.Kind, k.Recording)))
                {
                    var references = group
                        .Select(k => new CatalogReference(
                            k.Kind,
                            CatalogReferenceScheme.V4,
                            k.Id,
                            null
                        ))
                        .ToArray();
                    var resolutions = await lookup.Read(session, references, token);
                    var targets = resolutions
                        .Select(r => r.Target)
                        .OfType<CatalogResolvedTarget>()
                        .Distinct()
                        .ToArray();
                    var resources = await Hydrate(session, targets, group.Key.Recording, token);
                    foreach (var resolution in resolutions)
                    {
                        var key = new CatalogEntityLoad(
                            resolution.Reference.Kind,
                            resolution.Reference.Id,
                            group.Key.Recording
                        );
                        result[key] = new(
                            resolution.Target is { } target
                                ? resources.GetValueOrDefault(target.Id)
                                : null,
                            resolution.Availability,
                            resolution.Reason
                        );
                    }
                }
                return (IReadOnlyDictionary<CatalogEntityLoad, CatalogEntityResult>)result;
            },
            token
        );

    public Task<IReadOnlyDictionary<CatalogPageLoad, CatalogGraphPage>> Pages(
        IReadOnlyList<CatalogPageLoad> keys,
        CancellationToken token
    ) =>
        Read(
            async session =>
            {
                Batches.Record(keys.Count);
                var result = new Dictionary<CatalogPageLoad, CatalogGraphPage>();
                foreach (
                    var group in keys.Distinct()
                        .GroupBy(k => (k.Collection.Kind, k.Recordings, k.SongShows, k.Statistics))
                )
                {
                    foreach (var page in await ReadPageGroup(session, group.ToArray(), token))
                    {
                        result[page.Key] = page.Value;
                    }
                }
                return (IReadOnlyDictionary<CatalogPageLoad, CatalogGraphPage>)result;
            },
            token
        );

    private async Task<IReadOnlyDictionary<CatalogPageLoad, CatalogGraphPage>> ReadPageGroup(
        CatalogReadSession session,
        CatalogPageLoad[] requests,
        CancellationToken token
    )
    {
        var pages = await collections.ReadPages(
            session,
            requests.Select(k => (k.Collection, k.Page)).ToArray(),
            token
        );
        var targets = pages.SelectMany(p => p.Targets).Distinct().ToArray();
        var resources = await Hydrate(session, targets, requests[0].Recordings, token);
        var statistics = requests[0].Statistics
            ? await songMatches.ReadShowStatisticsBatch(
                session,
                pages
                    .Select(
                        (page, index) =>
                            new CatalogSongStatisticsRequest(
                                page.Targets.Count == 0
                                    ? Guid.Empty
                                    : ((ShowSummaryResource)resources[page.Targets[0].Id])
                                        .ArtistId
                                        .Value,
                                requests[index].Collection.Owner!,
                                page.Targets.Select(t => t.Uuid!.Value).ToArray()
                            )
                    )
                    .ToArray(),
                token
            )
            : null;

        return requests
            .Select(
                (request, index) =>
                    (
                        request,
                        page: ProjectPage(request, pages[index], resources, statistics?[index])
                    )
            )
            .ToDictionary(item => item.request, item => item.page);
    }

    private static CatalogGraphPage ProjectPage(
        CatalogPageLoad request,
        CatalogPageSelection page,
        IReadOnlyDictionary<string, object> resources,
        IReadOnlyDictionary<Guid, CatalogSongShowStatistics>? statistics
    )
    {
        var scope = CatalogCursor.Scope(request.Collection);
        var nodes = new object[page.Targets.Count];
        var edges = new CatalogGraphEdge[nodes.Length];
        for (var i = 0; i < nodes.Length; i++)
        {
            var target = page.Targets[i];
            nodes[i] = ProjectPageNode(request, target, resources[target.Id], statistics);
            edges[i] = new(nodes[i], scope, page.Keys[i]);
        }
        return new(
            nodes,
            edges,
            new(edges.Length == 0 ? null : edges[^1].Cursor, page.HasNextPage),
            page.TotalCount
        );
    }

    private static object ProjectPageNode(
        CatalogPageLoad request,
        CatalogResolvedTarget target,
        object value,
        IReadOnlyDictionary<Guid, CatalogSongShowStatistics>? statistics
    )
    {
        if (request.SongShows)
        {
            var stats = statistics?.GetValueOrDefault(target.Uuid!.Value) ?? new();
            return new CatalogGraphSongShow(
                (ShowSummaryResource)value,
                new(
                    stats.SingleSong is { } single
                        ? V4CatalogProjection.SourceStatistics(single)
                        : null,
                    stats.MultiSong is { } multi
                        ? V4CatalogProjection.SourceStatistics(multi)
                        : null
                ),
                request.Collection.Owner!
            );
        }
        if (!request.Collection.IncludeFallback)
        {
            return value;
        }

        return value switch
        {
            SongDetailResource song => new ArtistSongEntryResource(SongEntryKind.Song, song, null),
            ImportedSongResource imported => new ArtistSongEntryResource(
                SongEntryKind.ImportedSong,
                null,
                imported
            ),
            ArtistVenueResource venue => new ArtistVenueEntryResource(
                VenueEntryKind.ArtistVenue,
                venue,
                null
            ),
            ImportedVenueResource raw => new ArtistVenueEntryResource(
                VenueEntryKind.ImportedVenue,
                null,
                raw
            ),
            _ => throw new InvalidOperationException("Unexpected browse entry kind."),
        };
    }

    public Task<IReadOnlyList<CatalogGraphResolution>> Resolve(
        IReadOnlyList<CatalogReference> references,
        bool recordings,
        CancellationToken token
    ) =>
        Read(
            async session =>
            {
                var resolutions = await lookup.Read(session, references, token);
                var resources = new Dictionary<string, object>();
                foreach (
                    var group in resolutions
                        .Select(r => r.Target)
                        .OfType<CatalogResolvedTarget>()
                        .Distinct()
                        .GroupBy(t => t.Kind)
                )
                {
                    foreach (var pair in await Hydrate(session, group.ToArray(), recordings, token))
                    {
                        resources[pair.Key] = pair.Value;
                    }
                }
                return (IReadOnlyList<CatalogGraphResolution>)
                    resolutions
                        .Select(r => new CatalogGraphResolution(
                            r.Reference.Kind,
                            r.Reference.Scheme,
                            r.Reference.Id,
                            r.Availability,
                            r.Reason,
                            r.Target is { } target ? resources.GetValueOrDefault(target.Id) : null
                        ))
                        .ToArray();
            },
            token
        );

    private async Task<Dictionary<string, object>> Hydrate(
        CatalogReadSession session,
        CatalogResolvedTarget[] targets,
        bool recordings,
        CancellationToken token
    )
    {
        var kinds = targets.GroupBy(t => t.Kind).ToArray();
        if (kinds.Length > 1)
        {
            var combined = new Dictionary<string, object>();
            foreach (var kind in kinds)
            {
                foreach (var pair in await Hydrate(session, kind.ToArray(), recordings, token))
                {
                    combined[pair.Key] = pair.Value;
                }
            }
            return combined;
        }
        if (targets.Length == 0)
        {
            return new();
        }
        if (recordings && targets[0].Kind == CatalogReferenceKind.Source)
        {
            return (
                await entities.ReadRecordings(
                    session,
                    targets.Select(t => t.Uuid!.Value).ToArray(),
                    token
                )
            )
                .Select(ProjectRecording)
                .ToDictionary(s => s.Id.ToString(), s => (object)s);
        }
        var facts = await entities.Read(session, targets, token);
        ChargeRows(targets.Length + facts.Tracks.Sum(t => t.Normalization.Parts.Count));
        var patches = V4CatalogProjection.ResolvedEntities(facts);
        var result = new Dictionary<string, object>();
        foreach (var value in patches.Artists)
        {
            result[value.Id.ToString()] = value;
        }
        foreach (var value in patches.Shows)
        {
            result[value.Id.ToString()] = value;
        }
        foreach (var value in patches.Sources)
        {
            result[value.Id.ToString()] = value;
        }
        foreach (var value in patches.SourceSets)
        {
            result[value.Id.ToString()] = value;
        }
        foreach (var value in patches.Tracks)
        {
            result[value.Id.ToString()] = value;
        }
        foreach (var value in patches.Years)
        {
            result[value.Id.ToString()] = value;
        }
        foreach (var value in patches.Tours)
        {
            result[value.Id.ToString()] = value;
        }
        foreach (var value in patches.ImportedVenues)
        {
            result[value.Id.ToString()] = value;
        }
        foreach (var value in patches.ImportedSongs)
        {
            result[value.Id.ToString()] = value;
        }
        foreach (var value in patches.Venues)
        {
            result[value.Id.ToString()] = value;
        }
        foreach (var value in patches.Songs)
        {
            result[value.Id.ToString()] = value;
        }
        foreach (var value in patches.ArtistVenues)
        {
            result[value.Id.ToString()] = value;
        }
        foreach (var value in patches.SourceReviews)
        {
            result[value.Id.ToString()] = value;
        }
        foreach (var value in patches.SetlistShows)
        {
            result[value.Id.ToString()] = value;
        }
        return result;
    }
}

public sealed class CatalogEntityLoader(IBatchScheduler scheduler, CatalogGraphOperation operation)
    : BatchDataLoader<CatalogEntityLoad, CatalogEntityResult>(
        scheduler,
        new DataLoaderOptions { MaxBatchSize = 500 }
    )
{
    protected override Task<
        IReadOnlyDictionary<CatalogEntityLoad, CatalogEntityResult>
    > LoadBatchAsync(IReadOnlyList<CatalogEntityLoad> keys, CancellationToken cancellationToken) =>
        operation.Entities(keys, cancellationToken);
}

public sealed class CatalogPageLoader(IBatchScheduler scheduler, CatalogGraphOperation operation)
    : BatchDataLoader<CatalogPageLoad, CatalogGraphPage>(
        scheduler,
        new DataLoaderOptions { MaxBatchSize = 100 }
    )
{
    protected override Task<IReadOnlyDictionary<CatalogPageLoad, CatalogGraphPage>> LoadBatchAsync(
        IReadOnlyList<CatalogPageLoad> keys,
        CancellationToken cancellationToken
    ) => operation.Pages(keys, cancellationToken);
}
