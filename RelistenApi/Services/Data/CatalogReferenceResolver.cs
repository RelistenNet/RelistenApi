using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
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
            var ordinals = Enumerable.Range(0, references.Count).ToArray();

            return await db.WithConnection(async connection =>
            {
                using var results = await connection.QueryMultipleAsync(
                    CatalogReferenceResolverSql.Resolve,
                    new {catalogTypes, catalogUuids, ordinals});

                var resolvedReferences = (await results.ReadAsync<ResolvedCatalogReference>()).ToArray();
                var artists = (await results.ReadAsync<ResolvedArtist>()).ToArray();
                var shows = (await results.ReadAsync<ResolvedShow>()).ToArray();
                var sources = (await results.ReadAsync<ResolvedSource>()).ToArray();
                var sourceTracks = (await results.ReadAsync<ResolvedSourceTrack>()).ToArray();
                var songs = (await results.ReadAsync<ResolvedSong>()).ToArray();
                var tours = (await results.ReadAsync<ResolvedTour>()).ToArray();
                var venues = (await results.ReadAsync<ResolvedVenue>()).ToArray();

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
                        venues = venues
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
