using System;
using System.Collections.Generic;
using System.Linq;

namespace Relisten.Search;

internal static class VenueSearchIdentity
{
    public static IEnumerable<RankedCandidate> Collapse(
        IEnumerable<RankedCandidate> ranked,
        SearchVocabulary vocabulary
    ) => ranked.GroupBy(candidate => Key(candidate.Row, vocabulary)).Select(BestVenueRoute);

    private static RankedCandidate BestVenueRoute(IEnumerable<RankedCandidate> group)
    {
        var bestMatch = group.First();
        var canonical = group.FirstOrDefault(candidate =>
            candidate.Row is SearchDocument { Kind: "venue" } venue
            && venue.VenueId.StartsWith("venue_", StringComparison.Ordinal)
        );

        // Keep the evidence that put the place here. Use the canonical row's
        // route when search found it as well as an imported alias.
        return canonical == null ? bestMatch : bestMatch with { Row = canonical.Row };
    }

    private static (string Name, string Area) Key(ISearchRankItem row, SearchVocabulary vocabulary)
    {
        if (
            row is not SearchDocument { Kind: "venue" } venue
            || Area(venue.CityLabel, venue.LocationLabel) is not { } area
        )
        {
            return (row.Key, "");
        }

        // An imported name can resolve through a canonical alias in the same
        // city. If several names fit, keep the imported name's own identity.
        var canonical = vocabulary
            .VenuesFor(venue.Name)
            .Where(place => Area(place.CityLabel, place.LocationLabel) == area)
            .Select(place => Name(place.Name))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        return (canonical is [var name] ? name : Name(venue.Name), area);
    }

    private static string Name(string name) =>
        SearchText
            .Fold(name)
            .Replace("theater", "theatre", StringComparison.Ordinal)
            .Replace(" park and amphitheatre", " amphitheatre", StringComparison.Ordinal);

    private static string? Area(string cityLabel, string? locationLabel)
    {
        var parts = (locationLabel ?? "").Split(',');
        var city = SearchText.Fold(cityLabel.Length > 0 ? cityLabel : parts[0]);
        var region = parts.Length > 1 ? SearchText.Fold(parts[1]) : "";
        return (city, region) switch
        {
            ("nyc", "" or "ny") or ("ny", "ny") or ("new york", "new york" or "ny") =>
                "new york,ny",
            ("" or "unknown location", _) => null,
            _ => city + "," + region,
        };
    }
}
