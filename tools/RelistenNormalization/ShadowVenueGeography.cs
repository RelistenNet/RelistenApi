using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// A populated-place reference from an offline gazetteer. Coordinates describe
/// this geographic feature, never the entrance or footprint of a music venue.
/// </summary>
public sealed record ShadowGazetteerPlace(
    long GeonameId,
    string Name,
    string CountryCode,
    string Admin1Code,
    string Admin1Name,
    string FeatureCode,
    double Latitude,
    double Longitude,
    [property: JsonIgnore] IReadOnlyList<string> Names,
    [property: JsonIgnore] IReadOnlyList<string> Admin1Names
);

/// <summary>
/// One exact-name geographic proposal. A unique result is unique within the
/// retained cities500 snapshot; historical boundaries and name dates remain unknown.
/// </summary>
public sealed record ShadowGeographyResolution(
    string Status,
    string? ProposedGeographicKey,
    ShadowGazetteerPlace? Place,
    int CandidateCount,
    IReadOnlyList<ShadowGazetteerPlace> Candidates,
    bool CandidatesTruncated,
    string EvidenceIdentity,
    string Attribution = "GeoNames, CC BY 4.0: https://www.geonames.org/",
    string License = "https://creativecommons.org/licenses/by/4.0/",
    bool HistoricalValidityKnown = false
);

/// <summary>
/// Resolves locality wording against a caller-pinned GeoNames download. Matching
/// uses all exact aliases and supplied country/admin constraints. Ambiguous names
/// stay ambiguous; population and legacy venue coordinates never break a tie.
/// </summary>
public sealed class ShadowVenueGeography
{
    private readonly Dictionary<string, List<ShadowGazetteerPlace>> names = new(
        StringComparer.Ordinal
    );
    private readonly Dictionary<string, ShadowGeographyResolution> resolutions = new(
        StringComparer.Ordinal
    );
    public string EvidenceIdentity { get; }

    /// <summary>Build an offline index; callers must retain provenance for these reference rows.</summary>
    public ShadowVenueGeography(IEnumerable<ShadowGazetteerPlace> places, string evidenceIdentity)
    {
        EvidenceIdentity = evidenceIdentity;
        foreach (var place in places)
        {
            foreach (
                var alias in place.Names.Append(place.Name).Select(ShadowVenueText.Key).Distinct()
            )
            {
                if (alias.Length == 0)
                {
                    continue;
                }
                if (!names.TryGetValue(alias, out var entries))
                {
                    entries = [];
                    names.Add(alias, entries);
                }
                entries.Add(place);
            }
        }
    }

    /// <summary>
    /// Authenticate the manifest and original downloads before building an index.
    /// The returned method identity changes when any admitted input changes.
    /// </summary>
    public static ShadowVenueGeography Load(string pinnedManifest)
    {
        var separator = pinnedManifest.LastIndexOf("#sha256=", StringComparison.Ordinal);
        if (separator <= 0 || pinnedManifest.Length - separator != 72)
        {
            throw new ArgumentException(
                "Venue geography requires <manifest-path>#sha256=<64 hexadecimal digits>."
            );
        }
        var path = Path.GetFullPath(pinnedManifest[..separator]);
        var bytes = File.ReadAllBytes(path);
        var digest = Hash(bytes);
        if (!digest.Equals(pinnedManifest[(separator + 8)..], StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Venue geography manifest differs from the supplied digest."
            );
        }
        using var document = JsonDocument.Parse(bytes);
        var manifest = document.RootElement;
        if (manifest.GetProperty("version").GetString() != "shadow-geonames-cities500-v1")
        {
            throw new InvalidDataException("Unsupported venue geography input version.");
        }
        var directory = Path.GetDirectoryName(path)!;
        foreach (var name in new[] { "cities500.zip", "admin1CodesASCII.txt", "readme.txt" })
        {
            var receipt = manifest
                .GetProperty("files")
                .EnumerateArray()
                .Single(row => row.GetProperty("path").GetString() == name);
            var file = Path.Combine(directory, name);
            using var stream = File.OpenRead(file);
            if (
                stream.Length != receipt.GetProperty("bytes").GetInt64()
                || Convert.ToHexStringLower(SHA256.HashData(stream))
                    != receipt.GetProperty("sha256").GetString()
            )
            {
                throw new InvalidDataException(
                    "A retained venue geography download changed: " + name
                );
            }
        }
        return new(ReadPlaces(directory), digest);
    }

