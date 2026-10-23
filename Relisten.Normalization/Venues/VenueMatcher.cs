using System;
using System.Collections.Generic;
using System.Linq;
using Relisten.Normalization.Titles;

namespace Relisten.Normalization.Venues;

/// What a deterministic pass decided: venues to create, their names and ids, and each mapped
/// legacy row. Left are the families of rows for the LLM; Unplaced rows have no usable name.
public sealed record VenuePlan(
    IReadOnlyList<VenueRecord> NewVenues,
    IReadOnlyList<VenueNameRecord> Names,
    IReadOnlyList<VenueExternalIdRecord> ExternalIds,
    IReadOnlyList<VenueEvidenceRecord> Evidence,
    IReadOnlyList<IReadOnlyList<LegacyVenue>> Left,
    IReadOnlyList<LegacyVenue> Unplaced
);

/// Deterministic venue matching (string-theory docs/matching.md T3), name first. A location
/// is free text full of typos, seats and notes, so it never decides identity on its own: it
/// only splits one name into several places.
/// 1. A row joins an existing venue by external id; by its name in its town when the town is
///    trusted; by a name only one venue has anywhere when the town is weak or missing; or as
///    one venue's initials in its town ("MSG").
/// 2. The rest form families: rows sharing an external id, and names in one trusted town that
///    could be one place (initials, one name containing the other, a typo apart).
/// 3. A family tied by one external id is one venue. A family that is one name in at most one
///    town per state is created here, one venue per state; its weak-town rows join it when it
///    is one venue overall. Every other family
///    (two names that may be one place, one name in several towns of a state: a chain or a
///    suburb) goes to the LLM whole.
public static class VenueMatcher
{
    public const string Author = "venue-matcher@2.0.0";
    public const float Exact = 0.95f;
    public const float Likely = 0.9f;

    public static VenuePlan Match(VenueState state)
    {
        var plan = new PlanBuilder(state);
        var open = PlaceOnKnown(state, plan);
        foreach (var family in Families(open))
        {
            if (!CreateIfUnambiguous(family, plan))
            {
                plan.Left.Add(family);
            }
        }

        return plan.Build();
    }

    /// Step 1: rows an existing venue already explains. Returns the rest with a name.
    private static List<LegacyVenue> PlaceOnKnown(VenueState state, PlanBuilder plan)
    {
        var open = new List<LegacyVenue>();
        foreach (var row in state.Unmapped)
        {
            if (row.ExternalId is { } id && state.ExternalIds.TryGetValue(id, out var byId))
            {
                plan.Place(row, byId, Exact);
                continue;
            }

            if (row.Key == null)
            {
                plan.Unplaced.Add(row);
                continue;
            }

            var named = state.Names[row.Key].Distinct().Where(state.Venues.ContainsKey).ToList();
            if (row.Locality is { Trusted: true } here)
            {
                var inTown = named.Where(v => state.Venues[v].LocalityKey == here.Key).ToList();
                if (inTown.Count == 1)
                {
                    plan.Place(row, inTown[0], Exact);
                    continue;
                }

                var byInitials =
                    inTown.Count == 0
                        ? state
                            .ByLocality[here.Key]
                            .Where(v =>
                                plan.NamesOf(v.Uuid)
                                    .Any(n => VenueNormalizer.IsInitialsOf(row.Key, n))
                            )
                            .Select(v => v.Uuid)
                            .ToList()
                        : [];
                if (byInitials.Count == 1)
                {
                    plan.Place(row, byInitials[0], Likely);
                    continue;
                }
            }
            else if (named.Count == 1)
            {
                // A weak or missing town: only a name one venue has anywhere places the row.
                plan.Place(row, named[0], Likely);
                continue;
            }

            open.Add(row);
        }

        return open;
    }

    /// Step 2: rows that could be one place, together. Names are joined by a shared external
    /// id, or by looking alike within one trusted town.
    private static List<IReadOnlyList<LegacyVenue>> Families(List<LegacyVenue> open)
    {
        var parent = open.Select(r => r.Key!).Distinct().ToDictionary(k => k, k => k);
        string Find(string x)
        {
            while (parent[x] != x)
            {
                x = parent[x] = parent[parent[x]];
            }

            return x;
        }

        void Union(string a, string b) => parent[Find(a)] = Find(b);

        foreach (var group in open.Where(r => r.ExternalId != null).GroupBy(r => r.ExternalId))
        {
            var keys = group.Select(r => r.Key!).Distinct().ToList();
            foreach (var key in keys.Skip(1))
            {
                Union(keys[0], key);
            }
        }

        foreach (
            var town in open.Where(r => r.Locality is { Trusted: true })
                .GroupBy(r => r.Locality!.Key)
        )
        {
            var keys = town.Select(r => r.Key!).Distinct().ToList();
            for (var i = 0; i < keys.Count; i++)
            {
                for (var j = i + 1; j < keys.Count; j++)
                {
                    if (Alike(keys[i], keys[j]))
                    {
                        Union(keys[i], keys[j]);
                    }
                }
            }
        }

        return open.GroupBy(r => Find(r.Key!))
            .Select(g => (IReadOnlyList<LegacyVenue>)g.ToList())
            .ToList();
    }

