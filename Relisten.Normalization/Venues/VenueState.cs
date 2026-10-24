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

    /// Name identity (VenueNormalizer.Identities) → venues: what identity compares.
    public ILookup<string, Guid> IdentityNames =>
        identityNames ??= Names
            .SelectMany(g =>
                VenueNormalizer
                    .Identities(g.Key)
                    .SelectMany(id => g.Select(v => (Id: id, Venue: v)))
            )
            .Distinct()
            .ToLookup(n => n.Id, n => n.Venue);

    private ILookup<string, Guid>? identityNames;

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
            Unmapped = WithoutLoneTowns(
                WithoutPlacesInTowns(
                    WithoutStateTypos(
                        WithoutCodeTypos(
                            FoldCities(CountryCodes(FillRegions(legacy, catalogue), catalogue))
                        )
                    )
                )
            ),
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
            .GroupBy(l => VenueNormalizer.Normalize(l.City))
            .ToDictionary(
                g => g.Key,
                g => g.Select(l => (l.Region, l.Country)).Distinct().ToList()
            );
        return rows.Select(r =>
                r.Locality is { Region: null, Country: null } l
                && regions.TryGetValue(VenueNormalizer.Normalize(l.City), out var only)
                && only.Count == 1
                    ? r with
                    {
                        Locality = l with { Region = only[0].Region, Country = only[0].Country },
                    }
                    : r
            )
            .ToList();
    }

    /// "Amsterdam, NL", "Lorsch, DE", "Toronto, CA": a state code that is also a country's code
    /// names the country when the town is found elsewhere only in that country, never in the
    /// state's. The row takes the town's most used locality there.
    internal static List<LegacyVenue> CountryCodes(
        IReadOnlyList<LegacyVenue> rows,
        IEnumerable<Locality> catalogue
    )
    {
        static string? Code(LegacyVenue r) =>
            r.Locality is { Region: { Length: 2 } code, Country: { } country }
            && r.Location != null
            && VenueNormalizer.Normalize(r.Location.Split(',')[^1]) == code.ToLowerInvariant()
            && VenueNormalizer.IsCountryCode(code)
            && !code.Equals(country, StringComparison.OrdinalIgnoreCase)
                ? code.ToUpperInvariant()
                : null;

        var towns = rows.Where(r => Code(r) == null && r.Locality is { Trusted: true })
            .Select(r => (Locality: r.Locality!, Uses: Math.Max(r.Uses, 1)))
            .Concat(catalogue.Where(l => l.Country != null).Select(l => (Locality: l, Uses: 1)))
            .GroupBy(t => VenueNormalizer.Normalize(t.Locality.City))
            .ToDictionary(g => g.Key, g => g.ToList());
        return rows.Select(r =>
            {
                var code = Code(r);
                if (
                    code == null
                    || !towns.TryGetValue(VenueNormalizer.Normalize(r.Locality!.City), out var seen)
                    || seen.Any(t => t.Locality.Country != code)
                )
                {
                    return r;
                }

                var locality = seen.GroupBy(t => t.Locality.Key)
                    .OrderByDescending(g => g.Sum(t => t.Uses))
                    .First()
                    .First()
                    .Locality;
                return r with { Locality = locality };
            })
            .ToList();
    }

    /// A rarely used spelling of a town folds into a far more used one a typo away in the same
    /// state ("brommfield" → Broomfield, "Chilcothe" → Chillicothe): three times the uses, and
    /// one edit (two for long names) apart.
    internal static List<LegacyVenue> FoldCities(IReadOnlyList<LegacyVenue> rows)
    {
        var uses = rows.Where(r => r.Locality is { Trusted: true })
            .GroupBy(r =>
                (Region: r.Locality!.RegionKey!, City: VenueNormalizer.Normalize(r.Locality.City))
            )
            .ToDictionary(
                g => g.Key,
                g => (Uses: g.Sum(r => Math.Max(r.Uses, 1)), City: g.First().Locality!.City)
            );
        var byRegion = uses
            .Keys.GroupBy(k => k.Region)
            .ToDictionary(g => g.Key, g => g.Select(k => k.City).ToList());
        var folded = new Dictionary<(string, string), string>();
        foreach (var (key, value) in uses)
        {
            var limit = key.City.Length >= 7 ? 2 : 1;
            var target = byRegion[key.Region]
                .Where(c => c != key.City && uses[(key.Region, c)].Uses >= 3 * value.Uses)
                .Where(c => Titles.TitleGrouper.BoundedLevenshtein(c, key.City, limit) <= limit)
                .OrderByDescending(c => uses[(key.Region, c)].Uses)
                .FirstOrDefault();
            if (target != null)
            {
                folded[key] = uses[(key.Region, target)].City;
            }
        }

        return rows.Select(r =>
                r.Locality is { Trusted: true } l
                && folded.TryGetValue(
                    (l.RegionKey!, VenueNormalizer.Normalize(l.City)),
                    out var city
                )
                    ? r with
                    {
                        Locality = l with { City = city },
                    }
                    : r
            )
            .ToList();
    }

    /// One name in a town of one name in another state, used a tenth as much, is a typo'd state
    /// ("Red Rocks — Morrison, CA" beside 130 rows in Morrison, CO): those rows take the
    /// dominant locality.
    internal static List<LegacyVenue> WithoutStateTypos(IReadOnlyList<LegacyVenue> rows)
    {
        var fixedRows = rows.ToList();
        var groups = Enumerable
            .Range(0, rows.Count)
            .Where(i => rows[i].Key != null && rows[i].Locality is { Trusted: true })
            .GroupBy(i =>
                (
                    Name: VenueNormalizer.Identities(rows[i].Key!)[0],
                    City: VenueNormalizer.Normalize(rows[i].Locality!.City)
                )
            );
        foreach (var town in groups)
        {
            var regions = town.GroupBy(i => rows[i].Locality!.RegionKey)
                .OrderByDescending(g => g.Sum(i => Math.Max(rows[i].Uses, 1)))
                .ToList();
            if (regions.Count < 2)
            {
                continue;
            }

            var main = regions[0].Sum(i => Math.Max(rows[i].Uses, 1));
            var dominant = rows[regions[0].First()].Locality;
            foreach (
                var i in regions
                    .Skip(1)
                    .Where(g => g.Sum(i => Math.Max(rows[i].Uses, 1)) * 10 <= main)
                    .SelectMany(g => g)
            )
            {
                fixedRows[i] = rows[i] with { Locality = dominant };
            }
        }

        return fixedRows;
    }

    /// "Omaha, NB": a state code a letter off the town's usual one, naming a province of
    /// another country (New Brunswick for Nebraska), is a typo when the town is used three
    /// times as much in the usual state. Springfield, MA and MO are one country: both stay.
    internal static List<LegacyVenue> WithoutCodeTypos(IReadOnlyList<LegacyVenue> rows)
    {
        var towns = rows.Where(r => r.Locality is { Trusted: true, Region.Length: 2 })
            .GroupBy(r => VenueNormalizer.Normalize(r.Locality!.City))
            .ToDictionary(
                g => g.Key,
                g =>
                    g.GroupBy(r => r.Locality!.Key)
                        .Select(t =>
                            (Locality: t.First().Locality!, Uses: t.Sum(r => Math.Max(r.Uses, 1)))
                        )
                        .OrderByDescending(t => t.Uses)
                        .ToList()
            );
        return rows.Select(r =>
            {
                if (
                    r.Locality is not { Trusted: true, Region.Length: 2 } l
                    || towns[VenueNormalizer.Normalize(l.City)] is not { Count: > 1 } localities
                    || localities[0].Locality.Key == l.Key
                )
                {
                    return r;
                }

                var usual = localities[0];
                var here = localities.First(t => t.Locality.Key == l.Key).Uses;
                return
                    usual.Locality.Country != l.Country
                    && usual.Locality.Region is { Length: 2 } code
                    && Titles.TitleGrouper.BoundedLevenshtein(code, l.Region!, 1) <= 1
                    && usual.Uses >= 3 * here
                    ? r with
                    {
                        Locality = usual.Locality,
                    }
                    : r;
            })
            .ToList();
    }

    /// "The Sherman Theater Stroudsburg, PA": a town no other venue is filed under that ends
    /// with another town of the state ("stroudsburg") is that town, with the venue's name in
    /// front. West Palm Beach and South Burlington are towns of their own: a direction or
    /// another town prefix in front never counts, nor does a town other venues use.
    internal static List<LegacyVenue> WithoutPlacesInTowns(IReadOnlyList<LegacyVenue> rows)
    {
        var names = rows.Where(r => r.Key != null && r.Locality is { Trusted: true })
            .GroupBy(r => r.Locality!.Key)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => VenueNormalizer.Identities(r.Key!)[0]).Distinct().Count()
            );
        var towns = rows.Where(r => r.Locality is { Trusted: true })
            .GroupBy(r => r.Locality!.RegionKey!)
            .ToDictionary(
                g => g.Key,
                g =>
                    g.GroupBy(r => VenueNormalizer.Normalize(r.Locality!.City))
                        .ToDictionary(t => t.Key, t => t.First().Locality!)
            );
        return rows.Select(r =>
            {
                if (r.Locality is not { Trusted: true } l || names.GetValueOrDefault(l.Key) > 1)
                {
                    return r;
                }

                var city = VenueNormalizer.Normalize(l.City);
                var town = towns[l.RegionKey!]
                    .Where(t =>
                        t.Key != city
                        && city.EndsWith(" " + t.Key, StringComparison.Ordinal)
                        && city[..^(t.Key.Length + 1)].Split(' ') is var front
                        && !(front.Length == 1 && TownPrefixes.Contains(front[0]))
                    )
                    .OrderByDescending(t => t.Key.Length)
                    .Select(t => t.Value)
                    .FirstOrDefault();
                return town == null ? r : r with { Locality = town };
            })
            .ToList();
    }

    private static readonly HashSet<string> TownPrefixes =
    [
        "north",
        "south",
        "east",
        "west",
        "new",
        "old",
        "fort",
        "ft",
        "mount",
        "mt",
        "port",
        "lake",
        "upper",
        "lower",
        "little",
        "great",
        "saint",
        "st",
        "san",
        "santa",
        "el",
        "la",
        "los",
        "las",
        "palm",
        "glen",
        "bay",
        "point",
    ];

    /// A "town" no other venue name is ever filed under is usually not a town ("Days Inn St
    /// Joseph", "The Sherman Theater Stroudsburg", "Bonnaroo"): when the same name is used at
    /// least twice as much in one other town of the state, its rows take that town.
    internal static List<LegacyVenue> WithoutLoneTowns(IReadOnlyList<LegacyVenue> rows)
    {
        var trusted = rows.Where(r => r.Key != null && r.Locality is { Trusted: true }).ToList();
        var namesInTown = trusted
            .GroupBy(r => r.Locality!.Key)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => VenueNormalizer.Identities(r.Key!)[0]).Distinct().Count()
            );
        var uses = trusted
            .GroupBy(r => (Name: VenueNormalizer.Identities(r.Key!)[0], r.Locality!.RegionKey))
            .ToDictionary(
                g => g.Key,
                g =>
                    g.GroupBy(r => r.Locality!.Key)
                        .Select(t =>
                            (Locality: t.First().Locality!, Uses: t.Sum(r => Math.Max(r.Uses, 1)))
                        )
                        .OrderByDescending(t => t.Uses)
                        .ToList()
            );
        return rows.Select(r =>
            {
                if (
                    r.Key == null
                    || r.Locality is not { Trusted: true } l
                    || namesInTown[l.Key] > 1
                )
                {
                    return r;
                }

                var towns = uses[(VenueNormalizer.Identities(r.Key)[0], l.RegionKey)];
                var here = towns.First(t => t.Locality.Key == l.Key).Uses;
                var elsewhere = towns.Where(t => t.Locality.Key != l.Key).ToList();
                return elsewhere.Count == 1 && elsewhere[0].Uses >= 2 * here
                    ? r with
                    {
                        Locality = elsewhere[0].Locality,
                    }
                    : r;
            })
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
