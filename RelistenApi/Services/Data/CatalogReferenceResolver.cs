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

            return await db.WithConnection(async connection =>
            {
                using var results = await connection.QueryMultipleAsync(
                    CatalogReferenceResolverSql.Resolve,
                    new {catalogTypes, catalogUuids});

                var resolvedReferences = (await results.ReadAsync<ResolvedCatalogReference>()).ToArray();
                var artists = (await results.ReadAsync<ArtistWithCounts>()).ToArray();
                var features = (await results.ReadAsync<Features>())
                    .ToDictionary(item => item.artist_id);
                foreach (var artist in artists)
                {
                    artist.features = features[artist.id];
                    artist.upstream_sources = [];
                }

                var shows = (await results.ReadAsync<Show>()).ToArray();
                var sources = (await results.ReadAsync<SourceFull>()).ToArray();
                foreach (var source in sources)
                {
                    source.sets = [];
                    source.links = [];
                }

                var sourceTracks = (await results.ReadAsync<SourceTrack>()).ToArray();
                var songs = (await results.ReadAsync<SetlistSongWithPlayCount>()).ToArray();
                var tours = (await results.ReadAsync<TourWithShowCount>()).ToArray();
                var venues = (await results.ReadAsync<VenueWithShowCount>()).ToArray();
                var years = (await results.ReadAsync<Year>()).ToArray();
                var sourceSets = (await results.ReadAsync<SourceSet>()).ToArray();
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
