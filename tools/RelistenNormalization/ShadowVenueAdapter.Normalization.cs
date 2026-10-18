using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Relisten.Normalization.Batch;

public sealed partial class ShadowVenueAdapter
{
    private const int MaximumHypothesisExamples = 8;
    private readonly Dictionary<Guid, ShadowVenueSourceContext> sourceContexts = [];
    private readonly HashSet<Guid> conflictingSourceContexts = [];
    private Dictionary<Guid, ShadowVenueNormalization>? normalization;
    private readonly Dictionary<string, Guid[]> normalizedMembers = new(StringComparer.Ordinal);

    // Sources are observed only as compact captured associations. They produce no venue
    // outcomes, and repeated uploads/observations are not independent identity evidence.
    private bool ObserveSourceContext(JsonElement record)
    {
        if (!record.TryGetProperty("kind", out var kind) || kind.GetString() != "source_sample")
        {
            return false;
        }
        if (!record.TryGetProperty("row", out var row) || row.ValueKind != JsonValueKind.Object)
        {
            return true;
        }
        var uuid = GuidValue(row, "uuid");
        var artist = Number(row, "artist_id");
        if (uuid == null || artist == null || conflictingSourceContexts.Contains(uuid.Value))
        {
            return true;
        }
        var show =
            record.TryGetProperty("show_context", out var context)
            && context.ValueKind == JsonValueKind.Object
                ? context
                : default;
        if (
            show.ValueKind == JsonValueKind.Object
            && (
                Number(row, "show_id") == null
                || Number(row, "show_id") != Number(show, "id")
                || Number(show, "artist_id") is { } showArtist && showArtist != artist
            )
        )
        {
            DiscardSourceContext(uuid.Value);
            return true;
        }
        var sourceDate = ShadowVenueText.Text(row, "display_date");
        var showDate =
            show.ValueKind == JsonValueKind.Object
                ? ShadowVenueText.Text(show, "display_date") ?? ShadowVenueText.Text(show, "date")
                : null;
        if (Day(sourceDate) is { } first && Day(showDate) is { } second && first != second)
        {
            DiscardSourceContext(uuid.Value);
            return true;
        }
        var observed = new ShadowVenueSourceContext(
            uuid.Value,
            artist.Value,
            Number(row, "venue_id"),
            show.ValueKind == JsonValueKind.Object ? GuidValue(show, "uuid") : null,
            show.ValueKind == JsonValueKind.Object ? Number(show, "venue_id") : null,
            sourceDate,
            showDate
        );
        if (sourceContexts.TryGetValue(uuid.Value, out var previous) && previous != observed)
        {
            sourceContexts.Remove(uuid.Value);
            conflictingSourceContexts.Add(uuid.Value);
        }
        else
        {
            sourceContexts[uuid.Value] = observed;
        }
        normalization = null;
        return true;
    }

    private void DiscardSourceContext(Guid uuid)
    {
        sourceContexts.Remove(uuid);
        conflictingSourceContexts.Add(uuid);
        normalization = null;
    }

    private ShadowVenueNormalization Normalize(ShadowVenueObservation venue)
    {
        normalization ??= BuildNormalization();
        return
            !conflictingRows.Contains(venue.Uuid)
            && normalization.TryGetValue(venue.Uuid, out var result)
            ? result
            : UnresolvedNormalization(venue, []);
    }

    private string NormalizedPlaceKey(ShadowVenueObservation venue) =>
        Normalize(venue).Hypothesis?.Key ?? Place(venue).Key;

    // Build once after the runner's full observation pass. Name/locality buckets avoid a
    // pairwise catalog scan; initialisms select direct full-name anchors, never graph closure.
    private Dictionary<Guid, ShadowVenueNormalization> BuildNormalization()
    {
        normalizedMembers.Clear();
        var contextsByVenue = sourceContexts
            .Values.SelectMany(value =>
                new[] { value.SourceVenueId, value.ShowVenueId }
                    .Distinct()
                    .Where(id => id.HasValue)
                    .Select(id => (Id: id!.Value, Context: value))
            )
            .ToLookup(value => value.Id, value => value.Context);
        var rowsByCatalogId = venues
            .Values.Where(value => value.CatalogId.HasValue)
            .ToLookup(value => value.CatalogId!.Value);
        var eligible = venues
            .Values.Where(value =>
                CanNormalize(value)
                && value.CatalogId is { } id
                && rowsByCatalogId[id].Count() == 1
                && contextsByVenue[id].Any(context => context.ArtistId == value.ArtistId)
            )
            .ToArray();
        var conflicts = eligible
            .Where(venue => HasContextConflict(venue, contextsByVenue, rowsByCatalogId))
            .Select(venue => venue.Uuid)
            .ToHashSet();
        var groups = CreateNameGroups(eligible.Where(venue => !conflicts.Contains(venue.Uuid)));
        var choices = SelectHypotheses(eligible, groups, contextsByVenue, conflicts);
        AttachAliases(eligible, choices);
        var summaries = groups.ToDictionary(
            group => group,
            group => Summarize(group, contextsByVenue)
        );

        foreach (var group in groups)
        {
            normalizedMembers.Add(
                summaries[group].Key,
                group.Members.Select(value => value.Uuid).Order().ToArray()
            );
        }
        return eligible.ToDictionary(
            venue => venue.Uuid,
            venue => ProjectNormalization(venue, choices[venue.Uuid], summaries)
        );
    }

