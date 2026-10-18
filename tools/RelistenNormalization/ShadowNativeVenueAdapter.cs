using System.Text.Json;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Reads cited native venue accounts without allocating catalog venue UUIDs.
/// Browse keys retrieve related wording; observation keys retain each original account.
/// </summary>
public sealed class ShadowNativeVenueAdapter(
    ShadowProviderObservations observations,
    ShadowVenueGeography? geography = null
) : IShadowCatalogAdapter
{
    private static readonly IReadOnlyList<string> Domains = ["venues"];

    public string Version =>
        "shadow-native-venues-v1/"
        + ShadowProviderObservations.Version
        + "/geography="
        + (geography?.EvidenceIdentity ?? "none");

    public IReadOnlyList<string> DomainsFor(string inputKind) =>
        inputKind == "provider_input" ? Domains : [];

    public void Observe(JsonElement record) => observations.Observe(record);

    public IEnumerable<ShadowCatalogResult> Process(ShadowCatalogInput input)
    {
        if (DomainsFor(input.Kind).Count == 0)
        {
            return [];
        }

        try
        {
            return [Project(input)];
        }
        catch (Exception error)
            when (error
                    is JsonException
                        or ArgumentException
                        or NotSupportedException
                        or InvalidDataException
            )
        {
            return
            [
                ShadowCatalogResult.For(
                    input,
                    "venues",
                    "unresolved",
                    Version,
                    null,
                    new { input.Key, Error = error.GetType().Name },
                    "provider_input_or_venue_evidence_invalid"
                ),
            ];
        }
    }

    private ShadowCatalogResult Project(ShadowCatalogInput input)
    {
        var source = observations.Read(input);
        var revisions = source
            .Recordings.Select(recording => recording.SupportingRootInput)
            .OfType<NormalizationInputRevision>()
            .Append(source.Input)
            .DistinctBy(revision => revision.Uuid)
            .ToDictionary(revision => revision.Uuid);

        var accounts = source
            .Decoded.Venues.Concat(
                source
                    .Decoded.Setlists.Select(setlist => setlist.Venue)
                    .OfType<ProviderVenueObservation>()
            )
            .Concat(
                source
                    .Recordings.Select(recording => recording.Recording.Venue)
                    .OfType<ProviderVenueObservation>()
            )
            .Concat(
                source
                    .Recordings.Select(recording => recording.SupportingRootAccount?.Venue)
                    .OfType<ProviderVenueObservation>()
            )
            .DistinctBy(ObservationKey)
            .ToArray();
        ValidateConsumedEvidence(accounts, source, revisions);
        var venues = accounts.Select(venue => ProjectVenue(venue, source)).ToArray();
        var unavailable =
            source.Decoded.Issues.Count > 0
            || source.Issues.Any(issue => issue.Code != "local_root_requires_directory_input");
        var outcome =
            venues.Length == 0
                ? unavailable
                    ? "unresolved"
                    : "not_applicable"
                : venues.Any(venue => venue.Outcome == "proposed")
                    ? "proposed"
                    : "unresolved";

        return ShadowCatalogResult.For(
            input,
            "venues",
            outcome,
            Version,
            new ShadowNativeVenueProposal(venues),
            new
            {
                source.Input.Uuid,
                source.Input.Resource,
                source.Input.Sha256,
                source.Input.CaptureVersion,
                source.Decoded.DecoderVersion,
                DecoderIssues = source.Decoded.Issues,
                ContextIssues = source.Issues,
                SupportingRoots = revisions
                    .Values.Where(revision => revision.Uuid != source.Input.Uuid)
                    .Select(revision => new
                    {
                        revision.Uuid,
                        revision.Resource,
                        revision.Sha256,
                        revision.CaptureVersion,
                    })
                    .ToArray(),
            },
            venues.Length == 0
                ? unavailable
                    ? "native_venue_observation_incomplete"
                    : "no_native_venue_observation"
                : "physical_identity_and_history_unaccepted",
            venues
                .SelectMany(venue => venue.BrowseKeys.Prepend(venue.ObservationKey))
                .Concat(
                    venues
                        .SelectMany(venue => venue.RecordingLinks)
                        .SelectMany(link =>
                            new[]
                            {
                                link.CatalogSourceUuid is { } sourceUuid
                                    ? "source:" + sourceUuid.ToString("D")
                                    : null,
                                link.CatalogShowUuid is { } showUuid
                                    ? "show:" + showUuid.ToString("D")
                                    : null,
                            }.OfType<string>()
                        )
                )
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        );
    }

    private ShadowNativeVenueObservation ProjectVenue(
        ProviderVenueObservation venue,
        ShadowProviderObservationSet source
    )
    {
        var names = venue
            .Names.Select(name => name.Value)
            .Where(name => ShadowVenueText.Key(name).Length > 0 && !ShadowVenueText.IsUnknown(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var recordingAccounts = source
            .Recordings.Where(recording =>
                recording.Recording.Venue is { } direct
                    && ObservationKey(direct) == ObservationKey(venue)
                || recording.SupportingRootAccount?.Venue is { } root
                    && ObservationKey(root) == ObservationKey(venue)
            )
            .ToArray();
        var localityAssertions = recordingAccounts
            .SelectMany(recording =>
                recording
                    .Recording.Text.Concat(recording.SupportingRootAccount?.Text ?? [])
                    .Where(text => text.Field == "coverage")
                    .Select(text => text.Text)
            )
            .Concat(venue.OriginalLocality == null ? [] : new[] { venue.OriginalLocality })
            .DistinctBy(assertion => assertion.Evidence)
            .ToArray();
        var multiple =
            names.Any(ShadowVenueText.MultiplePlaces)
            || localityAssertions.Any(assertion => ShadowVenueText.MultiplePlaces(assertion.Value))
            || localityAssertions
                .Select(assertion => ShadowVenueText.Key(assertion.Value))
                .Distinct()
                .Count() > 1;
        var locality = Locality(venue);
        var keys = BrowseKeys(venue, names, locality);
        var links = recordingAccounts
            .Select(recording => new ShadowNativeVenueRecordingLink(
                recording.Recording.Origin,
                recording.CatalogSourceUuid,
                recording.CatalogShowUuid,
                recording.CatalogBindingReason,
                recording.SupportingRootAccount?.LiteralDate ?? recording.Recording.LiteralDate
            ))
            .Distinct()
            .ToArray();
        var unknowns = new List<string>
        {
            "physical_identity_unaccepted",
            "name_validity_dates_unknown",
            "relocation_history_unknown",
            "room_parent_unknown",
            "provider_record_may_cover_multiple_premises",
        };
        if (venue.CoordinateKind != ObservedCoordinateKind.Venue)
        {
            unknowns.Add("coordinates_do_not_identify_premises");
        }
        if (multiple)
        {
            unknowns.Add("multiple_names_or_localities_require_interpretation");
        }

        return new(
            ObservationKey(venue),
            names.FirstOrDefault(),
            venue,
            locality,
            localityAssertions,
            names.Select(ShadowVenueText.Scope).Distinct().Count() == 1
                ? ShadowVenueText.Scope(names.FirstOrDefault())
                : "unknown",
            Coordinates(venue),
            keys,
            links,
            source
                .Decoded.Setlists.Where(setlist =>
                    setlist.Venue is { } account && ObservationKey(account) == ObservationKey(venue)
                )
                .Select(setlist => new ShadowNativeVenueSetlistLink(
                    setlist.Origin,
                    setlist.LiteralDate
                ))
                .ToArray(),
            names.Length == 0 || multiple ? "unresolved" : "proposed",
            unknowns
        );
    }

    private ShadowVenueLocality Locality(ProviderVenueObservation venue)
    {
        // Original locality prose is retained rather than guessed into city/state fields.
        // Structured country names remain in Venue.Country; CountryCode requires a literal code.
        var country = venue.Country?.Value.Trim();
        var countryCode =
            country is { Length: 2 } && country.All(char.IsAsciiLetter)
                ? country.ToUpperInvariant()
                : null;
        var components = new[]
        {
            country,
            venue.Region?.Value,
            venue.City?.Value,
            venue.OriginalLocality?.Value,
        }
            .Select(ShadowVenueText.Key)
            .Where(value => value.Length > 0)
            .ToArray();
        var locality = new ShadowVenueLocality(
            venue.OriginalLocality?.Value,
            venue.City?.Value,
            venue.Region?.Value,
            countryCode,
            venue.Address?.Value,
            venue.Address?.Evidence.JsonPointer,
            "retained_provider_fields",
            components.Length == 0
                ? null
                : ShadowCatalogIdentity.Key(
                    "native_locality_wording",
                    string.Join("\n", components)
                )
        );

        return geography == null
            ? locality
            : locality with
            {
                Geography = geography.Resolve(locality),
            };
    }

    private static IReadOnlyList<string> BrowseKeys(
        ProviderVenueObservation venue,
        IReadOnlyList<string> names,
        ShadowVenueLocality locality
    )
    {
        var keys = new List<string>();
        foreach (var name in names)
        {
            var nameKey = ShadowVenueText.Key(name);
            keys.Add(ShadowCatalogIdentity.Key("native_venue_name_hypothesis", nameKey));
            if (locality.BrowseKey != null)
            {
                keys.Add(
                    ShadowCatalogIdentity.Key(
                        "native_venue_name_locality_hypothesis",
                        nameKey + "\n" + locality.BrowseKey
                    )
                );
            }
        }
        foreach (var identity in venue.Identities)
        {
            keys.Add(
                ShadowCatalogIdentity.Key(
                    "native_venue_provider_reference",
                    JsonSerializer.Serialize(identity.Identity)
                )
            );
        }
        if (locality.BrowseKey != null)
        {
            keys.Add(locality.BrowseKey);
        }
        if (locality.Geography?.ProposedGeographicKey is { } geographicKey)
        {
            keys.Add(geographicKey);
        }
        return keys.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static ShadowVenueCoordinates? Coordinates(ProviderVenueObservation venue)
    {
        if (venue.Latitude == null && venue.Longitude == null)
        {
            return null;
        }
        var latitude = venue.Latitude?.Value;
        var longitude = venue.Longitude?.Value;
        return new(
            latitude,
            longitude,
            venue.CoordinateKind.ToString().ToLowerInvariant(),
            latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180
        );
    }

    private static string ObservationKey(ProviderVenueObservation venue) =>
        ShadowCatalogIdentity.Key(
            "native_venue_observation",
            JsonSerializer.Serialize(new { venue.Origin.Item, venue.Origin.Evidence })
        );

    private static void ValidateConsumedEvidence(
        IReadOnlyList<ProviderVenueObservation> venues,
        ShadowProviderObservationSet source,
        IReadOnlyDictionary<Guid, NormalizationInputRevision> revisions
    )
    {
        var references = source
            .Recordings.SelectMany(recording =>
                new NormalizedEvidenceReference?[]
                {
                    recording.Recording.Origin.Evidence,
                    recording.Recording.LiteralDate?.Evidence,
                    recording.SupportingRootAccount?.LiteralDate?.Evidence,
                }
            )
            .Concat(
                source.Decoded.Setlists.SelectMany(setlist =>
                    new NormalizedEvidenceReference?[]
                    {
                        setlist.Origin.Evidence,
                        setlist.LiteralDate?.Evidence,
                    }
                )
            )
            .OfType<NormalizedEvidenceReference>();
        var coverage = source.Recordings.SelectMany(recording =>
            recording
                .Recording.Text.Concat(recording.SupportingRootAccount?.Text ?? [])
                .Where(text => text.Field == "coverage")
                .Select(text => text.Text.Evidence)
        );
        ValidateEvidence(
            references.Concat(coverage).Concat(venues.SelectMany(VenueEvidence)),
            revisions
        );
    }

    private static IEnumerable<NormalizedEvidenceReference> VenueEvidence(
        ProviderVenueObservation venue
    ) =>
        venue
            .Names.Select(name => name.Evidence)
            .Concat(venue.Identities.Select(identity => identity.Evidence))
            .Concat(venue.Text.Select(text => text.Text.Evidence))
            .Concat(
                new NormalizedEvidenceReference?[]
                {
                    venue.Origin.Evidence,
                    venue.OriginalLocality?.Evidence,
                    venue.Country?.Evidence,
                    venue.Region?.Evidence,
                    venue.City?.Evidence,
                    venue.Address?.Evidence,
                    venue.Latitude?.Evidence,
                    venue.Longitude?.Evidence,
                }.OfType<NormalizedEvidenceReference>()
            );

    private static void ValidateEvidence(
        IEnumerable<NormalizedEvidenceReference> evidence,
        IReadOnlyDictionary<Guid, NormalizationInputRevision> revisions
    )
    {
        foreach (var group in evidence.GroupBy(reference => reference.InputRevisionUuid))
        {
            if (!revisions.TryGetValue(group.Key, out var revision))
            {
                throw new InvalidDataException(
                    "Venue citation does not belong to an authenticated native input."
                );
            }
            using var document = NormalizationRetainedDocument.Open(revision);
            foreach (var reference in group)
            {
                document.ValidateEvidence(reference);
            }
        }
    }
}

public sealed record ShadowNativeVenueProposal(IReadOnlyList<ShadowNativeVenueObservation> Venues);

/// <summary>The original venue account stays cited, including unparsed historical-name text.</summary>
public sealed record ShadowNativeVenueObservation(
    string ObservationKey,
    string? DisplayName,
    ProviderVenueObservation Venue,
    ShadowVenueLocality Locality,
    IReadOnlyList<ObservedValue<string>> LocalityAssertions,
    string Scope,
    ShadowVenueCoordinates? Coordinates,
    IReadOnlyList<string> BrowseKeys,
    IReadOnlyList<ShadowNativeVenueRecordingLink> RecordingLinks,
    IReadOnlyList<ShadowNativeVenueSetlistLink> SetlistLinks,
    string Outcome,
    IReadOnlyList<string> ProtectedUnknowns,
    bool AcceptedIdentity = false
);

/// <summary>Catalog links exist only when the retained provider context authenticates that binding.</summary>
public sealed record ShadowNativeVenueRecordingLink(
    ProviderObservationOrigin Recording,
    Guid? CatalogSourceUuid,
    Guid? CatalogShowUuid,
    string? CatalogBindingReason,
    ObservedValue<string>? LiteralDate
);

public sealed record ShadowNativeVenueSetlistLink(
    ProviderObservationOrigin Setlist,
    ObservedValue<string>? LiteralDate
);
