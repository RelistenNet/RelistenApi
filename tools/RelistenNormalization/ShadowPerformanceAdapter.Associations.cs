using System.Text.Json;
using System.Text.RegularExpressions;
using Relisten.Api.Models;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

public sealed partial class ShadowPerformanceAdapter
{
    private readonly Dictionary<
        (long Artist, ShadowPerformanceDate Date),
        HashSet<ShowHome>
    > datedShows = [];
    private readonly Dictionary<(long Artist, int Year), HashSet<ShowHome>> showsByYear = [];
    private readonly Dictionary<string, HashSet<ShowHome>> showsByProviderKey = new(
        StringComparer.Ordinal
    );
    private readonly Dictionary<Guid, long> showOwners = [];
    private readonly HashSet<Guid> conflictingShowUuids = [];

    private static readonly Regex Billing = new(
        @"\b(?<type>early|late|afternoon)\s+(?:shows?|performances?)\b|\b(?<type>matinee|matinée)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );
    private static readonly Regex UncertainBilling = new(
        @"\b(?:possibly|probably|maybe|unknown|uncertain|not)\b|\?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    private static readonly Regex CombinedBilling = new(
        @"\b(?:early\s*(?:(?:show|performance)\s*)?(?:and|&|/|\+)\s*late|late\s*(?:(?:show|performance)\s*)?(?:and|&|/|\+)\s*early)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    private static readonly Regex DirectBilling = new(
        @"^\s*(?:(?:early|late|afternoon)\s+(?:show|performance)|(?:matinee|matinée)(?:\s+(?:show|performance))?)(?:\s*\(\s*\d{1,2}(?::\d{2})?\s*(?:[ap]\.?m\.?)?\s*\))?\s*[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    private void ObserveShow(JsonElement row)
    {
        var show = ShowHome.Read(row);
        if (shows.TryGetValue(show.Id, out var existing))
        {
            if (existing == show)
            {
                return;
            }
            conflictingShows.Add(show.Id);
        }
        shows.TryAdd(show.Id, show);
        if (showOwners.TryGetValue(show.Uuid, out var owner) && owner != show.Id)
        {
            conflictingShowUuids.Add(show.Uuid);
        }
        showOwners.TryAdd(show.Uuid, show.Id);

        var date = ShadowPerformanceDate.Parse(show.LiteralDate);
        if (date != null)
        {
            AddCandidate(datedShows, (show.ArtistId, date), show);
            AddCandidate(showsByYear, (show.ArtistId, date.Year), show);
        }
        if (!string.IsNullOrWhiteSpace(show.PerformanceKey))
        {
            AddCandidate(showsByProviderKey, show.PerformanceKey, show);
        }
    }

    private static void AddCandidate<TKey>(
        Dictionary<TKey, HashSet<ShowHome>> index,
        TKey key,
        ShowHome show
    )
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out var candidates))
        {
            candidates = [];
            index.Add(key, candidates);
        }
        candidates.Add(show);
    }

    private ShadowPerformanceAssociation AssociatePerformance(
        ShadowCatalogInput input,
        string? literalDate,
        ProviderObservationOrigin? origin = null,
        ObservedValue<string>? observedDate = null,
        ProviderVenueObservation? venue = null,
        IReadOnlyList<ProviderTextObservation>? text = null
    )
    {
        var discriminators = new List<ShadowPerformanceDiscriminator>();
        ShadowPerformanceAssociation Result(
            string basis,
            string? reason,
            IEnumerable<ShowHome> candidates,
            ShowHome? selected = null
        ) =>
            new(
                selected?.Uuid,
                selected == null ? null : "show:" + selected.Uuid.ToString("D"),
                basis,
                reason,
                literalDate,
                origin,
                observedDate,
                discriminators,
                candidates
                    .OrderBy(value => value.Id)
                    .ThenBy(value => value.Uuid)
                    .Select(value => new ShadowPerformanceCandidate(
                        value.Uuid,
                        value.Id,
                        value.LiteralDate,
                        value.PerformanceType,
                        value.PerformanceLabel,
                        value.PerformanceKey,
                        value.VenueId,
                        HasConflict(value)
                    ))
                    .ToArray()
            );

        if (
            input.Artist.Uuid == Guid.Empty
            || input.Artist.Id <= 0
            || Number(input.Row, "artist_id") is { } artistId && artistId != input.Artist.Id
            || Uuid(input.Row, "artist_uuid") is { } artistUuid && artistUuid != input.Artist.Uuid
        )
        {
            return Result("artist_scope", "artist_scope_conflict", []);
        }

        // A retained engagement key outranks date and presentation corrections. It can
        // select only captured same-artist shows; a conflicting key must not fall through
        // to a date guess. No wrapper-only review/binding protocol is invented here.
        if (origin != null)
        {
            var identity = origin.Item;
            var key = new PerformanceEngagementIdentity.Provider(
                identity.Provider,
                identity.ResourceType,
                identity.ProviderKey
            ).PerformanceKey;
            if (showsByProviderKey.TryGetValue(key, out var direct))
            {
                var scoped = direct.Where(value => value.ArtistId == input.Artist.Id).ToArray();
                return scoped.Length == 1 && !HasConflict(scoped[0])
                    ? Result("existing_provider_performance_key", null, scoped, scoped[0])
                    : Result(
                        "existing_provider_performance_key",
                        scoped.Length == 0
                            ? "provider_performance_artist_conflict"
                            : "conflicting_provider_performance_key",
                        direct
                    );
            }
        }

        var date = ShadowPerformanceDate.Parse(literalDate, origin?.Item.Provider == "setlist.fm");
        if (date == null)
        {
            return Result("artist_date", "missing_or_unsupported_performance_date", []);
        }

        // Prefer exact catalog dates when the provider supplies an exact date. Partial
        // catalog dates are considered only without an exact match. A partial provider
        // date considers every compatible captured date in that artist/year.
        var exact = date.IsExact ? datedShows.GetValueOrDefault((input.Artist.Id, date)) : null;
        var basis = exact is { Count: > 0 }
            ? "unique_exact_artist_date"
            : "unique_compatible_artist_date";
        var candidates = exact is { Count: > 0 }
            ? exact.ToArray()
            : (showsByYear.GetValueOrDefault((input.Artist.Id, date.Year)) ?? [])
                .Where(value =>
                    ShadowPerformanceDate.Parse(value.LiteralDate) is { } candidateDate
                    && date.CompatibleWith(candidateDate)
                )
                .ToArray();
        if (candidates.Length == 0)
        {
            return Result(basis, "no_compatible_catalog_performance", []);
        }
        if (candidates.Any(HasConflict))
        {
            return Result(basis, "conflicting_catalog_performance_context", candidates);
        }

        var eligible = candidates;
        var labels = ReadBilling(text ?? [], discriminators);
        if (labels.Invalid)
        {
            return Result(basis, "ambiguous_provider_performance_label", candidates);
        }
        if (labels.Type != null)
        {
            // Unknown presentation cannot exclude a candidate. Set names are never read.
            eligible = eligible
                .Where(value => CatalogBilling(value) is not { } known || known == labels.Type)
                .ToArray();
            basis += "_with_explicit_billing";
        }
        if (venue is { Names.Count: > 0 })
        {
            discriminators.AddRange(
                venue.Names.Select(value => new ShadowPerformanceDiscriminator(
                    "venue_name",
                    value.Value,
                    value.Evidence
                ))
            );
            if (venue.City is { } city)
            {
                discriminators.Add(new("venue_city", city.Value, city.Evidence));
            }
            // Literal names retrieve and describe candidates. Different names may be
            // aliases, so they cannot exclude another same-day performance until an
            // accepted venue correspondence is available to this adapter.
        }
        var selected = eligible.Length == 1 ? eligible[0] : null;
        var positiveBilling =
            selected != null && labels.Type != null && CatalogBilling(selected) == labels.Type;
        // With multiple original candidates, eliminating a known contradiction is not
        // enough: the remaining unknown show still needs positive billing support.
        return selected != null && (candidates.Length == 1 || positiveBilling)
            ? Result(basis, null, candidates, selected)
            : Result(
                basis,
                eligible.Length == 0
                    ? "provider_performance_metadata_conflict"
                    : "ambiguous_catalog_performance",
                candidates
            );
    }

    private bool HasConflict(ShowHome show) =>
        conflictingShows.Contains(show.Id) || conflictingShowUuids.Contains(show.Uuid);

    private static (string? Type, bool Invalid) ReadBilling(
        IReadOnlyList<ProviderTextObservation> text,
        List<ShadowPerformanceDiscriminator> evidence
    )
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        var uncertain = false;
        foreach (
            var observation in text.Where(value =>
                value.Field
                    is "show_facts"
                        or "title"
                        or "performance_label"
                        or "show_title"
                        or "info"
            )
        )
        {
            var value = observation.PlainText ?? observation.Text.Value;
            var matches = Billing.Matches(value);
            if (matches.Count == 0)
            {
                continue;
            }
            evidence.Add(new("performance_billing", value, observation.Text.Evidence));
            uncertain |=
                UncertainBilling.IsMatch(value)
                || CombinedBilling.IsMatch(value)
                || !DirectBilling.IsMatch(value);
            foreach (Match match in matches)
            {
                types.Add(NormalizeBilling(match.Groups["type"].Value));
            }
        }
        return (types.Count == 1 ? types.Single() : null, uncertain || types.Count > 1);
    }

    private static string? CatalogBilling(ShowHome show)
    {
        if (show.PerformanceType is "early" or "late" or "afternoon")
        {
            return show.PerformanceType;
        }
        var label = show.PerformanceLabel;
        if (
            label == null
            || UncertainBilling.IsMatch(label)
            || CombinedBilling.IsMatch(label)
            || !DirectBilling.IsMatch(label)
        )
        {
            return null;
        }
        var matches = Billing
            .Matches(label)
            .Select(match => NormalizeBilling(match.Groups["type"].Value))
            .Distinct()
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string NormalizeBilling(string value) =>
        value.ToLowerInvariant() switch
        {
            "matinee" or "matinée" => "afternoon",
            var type => type,
        };
}