    public ShadowGeographyResolution Resolve(ShadowVenueLocality locality)
    {
        var key = JsonSerializer.Serialize(
            new[] { locality.City, locality.Region, locality.CountryCode, locality.Basis }
        );
        if (resolutions.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var status =
            locality.Basis is "conflicting_catalog_geography" or "unrecognized_catalog_geography"
                ? locality.Basis
            : locality.City == null ? "missing_city"
            : locality.CountryCode == null ? "country_required"
            : "no_exact_gazetteer_candidate";
        ShadowGazetteerPlace[] candidates = [];
        if (
            status == "no_exact_gazetteer_candidate"
            && names.TryGetValue(ShadowVenueText.Key(locality.City), out var entries)
        )
        {
            candidates = entries
                .Where(place => place.CountryCode == locality.CountryCode)
                .Where(place =>
                    locality.Region == null
                    || place
                        .Admin1Names.Append(place.Admin1Code)
                        .Any(name =>
                            ShadowVenueText.AdministrativeKey(name, place.CountryCode)
                            == ShadowVenueText.AdministrativeKey(locality.Region, place.CountryCode)
                        )
                )
                .DistinctBy(place => place.GeonameId)
                .OrderBy(place => place.GeonameId)
                .ToArray();
            status =
                candidates.Length == 1 ? "unique_exact_gazetteer_candidate"
                : candidates.Length > 1 ? "ambiguous_gazetteer_candidates"
                : status;
        }
        var place = candidates.Length == 1 ? candidates[0] : null;
        var result = new ShadowGeographyResolution(
            status,
            place == null ? null : "geonames:" + place.GeonameId,
            place,
            candidates.Length,
            candidates.Take(8).ToArray(),
            candidates.Length > 8,
            EvidenceIdentity
        );
        resolutions.Add(key, result);
        return result;
    }

    /// <summary>
    /// Check a literal locality against an already resolved contextual place. This
    /// does not search the world for a missing country: the caller must supply an
    /// evidenced place, and every supplied country and region must still agree.
    /// </summary>
    public ShadowGeographyResolution? ResolveWithContext(
        ShadowVenueLocality locality,
        ShadowGeographyResolution context
    )
    {
        if (
            locality.Basis is "conflicting_catalog_geography" or "unrecognized_catalog_geography"
            || locality.City == null
            || context.Place is not { } place
            || !names.TryGetValue(ShadowVenueText.Key(locality.City), out var entries)
            || !entries.Any(candidate => candidate.GeonameId == place.GeonameId)
            || locality.CountryCode is { } country && country != place.CountryCode
            || locality.Region is { } region
                && !place
                    .Admin1Names.Append(place.Admin1Code)
                    .Any(name =>
                        ShadowVenueText.AdministrativeKey(name, place.CountryCode)
                        == ShadowVenueText.AdministrativeKey(region, place.CountryCode)
                    )
        )
        {
            return null;
        }

        return context with
        {
            Status = "exact_alias_with_context_locality",
        };
    }

    private static IEnumerable<ShadowGazetteerPlace> ReadPlaces(string directory)
    {
        var admins = File.ReadLines(Path.Combine(directory, "admin1CodesASCII.txt"))
            .Select(line => line.Split('\t'))
            .ToDictionary(fields => fields[0], fields => fields);
        using var archive = ZipFile.OpenRead(Path.Combine(directory, "cities500.zip"));
        var entry =
            archive.GetEntry("cities500.txt")
            ?? throw new InvalidDataException("The cities500 archive has no cities500.txt.");
        if (entry.Length > 128 * 1024 * 1024)
        {
            throw new InvalidDataException(
                "The cities500 input exceeds its supported uncompressed size."
            );
        }
        using var reader = new StreamReader(entry.Open());
        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split('\t');
            if (fields.Length != 19 || fields[6] != "P")
            {
                throw new InvalidDataException("Unexpected GeoNames populated-place row.");
            }
            var adminKey = fields[8] + "." + fields[10];
            admins.TryGetValue(adminKey, out var admin);
            var aliases = fields[3]
                .Split(',')
                .Append(fields[1])
                .Append(fields[2])
                .Where(name => name.Length > 0)
                .Distinct()
                .ToArray();
            yield return new(
                long.Parse(fields[0], System.Globalization.CultureInfo.InvariantCulture),
                fields[1],
                fields[8],
                fields[10],
                admin?[1] ?? fields[10],
                fields[7],
                double.Parse(fields[4], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(fields[5], System.Globalization.CultureInfo.InvariantCulture),
                aliases,
                admin == null ? [fields[10]] : [fields[10], admin[1], admin[2]]
            );
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
