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

                var resolvedReferences = (await results.ReadAsync<ResolvedCatalogReference>()).ToArray();
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

                return new CatalogResolveResponse
                {
                    contract_version = CatalogResolveRequestValidator.ContractVersion,
                    checked_at = DateTime.UtcNow,
                    references = resolvedReferences,
                    entities = new CatalogResolveEntities
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
                    }
                };
            });
        }

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
