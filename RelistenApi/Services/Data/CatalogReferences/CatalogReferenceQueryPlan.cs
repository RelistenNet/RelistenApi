using System;
using System.Collections.Generic;
using System.Linq;
using Relisten.Api.Models.Api;

namespace Relisten.Data
{
    [Flags]
    internal enum CatalogReferenceResultSets
    {
        Artists = 1 << 0,
        Shows = 1 << 1,
        Sources = 1 << 2,
        SourceTracks = 1 << 3,
        Songs = 1 << 4,
        Tours = 1 << 5,
        Venues = 1 << 6,
        Years = 1 << 7,
        SourceSets = 1 << 8
    }

    internal sealed record CatalogReferenceQueryPlan(
        string Sql,
        CatalogReferenceResultSets ResultSets)
    {
        public bool Includes(CatalogReferenceResultSets resultSet) =>
            ResultSets.HasFlag(resultSet);

        public static CatalogReferenceQueryPlan Create(IReadOnlyList<CatalogReference> references)
        {
            var requestedTypes = references
                .Select(reference => reference.CatalogType)
                .ToHashSet(StringComparer.Ordinal);

            // Every supported entity belongs to an artist. Child references also need the
            // shallow parents that the existing Realm repositories use to restore links.
            var resultSets = CatalogReferenceResultSets.Artists;
            foreach (var catalogType in requestedTypes)
            {
                resultSets |= catalogType switch
                {
                    "artist" => 0,
                    "show" => CatalogReferenceResultSets.Shows |
                              CatalogReferenceResultSets.Tours |
                              CatalogReferenceResultSets.Venues |
                              CatalogReferenceResultSets.Years,
                    "source" => CatalogReferenceResultSets.Shows |
                                CatalogReferenceResultSets.Sources |
                                CatalogReferenceResultSets.Tours |
                                CatalogReferenceResultSets.Venues |
                                CatalogReferenceResultSets.Years |
                                CatalogReferenceResultSets.SourceSets,
                    "source_track" => CatalogReferenceResultSets.Shows |
                                      CatalogReferenceResultSets.Sources |
                                      CatalogReferenceResultSets.SourceTracks |
                                      CatalogReferenceResultSets.Tours |
                                      CatalogReferenceResultSets.Venues |
                                      CatalogReferenceResultSets.Years |
                                      CatalogReferenceResultSets.SourceSets,
                    "song" => CatalogReferenceResultSets.Songs,
                    "tour" => CatalogReferenceResultSets.Tours,
                    "venue" => CatalogReferenceResultSets.Venues,
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(references),
                        catalogType,
                        "Unsupported catalog reference type.")
                };
            }

            // CatalogReferenceResolver reads these result sets in this order. Keep the append order
            // beside the dependency map so adding one entity cannot silently shift Dapper's mapping.
            var statements = new List<string>
            {
                CatalogReferenceArtistsSql.Query
            };

            AddIfIncluded(statements, resultSets, CatalogReferenceResultSets.Shows,
                CatalogReferenceShowsSql.Query);
            AddIfIncluded(statements, resultSets, CatalogReferenceResultSets.Sources,
                CatalogReferenceSourcesSql.Query);
            AddIfIncluded(statements, resultSets, CatalogReferenceResultSets.SourceTracks,
                CatalogReferenceSourceTracksSql.Query);
            AddIfIncluded(statements, resultSets, CatalogReferenceResultSets.Songs,
                CatalogReferenceSongsSql.Query);
            AddIfIncluded(statements, resultSets, CatalogReferenceResultSets.Tours,
                CatalogReferenceToursSql.Query);
            AddIfIncluded(statements, resultSets, CatalogReferenceResultSets.Venues,
                CatalogReferenceVenuesSql.Query);
            AddIfIncluded(statements, resultSets, CatalogReferenceResultSets.Years,
                CatalogReferenceYearsSql.Query);
            AddIfIncluded(statements, resultSets, CatalogReferenceResultSets.SourceSets,
                CatalogReferenceSourceSetsSql.Query);

            return new CatalogReferenceQueryPlan(string.Join("\n", statements), resultSets);
        }

        private static void AddIfIncluded(
            ICollection<string> statements,
            CatalogReferenceResultSets included,
            CatalogReferenceResultSets candidate,
            string sql)
        {
            if (included.HasFlag(candidate))
            {
                statements.Add(sql);
            }
        }
    }
}