    private static Dictionary<Guid, HypothesisChoice> SelectHypotheses(
        ShadowVenueObservation[] eligible,
        List<HypothesisGroup> groups,
        ILookup<long, ShadowVenueSourceContext> contexts,
        HashSet<Guid> conflicts
    )
    {
        var byName = groups.ToLookup(group => Family(group.Anchor));
        var byInitialism = groups
            .Where(group =>
                Acronym(group.Anchor.Name) == null && Initials(group.Anchor.NameKey) != null
            )
            .ToLookup(group =>
                (group.GeographicKey, group.Anchor.Scope, Initials(group.Anchor.NameKey)!)
            );
        var choices = new Dictionary<Guid, HypothesisChoice>();
        foreach (var venue in eligible.OrderBy(value => value.Uuid))
        {
            // Case variants of an observed initialism share its retrieval spelling. A full
            // name is never synthesized from a short label when no corpus anchor exists.
            var acronym =
                Acronym(venue.Name)
                ?? byName[Family(venue)]
                    .SelectMany(group => group.Members)
                    .Select(member => Acronym(member.Name))
                    .FirstOrDefault(value => value != null);
            var expanded =
                acronym == null
                    ? []
                    : byInitialism[
                        (venue.Locality.Geography!.ProposedGeographicKey!, venue.Scope, acronym)
                    ]
                        .ToArray();
            var pool = (expanded.Length == 0 ? byName[Family(venue)] : expanded)
                .Where(group => CompatibleAddress(venue, group.Address))
                .ToArray();
            if (conflicts.Contains(venue.Uuid))
            {
                choices.Add(
                    venue.Uuid,
                    new(null, "conflicting_source_show_venue_accounts", pool, true)
                );
                continue;
            }
            var direct = DirectContextTargets(venue, contexts);
            var supported = pool.Where(group =>
                    group.Members.Any(member => member.CatalogId is { } id && direct.Contains(id))
                )
                .ToArray();
            var winner =
                pool.Length == 1 ? pool[0]
                : supported.Length == 1 ? supported[0]
                : null;
            var basis =
                winner == null ? "no_unique_compatible_hypothesis"
                : expanded.Length == 0 ? "corpus_name_resolved_locality"
                : supported.Contains(winner) ? "initialism_with_source_show_context"
                : "unique_corpus_initialism_resolved_locality";
            choices.Add(venue.Uuid, new(winner, basis, pool));
        }
        return choices;
    }

    private static void AttachAliases(
        ShadowVenueObservation[] eligible,
        IReadOnlyDictionary<Guid, HypothesisChoice> choices
    )
    {
        // All choices use the original full-name seed members. A selected short name
        // cannot bridge anchors or become evidence for a transitive alias.
        foreach (var venue in eligible)
        {
            if (
                choices[venue.Uuid].Group is { } group
                && group.Members.All(member => member.Uuid != venue.Uuid)
            )
            {
                group.Members.Add(venue);
            }
        }
    }

