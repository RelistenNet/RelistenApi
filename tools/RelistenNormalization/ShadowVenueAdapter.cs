using System.Text.Json;
using Relisten.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Projects every retained venue into a place/locality/history hypothesis. The
/// first pass indexes names and explicit evidence; it never compares all pairs.
/// A name and city retrieve alternatives, but cannot merge physical premises.
/// Production matching, review and acceptance remain separate from this reader.
/// </summary>
public sealed class ShadowVenueAdapter : IShadowCatalogAdapter
{
    private const int MaximumAlternatives = 20;
    private static readonly IReadOnlyList<string> Domains = ["venues"];
    private readonly Dictionary<Guid, ShadowVenueObservation> venues = [];
    private readonly Dictionary<string, HashSet<Guid>> names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<Guid>> providers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<Guid>> addresses = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> conflictingRows = [];
    private readonly Dictionary<string, bool> providerConsistency = new(StringComparer.Ordinal);
    private readonly IdentityCandidateMatcher matcher = new();

    private readonly ShadowVenueGeography? geography;

    public string Version =>
        "shadow-venue-physical-history-v2/geography=" + (geography?.EvidenceIdentity ?? "none");

    /// <param name="geographyManifest">Optional pinned offline GeoNames manifest. Null preserves parsed locality labels.</param>
    public ShadowVenueAdapter(string? geographyManifest = null)
    {
        geography = geographyManifest == null ? null : ShadowVenueGeography.Load(geographyManifest);
    }

    /// <summary>Use an already authenticated offline reference index.</summary>
    public ShadowVenueAdapter(ShadowVenueGeography geography)
    {
        this.geography = geography;
    }

    public IReadOnlyList<string> DomainsFor(string inputKind) =>
        inputKind is "venue_sample" or "venue" or "imported_venue" ? Domains : [];

    public void Observe(JsonElement record)
    {
        if (DomainsFor(record.GetProperty("kind").GetString()!).Count == 0)
        {
            return;
        }
        var venue = ShadowVenueObservation.Read(record.GetProperty("row"), geography);
        if (venues.TryGetValue(venue.Uuid, out var previous))
        {
            // Repeated legacy UUIDs may carry different snapshots. Keep their
            // output evidence, but do not use either version to join premises.
            if (!venue.SameMetadata(previous))
            {
                conflictingRows.Add(venue.Uuid);
                providerConsistency.Clear();
            }
            return;
        }
        venues.Add(venue.Uuid, venue);
        if (venue.HasName)
        {
            Index(names, venue.NameKey, venue.Uuid);
        }
        Index(providers, venue.ProviderGroup, venue.Uuid);
        Index(addresses, venue.AddressGroup, venue.Uuid);
        providerConsistency.Clear();
    }

