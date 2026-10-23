using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace Relisten.Normalization.Venues;

/// One legacy venue row (public.venues: one per artist and upstream), with how much the
/// catalogue uses it. Key and Locality are its normalised name and place; ExternalId a
/// provider id its upstream gave it ("setlistfm", "4bd6ca4a").
public sealed record LegacyVenue(
    Guid Uuid,
    Guid ArtistUuid,
    string Name,
    string? Location,
    int Uses,
    int? FirstYear,
    int? LastYear
)
{
    public string? Key { get; } = VenueNormalizer.Key(Name);
    public Locality? Locality { get; init; } = VenueNormalizer.ParseLocation(Location);
    public (string Provider, string Key)? ExternalId { get; init; }
}

public sealed record CatalogueVenue(
    Guid Uuid,
    string Name,
    string Kind,
    Guid? ParentUuid,
    string? City,
    string? Region,
    string? Country
)
{
    public string? LocalityKey => City == null ? null : new Locality(City, Region, Country).Key;
}

/// The venue catalogue as it stands, for the rows in scope: legacy rows not yet mapped, the
/// venues with their names and external ids, and the mapping so far. Merged venues resolve to
/// their survivors, so a merged venue's names and ids place rows on the survivor.
public sealed partial class VenueState
{
    public required IReadOnlyList<LegacyVenue> Unmapped { get; init; }
    public required IReadOnlyDictionary<Guid, CatalogueVenue> Venues { get; init; }

    /// Normalised name → venues that carry it (resolved to survivors).
    public required ILookup<string, Guid> Names { get; init; }

    /// (provider, key) → venue (resolved to its survivor).
    public required IReadOnlyDictionary<(string, string), Guid> ExternalIds { get; init; }

    /// Venues by locality key.
    public ILookup<string, CatalogueVenue> ByLocality =>
        byLocality ??= Venues
            .Values.Where(v => v.LocalityKey != null)
            .ToLookup(v => v.LocalityKey!);

    private ILookup<string, CatalogueVenue>? byLocality;

    /// artists: only these artists' legacy rows (null: every artist). Venues are cross-artist,
    /// so the catalogue side is always whole.
    public static async Task<VenueState> LoadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? tx,
        IReadOnlyCollection<Guid>? artists = null
    )
    {
        var rows = await connection.QueryAsync<(
            Guid Uuid,
            Guid Artist,
            string Name,
            string? Location,
            string? Upstream,
            int Uses,
            int? First,
            int? Last
        )>(
            """
            WITH uses AS (
                SELECT venue_id, date FROM shows WHERE venue_id IS NOT NULL
                UNION ALL
                SELECT s.venue_id, sh.date FROM sources s LEFT JOIN shows sh ON sh.id = s.show_id WHERE s.venue_id IS NOT NULL
            ), counted AS (
                SELECT venue_id, count(*)::int AS uses,
                       extract(year FROM min(date))::int AS first, extract(year FROM max(date))::int AS last
                FROM uses GROUP BY venue_id
            )
            SELECT v.uuid, a.uuid, v.name, v.location, v.upstream_identifier,
                   coalesce(c.uses, 0), c.first, c.last
            FROM venues v JOIN artists a ON a.id = v.artist_id
            LEFT JOIN counted c ON c.venue_id = v.id
            WHERE (@all OR a.uuid = ANY(@artists))
              AND NOT EXISTS (SELECT 1 FROM norm.venue_evidence e WHERE e.legacy_venue_uuid = v.uuid)
              AND v.name IS NOT NULL
            """,
            new { all = artists == null, artists = artists?.ToArray() ?? [] },
            tx
        );

        var venues = (
            await connection.QueryAsync<(
                Guid Uuid,
                string Name,
                string Kind,
                Guid? Parent,
                string? City,
                string? Region,
                string? Country,
                Guid? MergedInto
            )>(
                "SELECT uuid, name, kind, parent_uuid, city, region, country, merged_into FROM norm.venues",
                transaction: tx
            )
        ).ToList();
        var mergedInto = venues
            .Where(v => v.MergedInto != null)
            .ToDictionary(v => v.Uuid, v => v.MergedInto!.Value);
        Guid Survivor(Guid uuid)
        {
            for (var depth = 0; depth < 20 && mergedInto.TryGetValue(uuid, out var next); depth++)
            {
                uuid = next;
            }

            return uuid;
        }

        var names = await connection.QueryAsync<(string Normalized, Guid Venue)>(
            "SELECT normalized, venue_uuid FROM norm.venue_names",
            transaction: tx
        );
        var ids = await connection.QueryAsync<(string Provider, string Key, Guid Venue)>(
            "SELECT provider, key, venue_uuid FROM norm.venue_external_ids",
            transaction: tx
        );

        var legacy = rows.Select(r => new LegacyVenue(
                r.Uuid,
                r.Artist,
                r.Name,
                r.Location,
                r.Uses,
                r.First,
                r.Last
            )
            {
                ExternalId = ParseExternalId(r.Upstream),
            })
            .ToList();
        var catalogue = venues
            .Where(v => v.MergedInto == null && v.City != null)
            .Select(v => new Locality(v.City!, v.Region, v.Country));

        return new VenueState
        {
            Unmapped = FillRegions(legacy, catalogue),
            Venues = venues
                .Where(v => v.MergedInto == null)
                .ToDictionary(
                    v => v.Uuid,
                    v => new CatalogueVenue(
                        v.Uuid,
                        v.Name,
                        v.Kind,
                        v.Parent,
                        v.City,
                        v.Region,
                        v.Country
                    )
                ),
            Names = names
                .Select(n => (n.Normalized, Venue: Survivor(n.Venue)))
                .Distinct()
                .ToLookup(n => n.Normalized, n => n.Venue),
            ExternalIds = ids.GroupBy(i => (i.Provider, i.Key))
                .ToDictionary(g => g.Key, g => Survivor(g.First().Venue)),
        };
    }

    /// A town written without its state ("New York City") takes the one state that town name
    /// has everywhere else in the data; a name found in several states (Portland) stays as is.
    internal static List<LegacyVenue> FillRegions(
        IReadOnlyList<LegacyVenue> rows,
        IEnumerable<Locality> catalogue
    )
    {
        var regions = rows.Select(r => r.Locality)
            .OfType<Locality>()
            .Concat(catalogue)
            .Where(l => l.Region != null || l.Country != null)
            .GroupBy(l => TitleNormalizer.Normalize(l.City))
            .ToDictionary(
                g => g.Key,
                g => g.Select(l => (l.Region, l.Country)).Distinct().ToList()
            );
        return rows.Select(r =>
                r.Locality is { Region: null, Country: null } l
                && regions.TryGetValue(TitleNormalizer.Normalize(l.City), out var only)
                && only.Count == 1
                    ? r with
                    {
                        Locality = l with { Region = only[0].Region, Country = only[0].Country },
                    }
                    : r
            )
            .ToList();
    }

    /// The provider ids legacy rows carry: "setlistfm:4bd6ca4a", "pt:12345". Other upstream
    /// identifiers are the name and location run together, not ids.
    public static (string Provider, string Key)? ParseExternalId(string? upstream)
    {
        var match = upstream == null ? null : ProviderId().Match(upstream);
        return match is { Success: true }
            ? (match.Groups[1].Value == "pt" ? "phantasytour" : "setlistfm", match.Groups[2].Value)
            : null;
    }

    [GeneratedRegex(@"^(setlistfm|pt):([0-9A-Za-z]+)$")]
    private static partial Regex ProviderId();
}
