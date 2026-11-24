using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Relisten.Search;

using Document = Dictionary<string, object>;

internal sealed class ArtistSearchIndexer(
    DbService db,
    TypesenseClient typesense,
    SearchIndexBuilder builder,
    IConfiguration configuration,
    ILogger<ArtistSearchIndexer> logger
)
{
    internal string AliasPrefix { get; } = configuration["Search:AliasPrefix"] ?? "catalog";

    public async Task Reindex(Guid artistUuid, CancellationToken cancellationToken)
    {
        // The entities alias publishes a complete generation. Sibling aliases
        // may already have moved if an earlier publish was interrupted.
        var entities = await typesense.ReadAlias(
            SearchIndexBuilder.AliasName(AliasPrefix, SearchIndexSchema.Entities),
            cancellationToken
        );
        if (entities is null)
        {
            return;
        }

        var generation = new SearchCollections(AliasPrefix, entities);
        var targets = SearchIndexSchema.Collections.ToDictionary(kind => kind, generation.Name);

        await using var connection = db.CreateConnection(longTimeout: true, readOnly: false);
        await connection.OpenAsync(cancellationToken);
        await using var snapshot = await SearchProjectionRows.BeginSnapshot(
            connection,
            cancellationToken
        );
        var artist = await connection.QuerySingleOrDefaultAsync<ArtistIdentity>(
            new CommandDefinition(
                "SELECT id, slug FROM artists WHERE uuid = @artistUuid",
                new { artistUuid },
                snapshot,
                cancellationToken: cancellationToken
            )
        );
        if (artist == null)
        {
            return;
        }

        var plays = await SearchPlayTotals.Load(
            connection,
            snapshot,
            DateOnly.FromDateTime(DateTime.UtcNow),
            exportPath: null,
            cancellationToken,
            artistUuid
        );
        var projection = await CatalogSearchProjection.Open(
            new SearchProjectionRows(connection, snapshot, artist.Id),
            plays,
            cancellationToken
        );
        await Apply(targets, artist.Slug, projection, cancellationToken);
        logger.LogInformation("Reindexed catalog search for {Artist}", artist.Slug);
    }

    private async Task Apply(
        Dictionary<string, string> targets,
        string slug,
        CatalogSearchProjection projection,
        CancellationToken cancellationToken
    )
    {
        var owned = $"artist_slug:=`{slug}`";
        var entityCollection = targets[SearchIndexSchema.Entities];
        var entities = await typesense
            .ExportDocuments(
                entityCollection,
                $"{owned} || artist_slugs:=`{slug}`",
                cancellationToken
            )
            .ToArrayAsync(cancellationToken);
        await UpdateSharedVenues(entityCollection, entities, projection, cancellationToken);

        foreach (
            var (kind, documents) in projection
                .Documents(cancellationToken, calibrate: false)
                .InBuildOrder()
        )
        {
            var collection = targets[kind];
            var existing =
                kind == SearchIndexSchema.Entities
                    ? entities.Where(document => !SharedVenue(document)).ToArray()
                    : await typesense
                        .ExportDocuments(collection, owned, cancellationToken)
                        .ToArrayAsync(cancellationToken);
            var oldDocuments = existing.ToDictionary(
                document => document.Id,
                StringComparer.Ordinal
            );
            await builder.Upsert(
                collection,
                TrackRemainingIds(documents, oldDocuments, cancellationToken),
                cancellationToken
            );
            await typesense.DeleteDocuments(collection, oldDocuments.Keys, cancellationToken);
        }
    }

    private async Task UpdateSharedVenues(
        string collection,
        TypesenseClient.IndexedDocument[] existing,
        CatalogSearchProjection projection,
        CancellationToken cancellationToken
    )
    {
        // Read both prior memberships and prior shows so a remap refreshes the
        // venue the artist left as well as the one it moved to.
        var oldVenues = existing
            .Select(document => document.VenueId)
            .OfType<string>()
            .Where(id => id.StartsWith("venue_", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var affected = oldVenues.Union(projection.SharedVenues).ToHashSet(StringComparer.Ordinal);
        var venues = await projection.SharedVenueDocuments(affected, cancellationToken);
        var percentiles = existing
            .Where(SharedVenue)
            .ToDictionary(
                document => document.Id,
                document => document.PopularityPercentile,
                StringComparer.Ordinal
            );
        foreach (var venue in venues)
        {
            venue["popularity_percentile"] = percentiles.GetValueOrDefault((string)venue["id"]);
        }
        await builder.Upsert(collection, venues.ToAsyncEnumerable(), cancellationToken);
        oldVenues.ExceptWith(venues.Select(venue => (string)venue["id"]));
        await typesense.DeleteDocuments(collection, oldVenues, cancellationToken);
    }

    private static bool SharedVenue(TypesenseClient.IndexedDocument document) =>
        document.Kind == "venue" && document.Id.StartsWith("venue_", StringComparison.Ordinal);

    private static async IAsyncEnumerable<Document> TrackRemainingIds(
        IAsyncEnumerable<Document> documents,
        Dictionary<string, TypesenseClient.IndexedDocument> oldDocuments,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        await foreach (var document in documents.WithCancellation(cancellationToken))
        {
            if (oldDocuments.Remove((string)document["id"], out var previous))
            {
                document["popularity_percentile"] = previous.PopularityPercentile;
            }
            yield return document;
        }
    }

    private sealed record ArtistIdentity(int Id, string Slug);
}