    public IEnumerable<ShadowCatalogResult> Process(ShadowCatalogInput input)
    {
        if (DomainsFor(input.Kind).Count == 0)
        {
            yield break;
        }
        var venue = ShadowVenueObservation.Read(input.Row, geography);
        var grouping = Place(venue);
        var candidates = AlternativeIds(venue);
        var alternatives = candidates
            .OrderByDescending(id => Place(venues[id]).Key == grouping.Key)
            .ThenByDescending(id => venues[id].NameLocalityKey == venue.NameLocalityKey)
            .ThenBy(id => id)
            .Take(MaximumAlternatives)
            .Select(id => Alternative(venue, venues[id], grouping.Key))
            .ToArray();
        var unknowns = Unknowns(venue, grouping.Basis, candidates.Count);
        var enrichment = Enrichment(venue, grouping.Basis);
        var proposal = new ShadowVenueProposal(
            grouping.Key,
            venue.Name,
            venue.Scope,
            venue.Locality,
            venue.Coordinates,
            venue.PastNames,
            venue.ProviderReference,
            grouping.Basis,
            alternatives,
            candidates.Count,
            candidates.Count > alternatives.Length,
            unknowns,
            enrichment
        );
        var outcome =
            !venue.HasName
            || venue.MultiplePlaces
            || venue.Locality.BrowseKey == null
            || venue.Locality.Geography is { ProposedGeographicKey: null }
            || conflictingRows.Contains(venue.Uuid)
                ? "unresolved"
                : "proposed";
        var reason =
            !venue.HasName ? "missing_usable_venue_name"
            : conflictingRows.Contains(venue.Uuid) ? "conflicting_snapshots_for_legacy_uuid"
            : venue.MultiplePlaces ? "multiple_places_in_legacy_row"
            : venue.Locality.Geography is { ProposedGeographicKey: null } geographic
                ? geographic.Status
            : venue.Locality.BrowseKey == null ? "locality_requires_interpretation"
            : grouping.Basis == "retained_venue_row" ? "physical_identity_requires_more_evidence"
            : null;
        var entityKeys = new List<string> { grouping.Key };
        if (venue.Locality.BrowseKey != null)
        {
            entityKeys.Add(venue.Locality.BrowseKey);
        }
        if (venue.Locality.Geography?.ProposedGeographicKey is { } geographicKey)
        {
            entityKeys.Add(geographicKey);
        }
        if (venue.ProviderGroup != null)
        {
            // This joins provider records for inspection even when their location
            // histories conflict; its namespace explicitly differs from a place.
            entityKeys.Add(ShadowCatalogIdentity.Key("venue_reference", venue.ProviderGroup));
        }
        yield return ShadowCatalogResult.For(
            input,
            "venues",
            outcome,
            Version,
            proposal,
            new
            {
                NativeVenue = input.Row.Clone(),
                NameField = "name",
                LocationField = "location",
                HistoryField = "past_names",
                CoordinatesClassified = false,
                LegacyUuidEstablishesOnePremises = false,
                NameAndLocalityEstablishIdentity = false,
            },
            reason,
            entityKeys
        );
    }

    private (string Key, string Basis) Place(ShadowVenueObservation venue)
    {
        if (conflictingRows.Contains(venue.Uuid))
        {
            return (venue.ConflictingObservationKey(), "conflicting_catalog_observation");
        }
        if (venue.CanProposePhysicalGroup)
        {
            if (venue.ProviderGroup is { } provider && ProviderLocationsAgree(provider))
            {
                return (
                    ShadowCatalogIdentity.Key("venue", "provider\n" + provider),
                    "qualified_provider_venue_name_and_locality"
                );
            }
            if (venue.AddressGroup is { } address)
            {
                return (
                    ShadowCatalogIdentity.Key("venue", "address\n" + address),
                    "exact_name_street_address_and_locality"
                );
            }
        }
        return (
            ShadowCatalogIdentity.Key("venue", "catalog\n" + venue.Uuid.ToString("D")),
            "retained_venue_row"
        );
    }

    private bool ProviderLocationsAgree(string provider)
    {
        if (providerConsistency.TryGetValue(provider, out var agrees))
        {
            return agrees;
        }
        if (!providers.TryGetValue(provider, out var ids))
        {
            return false;
        }
        var members = ids.Select(id => venues[id]).ToArray();
        agrees =
            members.All(venue =>
                venue.CanProposePhysicalGroup && !conflictingRows.Contains(venue.Uuid)
            )
            && members.Select(venue => venue.NameKey).Distinct().Count() == 1
            && members.Select(venue => venue.Locality.PhysicalConstraintKey).Distinct().Count() == 1
            && members.Select(venue => venue.Scope).Distinct().Count() == 1
            && members
                .Where(venue => venue.Locality.Address != null)
                .Select(venue => ShadowVenueText.Key(venue.Locality.Address))
                .Distinct()
                .Count() <= 1;
        providerConsistency.Add(provider, agrees);
        return agrees;
    }

    private HashSet<Guid> AlternativeIds(ShadowVenueObservation venue)
    {
        var result = new HashSet<Guid>();
        if (venue.HasName && names.TryGetValue(venue.NameKey, out var sameName))
        {
            result.UnionWith(sameName);
        }
        if (
            venue.ProviderGroup is { } provider
            && providers.TryGetValue(provider, out var sameProvider)
        )
        {
            result.UnionWith(sameProvider);
        }
        if (
            venue.AddressGroup is { } address
            && addresses.TryGetValue(address, out var sameAddress)
        )
        {
            result.UnionWith(sameAddress);
        }
        result.Remove(venue.Uuid);
        return result;
    }