    private static ShadowVenueNormalization ProjectNormalization(
        ShadowVenueObservation venue,
        HypothesisChoice choice,
        IReadOnlyDictionary<HypothesisGroup, ShadowVenueHypothesis> summaries
    )
    {
        var summary = choice.Group == null ? null : summaries[choice.Group];
        return new(
            summary == null
                ? choice.ContextConflict || choice.Candidates.Length > 1
                    ? "ambiguous"
                    : "unresolved"
                : "proposed",
            choice.Basis,
            venue.Name,
            DisplayLocation(venue),
            venue.Locality.Geography?.ProposedGeographicKey,
            summary,
            choice.Candidates.Length,
            choice
                .Candidates.Select(group => summaries[group].Key)
                .Order(StringComparer.Ordinal)
                .Take(MaximumHypothesisExamples)
                .ToArray(),
            choice.Candidates.Length > MaximumHypothesisExamples,
            choice.ContextConflict ? ["source_show_venue_accounts_disagree"]
                : summary == null ? ["physical_identity_not_resolved"]
                :
                [
                    "physical_identity_hypothesis_not_accepted",
                    "premises_history_not_verified",
                    "source_show_associations_are_not_independent_identity_confirmation",
                ]
        );
    }

    private static bool HasContextConflict(
        ShadowVenueObservation venue,
        ILookup<long, ShadowVenueSourceContext> contexts,
        ILookup<long, ShadowVenueObservation> rows
    )
    {
        foreach (var id in DirectContextTargets(venue, contexts).Where(id => id != venue.CatalogId))
        {
            var targets = rows[id].ToArray();
            if (targets.Length != 1)
            {
                continue;
            }
            var target = targets[0];
            if (
                target.Scope is "room" or "stage"
                || venue.Scope != target.Scope
                || target.Locality.Geography?.ProposedGeographicKey is { } geography
                    && geography != venue.Locality.Geography?.ProposedGeographicKey
                || Address(venue) is { } address
                    && Address(target) is { } otherAddress
                    && address != otherAddress
                || !RelatedNames(venue, target)
            )
            {
                return true;
            }
        }
        return false;
    }

    private static bool RelatedNames(ShadowVenueObservation first, ShadowVenueObservation second) =>
        FullName(first.NameKey) == FullName(second.NameKey)
        || Acronym(first.Name) is { } left && left == Initials(second.NameKey)
        || Acronym(second.Name) is { } right && right == Initials(first.NameKey);

    private bool CanNormalize(ShadowVenueObservation venue) =>
        venue.CanProposePhysicalGroup
        && !conflictingRows.Contains(venue.Uuid)
        && venue.Locality.Geography?.ProposedGeographicKey != null
        && venue.Scope is "place" or "site" or "building"
        && venue.PastNames.Count == 0;

    private static List<HypothesisGroup> CreateNameGroups(
        IEnumerable<ShadowVenueObservation> eligible
    )
    {
        var result = new List<HypothesisGroup>();
        foreach (var family in eligible.GroupBy(Family))
        {
            var addresses = family
                .Select(Address)
                .Where(value => value != null)
                .Distinct()
                .ToArray();
            // Several premises under one name are competing anchors. Unknown-address rows
            // remain unassigned unless their own original source/show context selects one.
            var partitions =
                addresses.Length <= 1
                    ? new[] { family.ToArray() }
                    : addresses.Select(address =>
                        family.Where(value => Address(value) == address).ToArray()
                    );
            foreach (var partition in partitions)
            {
                result.Add(
                    new(
                        partition,
                        addresses.Length <= 1 ? addresses.SingleOrDefault() : Address(partition[0])
                    )
                );
            }
        }
        return result;
    }

    private static HashSet<long> DirectContextTargets(
        ShadowVenueObservation venue,
        ILookup<long, ShadowVenueSourceContext> contexts
    )
    {
        var targets = new HashSet<long>();
        if (venue.CatalogId is not { } id)
        {
            return targets;
        }
        foreach (
            var context in contexts[id]
                .Where(context =>
                    context.ArtistId == venue.ArtistId
                    && context.ShowUuid != null
                    && Day(context.SourceDate) is { } day
                    && Day(context.ShowDate) == day
                )
        )
        {
            if (context.SourceVenueId is { } source && context.ShowVenueId is { } show)
            {
                targets.Add(source == id ? show : source);
            }
        }
        return targets;
    }

