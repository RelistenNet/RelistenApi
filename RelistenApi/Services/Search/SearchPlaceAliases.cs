using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Relisten.Identifiers;
using Relisten.Normalization.Venues;

namespace Relisten.Search;

internal sealed record SearchPlaceAlias(
    string Label,
    FrozenSet<string> VenueIds,
    bool IsCity,
    SearchVenueAliasTarget[]? AmbiguousVenues = null
);

internal sealed record SearchVenueAliasTarget(
    string Id,
    string Label,
    string City,
    FrozenSet<string> VenueIds
);

internal static class SearchPlaceAliases
{
    private static readonly FrozenSet<string> GenericVenueWords = """
        amphitheater amphitheatre arena auditorium ballroom bar bowl cafe center centre city civic club coliseum
        community concert convention event events fairground fairgrounds festival field garden gardens grounds
        hall house lounge main memorial music park pavilion performing performingarts rec recreation room
        sports stadium stage theatre theater venue arts
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToFrozenSet(
        StringComparer.Ordinal
    );

    // A bare noun is weak venue evidence. A distinctive full name such as
    // "The Beacon" still gets checked against literal catalog names.
    private static readonly FrozenSet<string> CommonVenueNouns = """
        american assembly backyard beacon bluebird canary cannery canopy chameleon copper cradle crystal
        factory farm forum gorge landmark landing majesty marquee meadow meadows mystic observatory
        night paradise pines plaza pony quad radio republic revolution river riverside sandstone soapbox southern state
        steel tabernacle tower township tractor uptown variety varsity water wharf world
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToFrozenSet(
        StringComparer.Ordinal
    );

    internal sealed record Readings(
        FrozenDictionary<string, SearchPlaceAlias> Aliases,
        FrozenSet<(string VenueId, string Key)> NoUnqualifiedReading
    );

    internal sealed record VenueNameDecision(
        SearchProjectionRows.ShortPlaceName Name,
        string Key,
        SearchProjectionRows.Venue? Target,
        string? RejectedBecause
    );

    public static Readings Build(
        IReadOnlyDictionary<string, SearchProjectionRows.Venue> venues,
        IEnumerable<SearchProjectionRows.ShortPlaceName> venueNames,
        IEnumerable<SearchProjectionRows.CityPlaceName> cityNames,
        IEnumerable<SearchProjectionRows.ImportedPlace> imports,
        IReadOnlySet<string>? catalogCollisions = null
    )
    {
        var cities = cityNames.ToArray();
        var places = imports
            .Select(row =>
                (
                    Id: UuidTypeId.Format("imported_venue", row.Uuid),
                    MappedVenueId: SearchProjectionRows.CurrentVenue(venues, row.MappedVenueId)?.Id,
                    Locality: VenueNormalizer.ParseLocation(row.Location)
                )
            )
            .ToArray();
        var importedByVenue = places
            .Where(place => place.MappedVenueId != null)
            .ToLookup(place => place.MappedVenueId!, place => place.Id);
        var venuesByCity = venues
            .Values.Where(venue => venue.MergedInto == null && venue.City != null)
            .Select(venue =>
                (venue.Id, Location: new Locality(venue.City!, venue.Region, venue.Country))
            )
            .Concat(
                places
                    .Where(place => place.Locality != null)
                    .Select(place => (place.Id, Location: place.Locality!))
            )
            .ToLookup(place => place.Location.Key, place => place.Id);
        var decisions = DecideVenueNames(venues, venueNames, catalogCollisions);
        var aliases = CityReadings(cities, venuesByCity);
        var catalogVenuesByName = venues
            .Values.SelectMany(venue =>
                venue
                    .Aliases.Prepend(venue.Name)
                    .Select(name =>
                        (
                            Key: SearchText.Fold(name),
                            Venue: SearchProjectionRows.CurrentVenue(venues, venue.Id)
                        )
                    )
            )
            .Where(entry => entry.Venue != null)
            .ToLookup(entry => entry.Key, entry => entry.Venue!, StringComparer.Ordinal);
        var unqualified = new HashSet<(string VenueId, string Key)>();
        foreach (
            var group in decisions.Where(row => row.RejectedBecause == null).GroupBy(row => row.Key)
        )
        {
            var targets = group
                .Select(row => row.Target!)
                .Concat(catalogVenuesByName[group.Key])
                .DistinctBy(venue => venue.Id)
                .Select(venue => new SearchVenueAliasTarget(
                    venue.Id,
                    venue.Name,
                    SearchText.Fold(venue.City ?? ""),
                    importedByVenue[venue.Id].Prepend(venue.Id).ToFrozenSet(StringComparer.Ordinal)
                ))
                .ToArray();
            if (targets is [var target])
            {
                aliases.TryAdd(group.Key, new(target.Label, target.VenueIds, false));
                unqualified.Add((target.Id, group.Key));
            }
            else
            {
                // A place chain or reused room name needs context that selects one live venue.
                aliases.TryAdd(group.Key, new(group.Key, FrozenSet<string>.Empty, false, targets));
            }
        }

        return new(
            aliases.ToFrozenDictionary(StringComparer.Ordinal),
            decisions
                .Where(row => row.Target != null)
                .Select(row => (row.Target!.Id, row.Key))
                .Where(key => !unqualified.Contains(key))
                .ToFrozenSet()
        );
    }

