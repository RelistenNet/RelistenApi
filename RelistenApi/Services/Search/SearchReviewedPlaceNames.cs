using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Relisten.Normalization.Venues;

namespace Relisten.Search;

// Retire the bundle after the norm import and admin editor support these names.
internal static class SearchReviewedPlaceNames
{
    private static readonly Lazy<ReviewedPlaceAliases.ReviewedBatch> File = new(Load);

    public static string VenueKeysJson =>
        JsonSerializer.Serialize(
            File.Value.VenueNames.Select(name => new
            {
                venue_id = name.VenueId,
                normalized = name.Normalized,
            })
        );

    private static readonly Lazy<ILookup<string, string>> VenueSpellings = new(() =>
        File.Value.VenueNames.ToLookup(
            name => name.VenueId,
            name => name.Name,
            StringComparer.Ordinal
        )
    );

    public static IEnumerable<SearchProjectionRows.Venue> WithVenueSpellings(
        IEnumerable<SearchProjectionRows.Venue> venues
    ) =>
        venues.Select(venue =>
        {
            if (!VenueSpellings.Value.Contains(venue.Id))
            {
                return venue;
            }

            return venue with
            {
                AliasValues = VenueSpellings
                    .Value[venue.Id]
                    .Concat(venue.Aliases)
                    .DistinctBy(SearchText.Fold)
                    .OrderBy(SearchText.Fold, StringComparer.Ordinal)
                    .ToArray(),
            };
        });

    public static IEnumerable<SearchProjectionRows.ShortPlaceName> WithShortNames(
        IEnumerable<SearchProjectionRows.ShortPlaceName> stored
    ) =>
        File
            .Value.VenueNames.Select(name => new SearchProjectionRows.ShortPlaceName(
                name.VenueId,
                name.Name,
                name.Normalized,
                true
            ))
            .Concat(stored)
            .DistinctBy(name => (name.VenueId, name.Normalized));

    public static IEnumerable<SearchProjectionRows.CityPlaceName> CityNames =>
        File.Value.CityNames.Select(name => new SearchProjectionRows.CityPlaceName(
            name.City,
            name.Region,
            name.Country,
            name.Name,
            name.Normalized
        ));

    private static ReviewedPlaceAliases.ReviewedBatch Load()
    {
        using var resource =
            typeof(SearchReviewedPlaceNames).Assembly.GetManifestResourceStream(
                "Relisten.Search.ReviewedPlaceAliases.json"
            )
            ?? throw new InvalidDataException(
                "The reviewed search place names resource is missing."
            );
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        return ReviewedPlaceAliases.Read(bytes.ToArray());
    }
}

/// <summary>Reads and validates bundled place names.</summary>
internal static class ReviewedPlaceAliases
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static ReviewedBatch Read(ReadOnlySpan<byte> content)
    {
        var batch =
            JsonSerializer.Deserialize<Batch>(content, Json)
            ?? throw new InvalidDataException("Expected an approved place alias file.");
        var venueNames = batch.VenueNames ?? [];
        var cityNames = batch.CityNames ?? [];
        if (
            string.IsNullOrWhiteSpace(batch.ReviewedBy)
            || venueNames.Length + cityNames.Length == 0
        )
        {
            throw new InvalidDataException("Expected a reviewer and approved names.");
        }

        var venues = venueNames
            .Select(name =>
            {
                if (name.Kind != "short")
                {
                    throw new InvalidDataException("The approved venue names must be short names.");
                }
                return new ReviewedVenueName(
                    Relisten.Normalization.VenueId.Parse(name.VenueId).Value,
                    name.Name.Trim(),
                    NameKey(name.Name)
                );
            })
            .ToArray();
        var cities = cityNames
            .Select(name => new ReviewedCityName(
                VenueNormalizer.Normalize(name.City)
                    ?? throw new InvalidDataException($"Invalid city: {name.City}"),
                name.Region.Trim().ToUpperInvariant(),
                name.Country.Trim().ToUpperInvariant(),
                name.Name.Trim(),
                NameKey(name.Name)
            ))
            .ToArray();
        return new(batch.ReviewedBy.Trim(), venues, cities);
    }

    public sealed record ReviewedBatch(
        string ReviewedBy,
        ReviewedVenueName[] VenueNames,
        ReviewedCityName[] CityNames
    );

    public sealed record ReviewedVenueName(string VenueId, string Name, string Normalized);

    public sealed record ReviewedCityName(
        string City,
        string Region,
        string Country,
        string Name,
        string Normalized
    );

    private static string NameKey(string name) =>
        VenueNormalizer.Key(name) ?? throw new InvalidDataException($"Invalid place name: {name}");

    private sealed record Batch(string ReviewedBy, VenueName[] VenueNames, CityName[] CityNames);

    private sealed record VenueName(string VenueId, string Name, string Kind);

    private sealed record CityName(string City, string Region, string Country, string Name);
}