    private static ShadowVenueHypothesis Summarize(
        HypothesisGroup group,
        ILookup<long, ShadowVenueSourceContext> contexts
    )
    {
        var members = group.Members.OrderBy(value => value.Uuid).ToArray();
        var names = members
            .Select(value => value.Name!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var sources = members
            .SelectMany(value =>
                contexts[value.CatalogId!.Value]
                    .Where(context => context.ArtistId == value.ArtistId)
            )
            .DistinctBy(context => context.SourceUuid)
            .OrderBy(context => context.SourceUuid)
            .ToArray();
        var days = sources
            .Select(source => Day(source.SourceDate) ?? Day(source.ShowDate))
            .ToArray();
        var knownDays = days.Where(day => day.HasValue).Select(day => day!.Value).ToArray();
        return new(
            ShadowCatalogIdentity.Key(
                "venue_hypothesis",
                "retained_anchor\n" + group.Anchor.Uuid.ToString("D")
            ),
            group.Anchor.Uuid,
            group.Anchor.Name!,
            group.Anchor.Scope,
            group.GeographicKey,
            DisplayLocation(group.Anchor)!,
            "corpus_alias_locality_context_hypothesis",
            members.Length,
            members.Select(value => value.ArtistId).Distinct().Count(),
            members.Select(value => value.Uuid).Take(MaximumHypothesisExamples).ToArray(),
            members.Length > MaximumHypothesisExamples,
            names.Length,
            names.Take(MaximumHypothesisExamples).ToArray(),
            names.Length > MaximumHypothesisExamples,
            sources.Length,
            sources.Take(MaximumHypothesisExamples).ToArray(),
            sources.Length > MaximumHypothesisExamples,
            new(
                knownDays.Length == 0 ? null : knownDays.Min(),
                knownDays.Length == 0 ? null : knownDays.Max(),
                days.Count(day => day == null)
            )
        );
    }

    private static ShadowVenueNormalization UnresolvedNormalization(
        ShadowVenueObservation venue,
        IReadOnlyList<string> candidates
    ) =>
        new(
            "unresolved",
            "retained_record_without_corpus_hypothesis",
            venue.Name,
            DisplayLocation(venue),
            venue.Locality.Geography?.ProposedGeographicKey,
            null,
            candidates.Count,
            candidates.Take(MaximumHypothesisExamples).ToArray(),
            candidates.Count > MaximumHypothesisExamples,
            venue.Scope is "room" or "stage" ? ["room_or_stage_parent_requires_evidence"]
                : venue.PastNames.Count > 0
                    ? ["historical_name_and_premises_correspondence_requires_evidence"]
                : ["complete_source_context_and_resolved_geography_required"]
        );

    private static (string Geography, string Scope, string Name) Family(
        ShadowVenueObservation venue
    ) => (venue.Locality.Geography!.ProposedGeographicKey!, venue.Scope, FullName(venue.NameKey));

    private static string FullName(string key) =>
        key.StartsWith("the ", StringComparison.Ordinal) ? key[4..] : key;

    private static string? Acronym(string? name)
    {
        var text = name?.Trim();
        if (
            text == null
            || !Regex.IsMatch(
                text,
                @"^(?:[\p{Lu}\p{N}][\s.]*){2,8}$",
                RegexOptions.CultureInvariant
            )
        )
        {
            return null;
        }
        return ShadowVenueText.Key(text).Replace(" ", "", StringComparison.Ordinal);
    }

    private static string? Initials(string nameKey)
    {
        var words = FullName(nameKey).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length is >= 2 and <= 8
            ? string.Concat(words.Select(word => StringInfo.GetNextTextElement(word)))
            : null;
    }

    private static string? Address(ShadowVenueObservation venue) =>
        venue.Locality.Address == null ? null : ShadowVenueText.Key(venue.Locality.Address);

    private static bool CompatibleAddress(ShadowVenueObservation venue, string? address) =>
        Address(venue) is not { } actual || address == null || actual == address;

    private static string? DisplayLocation(ShadowVenueObservation venue) =>
        venue.Locality.Geography?.Place is { } place
            ? place.Name + ", " + place.Admin1Name + ", " + place.CountryCode
            : venue.Locality.OriginalWording;

    private static long? Number(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : null;

    private static Guid? GuidValue(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && Guid.TryParse(value.GetString(), out var uuid)
        && uuid != Guid.Empty
            ? uuid
            : null;

    private static DateOnly? Day(string? value) =>
        DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var day
        )
            ? day
            : null;

    private sealed record HypothesisChoice(
        HypothesisGroup? Group,
        string Basis,
        HypothesisGroup[] Candidates,
        bool ContextConflict = false
    );

    private sealed class HypothesisGroup
    {
        internal HypothesisGroup(ShadowVenueObservation[] members, string? address)
        {
            Members = members.ToList();
            Anchor = members.OrderBy(value => value.Uuid).First();
            Address = address;
        }

        internal ShadowVenueObservation Anchor { get; }
        internal List<ShadowVenueObservation> Members { get; }
        internal string? Address { get; }
        internal string GeographicKey => Anchor.Locality.Geography!.ProposedGeographicKey!;
    }
}
