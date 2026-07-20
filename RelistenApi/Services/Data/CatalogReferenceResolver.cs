using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;
using Relisten.Api.Models.Api;

namespace Relisten.Data
{
    public sealed class CatalogReferenceResolver : RelistenDataServiceBase
    {
        public CatalogReferenceResolver(DbService db) : base(db)
        {
        }

        public async Task<CatalogResolveResponse> Resolve(IReadOnlyList<CatalogReference> references)
        {
            if (references.Count == 0)
            {
                return EmptyResponse();
            }

            var catalogTypes = references.Select(reference => reference.CatalogType).ToArray();
            var catalogUuids = references.Select(reference => reference.CatalogUuid).ToArray();
            var queryPlan = CatalogReferenceQueryPlan.Create(references);

            return await db.WithConnection(async connection =>
            {
                using var results = await connection.QueryMultipleAsync(
                    queryPlan.Sql,
                    new {catalog_types = catalogTypes, catalog_uuids = catalogUuids});

                var artists = (await results.ReadAsync<ArtistWithCounts>()).ToArray();
                var features = (await results.ReadAsync<Features>())
                    .ToDictionary(feature => feature.artist_id);
                foreach (var artist in artists)
                {
                    artist.features = features[artist.id];
                    artist.upstream_sources = [];
                }

                var shows = await ReadIfIncluded<Show>(
                    results, queryPlan, CatalogReferenceResultSets.Shows);
                var sources = await ReadIfIncluded<SourceFull>(
                    results, queryPlan, CatalogReferenceResultSets.Sources);
                foreach (var source in sources)
                {
                    source.sets = [];
                    source.links = [];
                }

                var sourceTracks = await ReadIfIncluded<SourceTrack>(
                    results, queryPlan, CatalogReferenceResultSets.SourceTracks);
                var songs = await ReadIfIncluded<SetlistSongWithPlayCount>(
                    results, queryPlan, CatalogReferenceResultSets.Songs);
                var tours = await ReadIfIncluded<TourWithShowCount>(
                    results, queryPlan, CatalogReferenceResultSets.Tours);
                var venues = await ReadIfIncluded<VenueWithShowCount>(
                    results, queryPlan, CatalogReferenceResultSets.Venues);
                var years = await ReadIfIncluded<Year>(
                    results, queryPlan, CatalogReferenceResultSets.Years);
                var sourceSets = await ReadIfIncluded<SourceSet>(
                    results, queryPlan, CatalogReferenceResultSets.SourceSets);
                foreach (var sourceSet in sourceSets)
                {
                    sourceSet.tracks = [];
                }

                var entities = new CatalogResolveEntities
                {
                    artists = artists,
                    shows = shows,
                    sources = sources,
                    source_tracks = sourceTracks,
                    songs = songs,
                    tours = tours,
                    venues = venues,
                    years = years,
                    source_sets = sourceSets
                };

                return new CatalogResolveResponse
                {
                    contract_version = CatalogResolveRequestValidator.ContractVersion,
                    checked_at = DateTime.UtcNow,
                    references = BuildResolvedReferences(references, entities),
                    entities = entities
                };
            });
        }

        internal static IReadOnlyList<ResolvedCatalogReference> BuildResolvedReferences(
            IReadOnlyList<CatalogReference> references,
            CatalogResolveEntities entities)
        {
            var resolvedUuids = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal)
            {
                ["artist"] = EntityUuids(entities.artists),
                ["show"] = EntityUuids(entities.shows),
                ["source"] = EntityUuids(entities.sources),
                ["source_track"] = EntityUuids(entities.source_tracks),
                ["song"] = EntityUuids(entities.songs),
                ["tour"] = EntityUuids(entities.tours),
                ["venue"] = EntityUuids(entities.venues)
            };

            return references.Select(reference => new ResolvedCatalogReference
            {
                catalog_type = reference.CatalogType,
                catalog_uuid = reference.CatalogUuid,
                availability = resolvedUuids[reference.CatalogType].Contains(reference.CatalogUuid)
                    ? "available"
                    : "unavailable"
            }).ToArray();
        }

        private static HashSet<Guid> EntityUuids<T>(IEnumerable<T> entities)
            where T : IHasPersistentIdentifier =>
            entities.Select(entity => entity.uuid).ToHashSet();

        private static async Task<T[]> ReadIfIncluded<T>(
            SqlMapper.GridReader results,
            CatalogReferenceQueryPlan queryPlan,
            CatalogReferenceResultSets resultSet)
        {
            if (!queryPlan.Includes(resultSet))
            {
                return [];
            }

            return (await results.ReadAsync<T>()).ToArray();
        }

        private static CatalogResolveResponse EmptyResponse()
        {
            return new CatalogResolveResponse
            {
                contract_version = CatalogResolveRequestValidator.ContractVersion,
                checked_at = DateTime.UtcNow
            };
        }
    }
}
