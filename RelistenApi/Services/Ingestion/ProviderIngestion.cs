using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Relisten.Api.Models;
using Relisten.Import;

namespace Relisten.Ingestion;

/// <summary>Staged provider input. Body is stored in PostgreSQL and is never a workflow argument.</summary>
public record ProviderItem(string Key, string Body);
/// <summary>One fetched or locally selected page of item inputs.</summary>
public record ProviderPage(ProviderItem[] Items, string? NextCursor = null, string[]? ObservedKeys = null);
/// <summary>One item's fetched dependency; Done ends its fetch sequence.</summary>
public record ProviderFetch(string Body, bool Done = true, DateTime? ModifiedAt = null);
/// <summary>Activity-local context; mutable importer instances are scoped to one call.</summary>
public record ProviderContext(Artist Artist, ArtistUpstreamSource Source, ArtistImportRequest Request,
    ImportRunRepository Store, CancellationToken CancellationToken);

/// <summary>Provider-specific parsing and catalog writes reused by Temporal activity composition.</summary>
public interface IProviderIngestion
{
    Task<ProviderPage> DiscoverAsync(ProviderContext context, string? cursor);
    Task<ProviderFetch> FetchAsync(ProviderContext context, ProviderItem item, int part);
    Task<ImportStats> ApplyAsync(ProviderContext context, ProviderItem item, ProviderFetch[] fetched);
}