    /// Two names that could be one place: initials ("MSG"), one inside the other with at least
    /// two words ("Tweeter Center" / "Tweeter Center at the Waterfront"), or one word a typo
    /// apart.
    internal static bool Alike(string a, string b)
    {
        if (VenueNormalizer.IsInitialsOf(a, b) || VenueNormalizer.IsInitialsOf(b, a))
        {
            return true;
        }

        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        if (shorter.Contains(' ') && $" {longer} ".Contains($" {shorter} "))
        {
            return true;
        }

        // A typo sits inside a long word ("hammerstien"), not in a short one that changes the
        // place ("east" / "west").
        var (wa, wb) = (a.Split(' '), b.Split(' '));
        if (wa.Length != wb.Length)
        {
            return false;
        }

        var differing = wa.Zip(wb).Where(p => p.First != p.Second).ToList();
        return differing.Count == 1
            && differing[0].First.Length >= 5
            && differing[0].Second.Length >= 5
            && TitleGrouper.BoundedLevenshtein(differing[0].First, differing[0].Second, 2) <= 2;
    }

    /// Step 3: one name in at most one trusted town per state is one venue per state. A family
    /// with several names, or a name in several towns of a state, is the LLM's.
    private static bool CreateIfUnambiguous(IReadOnlyList<LegacyVenue> family, PlanBuilder plan)
    {
        // One external id across the family: one place, however its rows spell it.
        var ids = family.Select(r => r.ExternalId).Where(i => i != null).Distinct().ToList();
        if (
            ids.Count == 1
            && family.All(r => r.ExternalId != null || r.Locality is not { Trusted: true })
        )
        {
            var tied = family.Where(r => r.ExternalId != null).ToList();
            if (tied.Any(r => r.Locality is { Trusted: true }))
            {
                var venue = plan.Create(tied.Where(r => r.Locality is { Trusted: true }).ToList());
                foreach (
                    var row in family
                        .Except(tied)
                        .Concat(tied.Where(r => r.Locality is not { Trusted: true }))
                )
                {
                    plan.Place(row, venue, Likely);
                }

                return true;
            }
        }

        if (family.Select(r => r.Key).Distinct().Count() > 1)
        {
            return false;
        }

        var trusted = family.Where(r => r.Locality is { Trusted: true }).ToList();
        var byRegion = trusted.GroupBy(r => r.Locality!.RegionKey!).ToList();
        if (
            byRegion.Count == 0
            || byRegion.Any(g => g.Select(r => r.Locality!.Key).Distinct().Count() > 1)
        )
        {
            return false;
        }

        // A name already known in one of those towns belongs to that venue, not a new one.
        if (byRegion.Any(g => plan.KnownIn(g.First().Locality!.Key, family[0].Key!)))
        {
            return false;
        }

        var weak = family.Where(r => r.Locality is not { Trusted: true }).ToList();
        if (weak.Count > 0 && byRegion.Count > 1)
        {
            return false;
        }

        foreach (var region in byRegion)
        {
            var venue = plan.Create(region.ToList());
            foreach (var row in weak)
            {
                plan.Place(row, venue, Likely);
            }
        }

        return true;
    }

    private sealed class PlanBuilder(VenueState state)
    {
        private readonly List<VenueRecord> newVenues = [];
        private readonly List<VenueNameRecord> names = [];
        private readonly List<VenueExternalIdRecord> ids = [];
        private readonly List<VenueEvidenceRecord> evidence = [];
        private readonly HashSet<(string Key, Guid Venue)> known = state
            .Names.SelectMany(g => g.Select(v => (g.Key, v)))
            .ToHashSet();
        private readonly HashSet<(string, string)> idsAdded = [];

        public List<IReadOnlyList<LegacyVenue>> Left { get; } = [];
        public List<LegacyVenue> Unplaced { get; } = [];

        public IEnumerable<string> NamesOf(Guid venue) =>
            known.Where(n => n.Venue == venue).Select(n => n.Key);

        public bool KnownIn(string localityKey, string key) =>
            state.ByLocality[localityKey].Any(v => known.Contains((key, v.Uuid)));

        public void Place(LegacyVenue row, Guid venue, float confidence)
        {
            evidence.Add(new VenueEvidenceRecord(row.Uuid, venue, confidence));
            if (row.Key != null && known.Add((row.Key, venue)))
            {
                names.Add(
                    new VenueNameRecord(
                        venue,
                        row.Name.Trim(),
                        VenueNameKinds.Spelling,
                        Confidence: confidence
                    )
                );
            }

            if (row.ExternalId is { } id && !state.ExternalIds.ContainsKey(id) && idsAdded.Add(id))
            {
                ids.Add(new VenueExternalIdRecord(id.Provider, id.Key, venue, confidence));
            }
        }

        /// A venue for rows of one name in one town: named by the spelling most uses carry.
        public Guid Create(IReadOnlyList<LegacyVenue> rows)
        {
            var venue = Guid.CreateVersion7();
            var display = rows.GroupBy(r => r.Name.Trim())
                .OrderByDescending(g => g.Sum(r => r.Uses))
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .First()
                .OrderByDescending(r => r.Uses)
                .First();
            newVenues.Add(
                new VenueRecord(
                    venue,
                    display.Name.Trim(),
                    City: display.Locality!.City,
                    Region: display.Locality.Region,
                    Country: display.Locality.Country,
                    Confidence: Exact
                )
            );
            foreach (var row in rows.OrderByDescending(r => r.Uses))
            {
                Place(row, venue, Exact);
            }

            return venue;
        }

        public VenuePlan Build() => new(newVenues, names, ids, evidence, Left, Unplaced);
    }
}