    private static Dictionary<string, SearchPlaceAlias> CityReadings(
        IEnumerable<SearchProjectionRows.CityPlaceName> cities,
        ILookup<string, string> venuesByCity
    ) =>
        cities
            .SelectMany(name =>
                Keys(name.Name, name.Normalized).Select(key => (Key: key, Name: name))
            )
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var names = group.Select(entry => entry.Name);
                    var labels = names
                        .Select(name =>
                            $"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.City)}, {name.Region}"
                        )
                        .Distinct()
                        .Take(2)
                        .ToArray();
                    return new SearchPlaceAlias(
                        labels is [var exact] ? exact : group.Key,
                        names
                            .SelectMany(name =>
                                venuesByCity[new Locality(name.City, name.Region, name.Country).Key]
                            )
                            .ToFrozenSet(StringComparer.Ordinal),
                        true
                    );
                },
                StringComparer.Ordinal
            );

    internal static VenueNameDecision[] DecideVenueNames(
        IReadOnlyDictionary<string, SearchProjectionRows.Venue> venues,
        IEnumerable<SearchProjectionRows.ShortPlaceName> names,
        IReadOnlySet<string>? catalogCollisions
    ) =>
        names
            .SelectMany(name =>
            {
                var target = SearchProjectionRows.CurrentVenue(venues, name.VenueId);
                return Keys(name.Name, name.Normalized)
                    .Select(key => new VenueNameDecision(
                        name,
                        key,
                        target,
                        target == null ? "no live venue"
                            : name.ExplicitlyApproved ? null
                            : Rejection(key, catalogCollisions)
                    ));
            })
            .ToArray();

    private static string? Rejection(string key, IReadOnlySet<string>? catalogCollisions) =>
        WeakNameReason(key)
        ?? (catalogCollisions?.Contains(key) == true ? "exact artist or song name" : null);

    internal static string? WeakNameReason(string key)
    {
        var words = key.Split(' ');
        if (
            words.All(word =>
                GenericVenueWords.Contains(word)
                || word is ("a" or "an" or "at" or "and" or "for" or "of" or "on" or "the")
            )
        )
        {
            return "generic venue name or stopwords";
        }
        if (
            words.Length == 1
            && (ArtistInitialisms.IsCommonWord(key) || CommonVenueNouns.Contains(key))
        )
        {
            return "common English word";
        }
        return null;
    }

    internal static IEnumerable<string> Keys(string name, string normalized) =>
        new[] { SearchText.Fold(name), SearchText.Fold(normalized) }
            .Where(key => key.Length > 0)
            .Distinct(StringComparer.Ordinal);
}