    private ShadowVenueAlternative Alternative(
        ShadowVenueObservation source,
        ShadowVenueObservation other,
        string placeKey
    )
    {
        var otherPlace = Place(other);
        var relationship =
            otherPlace.Key == placeKey ? "proposed_same_physical_place"
            : source.Locality.PhysicalConstraintKey == null
            || other.Locality.PhysicalConstraintKey == null
                ? "locality_unknown"
            : source.Locality.PhysicalConstraintKey != other.Locality.PhysicalConstraintKey
                ? "different_locality_or_location_history"
            : source.Scope != other.Scope ? "room_stage_or_site_scope_unresolved"
            : source.Locality.Address != null
            && other.Locality.Address != null
            && ShadowVenueText.Key(source.Locality.Address)
                != ShadowVenueText.Key(other.Locality.Address)
                ? "different_address_or_relocation"
            : source.NameKey == other.NameKey ? "same_name_locality_premises_unresolved"
            : "provider_reference_premises_unresolved";
        return new(
            other.Uuid,
            other.ArtistId,
            other.Name,
            other.Location,
            other.Scope,
            other.Locality.Address,
            relationship,
            otherPlace.Key,
            matcher.CompareVenues(source.MatchRecord(), other.MatchRecord())
        );
    }

    private IReadOnlyList<string> Unknowns(
        ShadowVenueObservation venue,
        string basis,
        int alternatives
    )
    {
        var result = new List<string>
        {
            "physical_identity_not_accepted",
            "current_name_validity_dates_unknown",
            "historical_geographic_boundaries_unknown",
        };
        if (venue.Locality.BrowseKey == null)
        {
            result.Add("locality_unresolved");
        }
        if (venue.Locality.Geography is { ProposedGeographicKey: null } geographic)
        {
            result.Add(geographic.Status);
        }
        if (venue.MultiplePlaces)
        {
            result.Add("possible_multiple_places_in_one_legacy_row");
        }
        if (venue.Coordinates != null)
        {
            result.Add("coordinate_precision_and_entity_scope_unknown");
        }
        if (venue.PastNames.Count > 0)
        {
            result.Add("past_names_do_not_establish_same_premises_or_complete_history");
        }
        if (basis == "retained_venue_row" && alternatives > 0)
        {
            result.Add("namesake_or_relocation_not_resolved");
        }
        if (venue.ProviderGroup is { } provider && !ProviderLocationsAgree(provider))
        {
            result.Add("provider_venue_name_location_or_scope_conflict");
        }
        if (conflictingRows.Contains(venue.Uuid))
        {
            result.Add("conflicting_snapshots_for_legacy_uuid");
        }
        return result;
    }

    private static IReadOnlyList<string> Enrichment(ShadowVenueObservation venue, string basis)
    {
        var result = new List<string>();
        if (!venue.HasName)
        {
            result.Add("interpret_missing_or_placeholder_venue");
        }
        if (venue.Locality.BrowseKey == null || venue.MultiplePlaces)
        {
            result.Add("parse_unstructured_locality_or_multiple_places");
        }
        if (venue.Locality.Geography is { ProposedGeographicKey: null })
        {
            result.Add("resolve_gazetteer_ambiguity_or_missing_geographic_evidence");
        }
        if (venue.PastNames.Count > 0)
        {
            result.Add("interpret_name_history_and_premises_scope");
        }
        if (basis is "retained_venue_row" or "conflicting_catalog_observation")
        {
            result.Add("acquire_venue_authority_or_history_evidence");
        }
        return result;
    }

    private static void Index(Dictionary<string, HashSet<Guid>> index, string? key, Guid uuid)
    {
        if (key == null)
        {
            return;
        }
        if (!index.TryGetValue(key, out var members))
        {
            members = [];
            index.Add(key, members);
        }
        members.Add(uuid);
    }
}
