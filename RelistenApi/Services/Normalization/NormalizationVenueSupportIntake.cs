using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>
/// Projects concrete retained place evidence into matching facts. GeoNames supplies geography,
/// never a venue pin or physical identity. MusicBrainz place evidence qualifies only an original
/// matching place ID and an explicit Venue type. No name search result assigns a venue identity.
/// </summary>
public sealed class NormalizationVenueSupportIntake
{
    public const string GeographyCaptureVersion = "geonames-venue-locality-search-json-v1";
    public const string PlaceCaptureVersion = "musicbrainz-venue-place-json-v1";

    /// <summary>The capture resource must name the exact complete structured locality query.</summary>
    public static NormalizationResourceKey GeographyResource(
        string city,
        string country,
        string region
    ) =>
        new(
            "geonames.org",
            "venue-locality-search",
            "name_equals="
                + Uri.EscapeDataString(city)
                + "&country="
                + Uri.EscapeDataString(country)
                + "&adminCode1="
                + Uri.EscapeDataString(region)
                + "&featureClass=P&style=FULL"
        );

    public SupportedVenueMatchProjection PrepareSource(
        VenueResolutionClaim claim,
        VenueMatchRecord original,
        NormalizationInputRevision? geography,
        NormalizationInputRevision? place
    )
    {
        var observation = claim.Observation;
        var evidence = new List<NormalizedEvidenceReference> { claim.Evidence };
        var location = ResolveLocation(
            observation.City?.Value,
            observation.Country?.Value,
            observation.Region?.Value,
            observation.Address?.Value,
            geography,
            evidence
        );
        var identities = QualifyPlace(original.ExternalIdentities, place, evidence);
        return new(
            original with
            {
                ExternalIdentities = identities,
                LocationAssertions = [new(location)],
            },
            evidence.Distinct().ToArray()
        );
    }

    public SupportedVenueMatchProjection PrepareCandidate(
        VenueCandidateFacts candidate,
        VenueMatchRecord original,
        NormalizationInputRevision? geography,
        NormalizationInputRevision? place
    )
    {
        var evidence = candidate
            .Names.SelectMany(value => value.Provenance.Evidence)
            .Concat(candidate.Locations.SelectMany(value => value.Provenance.Evidence))
            .Concat(candidate.ExternalIdentities.SelectMany(value => value.Provenance.Evidence))
            .ToList();
        var locations = candidate
            .Locations.Select(value => new MatchingVenueLocationAssertion(
                ResolveLocation(
                    value.City,
                    value.Country,
                    value.Region,
                    value.Address,
                    geography,
                    evidence
                ),
                value.ValidFrom,
                value.ValidTo
            ))
            .ToArray();
        var identities = QualifyPlace(original.ExternalIdentities, place, evidence);
        return new(
            original with
            {
                ExternalIdentities = identities,
                LocationAssertions = locations,
            },
            evidence.Distinct().ToArray()
        );
    }

    private static MatchingVenueLocation ResolveLocation(
        string? city,
        string? country,
        string? region,
        string? address,
        NormalizationInputRevision? input,
        List<NormalizedEvidenceReference> evidence
    )
    {
        if (input == null)
        {
            return new(null, null, address);
        }
        RequireContract(input, "geonames.org", "venue-locality-search", GeographyCaptureVersion);
        using var document = JsonDocument.Parse(input.Payload);
        var root = document.RootElement;
        if (
            !root.TryGetProperty("geonames", out var rows)
            || rows.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("totalResultsCount", out var total)
            || !total.TryGetInt32(out var count)
            || count != rows.GetArrayLength()
        )
        {
            throw new ArgumentException(
                "Venue geography requires a complete GeoNames result, not a truncated page."
            );
        }
        if (
            string.IsNullOrWhiteSpace(city)
            || country is not { Length: 2 }
            || string.IsNullOrWhiteSpace(region)
            || input.Resource != GeographyResource(city, country, region)
        )
        {
            return new(null, null, address);
        }

        var candidates = rows.EnumerateArray()
            .Select((value, index) => (Value: value, Index: index))
            .Where(value =>
                Text(value.Value, "fcl") == "P"
                && Equal(Text(value.Value, "countryCode"), country)
                && Equal(Text(value.Value, "adminCode1"), region)
                && (
                    Equal(Text(value.Value, "name"), city)
                    || Equal(Text(value.Value, "toponymName"), city)
                )
            )
            .Where(value =>
                value.Value.TryGetProperty("geonameId", out var id)
                && id.TryGetInt64(out var number)
                && number > 0
            )
            .ToArray();
        var identities = candidates
            .Select(value => value.Value.GetProperty("geonameId").GetInt64())
            .Distinct()
            .ToArray();
        if (identities.Length != 1)
        {
            return new(null, null, address);
        }
        evidence.AddRange(
            candidates.Select(value => new NormalizedEvidenceReference(
                input.Uuid,
                "/geonames/" + value.Index.ToString(CultureInfo.InvariantCulture)
            ))
        );
        return new(
            country.ToUpperInvariant(),
            "geonames:" + identities[0].ToString(CultureInfo.InvariantCulture),
            address
        );
    }

    private static IReadOnlyList<MatchingExternalIdentity> QualifyPlace(
        IReadOnlyList<MatchingExternalIdentity> identities,
        NormalizationInputRevision? input,
        List<NormalizedEvidenceReference> evidence
    )
    {
        var unqualified = identities
            .Select(value => value with { IdentifiesEntity = false, ExclusiveWithinEntity = false })
            .ToArray();
        if (input == null)
        {
            return unqualified;
        }
        RequireContract(input, "musicbrainz.org", "place", PlaceCaptureVersion);
        using var document = JsonDocument.Parse(input.Payload);
        var root = document.RootElement;
        if (
            !Guid.TryParse(input.Resource.ProviderKey, out var resourceId)
            || !Guid.TryParse(Text(root, "id"), out var responseId)
            || resourceId != responseId
        )
        {
            throw new ArgumentException(
                "MusicBrainz place evidence must match the requested place identity."
            );
        }
        // Other place types need their own physical-site/room contract. Search rank, coordinates
        // and a same-name place never qualify an identifier absent from the original assertion.
        if (Text(root, "type") != "Venue")
        {
            return unqualified;
        }
        var qualified = unqualified
            .Select(identity =>
                identity with
                {
                    IdentifiesEntity =
                        (
                            identity.Provider == "musicbrainz.org"
                            || identity.Provider == "musicbrainz"
                        )
                        && identity.ResourceType == "place"
                        && Guid.TryParse(identity.ProviderKey, out var id)
                        && id == resourceId,
                }
            )
            .ToArray();
        if (qualified.Any(value => value.IdentifiesEntity))
        {
            evidence.Add(new(input.Uuid, "/id"));
            evidence.Add(new(input.Uuid, "/type"));
        }
        return qualified;
    }

    private static void RequireContract(
        NormalizationInputRevision input,
        string provider,
        string type,
        string version
    )
    {
        if (
            input.Resource.Provider != provider
            || input.Resource.ResourceType != type
            || input.CaptureVersion != version
        )
        {
            throw new ArgumentException("Unsupported retained venue support representation.");
        }
    }

    private static string? Text(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString()
            : null;

    private static bool Equal(string? first, string? second) =>
        first != null
        && second != null
        && StringComparer.OrdinalIgnoreCase.Equals(first.Trim(), second.Trim());
}
