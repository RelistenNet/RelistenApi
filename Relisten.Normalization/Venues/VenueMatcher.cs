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
    public const string Author = "venue-matcher@2.6.0";
    public const float Exact = 0.95f;
    public const float Likely = 0.9f;

    public static VenuePlan Match(VenueState state)
    {
        var plan = new PlanBuilder(state);
        var open = PlaceOnSameShows(
            state,
            PlaceOnLearnedIds(PlaceOnKnown(state, plan), plan),
            plan
        );
        foreach (var family in Families(open, state.SameShowRows))
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

            var named = VenueNormalizer
                .Identities(row.Key)
                .SelectMany(id => state.IdentityNames[id])
                .Distinct()
                .Where(state.Venues.ContainsKey)
                .ToList();
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

    /// Every tape of a show was recorded at one place: a row whose shows the other tapes put
    /// on one venue joins it, when that covers at least half its show dates and the row
    /// could be that venue by its name (initials, alike, a shared place word) or has no usable
    /// town. A row whose name says otherwise ("Good Music and Other Stuff" the afternoon The
    /// Orange Peel had the show) is left: an in-store or a late set is another place.
    private static List<LegacyVenue> PlaceOnSameShows(
        VenueState state,
        List<LegacyVenue> open,
        PlanBuilder plan
    )
    {
        var rest = new List<LegacyVenue>();
        foreach (var row in open)
        {
            if (
                state.SameShows.TryGetValue(row.Uuid, out var votes)
                && votes.Count == 1
                && votes.Single() is var (venue, dates)
                && dates * 2 >= state.ShowDates.GetValueOrDefault(row.Uuid, 1)
                && state.Venues.TryGetValue(venue, out var v)
                && (
                    row.Locality is not { Trusted: true }
                    || (
                        (
                            v.LocalityKey == row.Locality.Key
                            || (
                                v.City != null
                                && VenueNormalizer.Normalize(v.City)
                                    == VenueNormalizer.Normalize(row.Locality.City)
                            )
                            || new Locality(v.City ?? "", v.Region, v.Country).RegionKey
                                == row.Locality.RegionKey
                        )
                        && plan.NamesOf(venue)
                            .Append(VenueNormalizer.Key(v.Name))
                            .OfType<string>()
                            .Any(n =>
                                VenueNormalizer.SameName(n, row.Key!)
                                || Alike(n, row.Key!)
                                || ShareADistinctiveWord(
                                    n,
                                    row.Key!,
                                    VenueNormalizer
                                        .Normalize(row.Locality.City)
                                        .Split(' ')
                                        .ToHashSet()
                                )
                            )
                    )
                )
            )
            {
                plan.Place(row, venue, Likely);
                continue;
            }

            rest.Add(row);
        }

        return rest;
    }

    /// An external id a row taught a venue in this pass places every other row carrying it,
    /// whatever order the rows came in.
    private static List<LegacyVenue> PlaceOnLearnedIds(List<LegacyVenue> open, PlanBuilder plan)
    {
        var rest = new List<LegacyVenue>();
        foreach (var row in open)
        {
            if (row.ExternalId is { } id && plan.LearnedVenue(id) is { } venue)
            {
                plan.Place(row, venue, Exact);
            }
            else
            {
                rest.Add(row);
            }
        }

        return rest;
    }

    /// Step 2: rows that could be one place, together, each in its town (a name is a different
    /// place in another state: House of Blues in Chicago and in Dallas). Rows are joined by one
    /// name in one town; one name in two towns of a state (a suburb) or in a town of one name in
    /// two states (a typo'd state); a shared external id; and names alike, or sharing a word
    /// that names a place, within one town. A row without a town joins the one family its name
    /// has, if it has only one.
    internal static List<IReadOnlyList<LegacyVenue>> Families(
        List<LegacyVenue> open,
        IReadOnlyList<(Guid A, Guid B)>? sameShows = null
    )
    {
        var parent = Enumerable.Range(0, open.Count).ToArray();
        int Find(int x)
        {
            while (parent[x] != x)
            {
                x = parent[x] = parent[parent[x]];
            }

            return x;
        }

        void Union(int a, int b) => parent[Find(a)] = Find(b);

        void UnionAll(IEnumerable<IEnumerable<int>> groups)
        {
            foreach (var group in groups)
            {
                var rows = group.ToList();
                foreach (var row in rows.Skip(1))
                {
                    Union(rows[0], row);
                }
            }
        }

        var trusted = Enumerable
            .Range(0, open.Count)
            .Where(i => open[i].Locality is { Trusted: true })
            .ToList();
        var named = trusted
            .SelectMany(i =>
                VenueNormalizer.Identities(open[i].Key!).Select(id => (Id: id, Row: i))
            )
            .ToList();
        UnionAll(named.GroupBy(n => (n.Id, open[n.Row].Locality!.RegionKey), n => n.Row));
        UnionAll(
            named.GroupBy(
                n => (n.Id, VenueNormalizer.Normalize(open[n.Row].Locality!.City)),
                n => n.Row
            )
        );
        UnionAll(
            Enumerable
                .Range(0, open.Count)
                .Where(i => open[i].ExternalId != null)
                .GroupBy(i => open[i].ExternalId)
        );

        foreach (var town in trusted.GroupBy(i => open[i].Locality!.Key))
        {
            var locality = open[town.First()].Locality!;
            var townWords = VenueNormalizer.Normalize(locality.City).Split(' ').ToHashSet();
            if (locality.Region != null)
            {
                townWords.Add(locality.Region.ToLowerInvariant());
            }

            var keys = town.GroupBy(i => open[i].Key!)
                .Select(g => (Key: g.Key, Local: WithoutTown(g.Key, townWords), Row: g.First()))
                .ToList();
            for (var i = 0; i < keys.Count; i++)
            {
                for (var j = i + 1; j < keys.Count; j++)
                {
                    if (
                        Alike(keys[i].Key, keys[j].Key)
                        || ShareADistinctiveWord(keys[i].Key, keys[j].Key, townWords)
                        || (
                            keys[i].Local is { } a
                            && keys[j].Local is { } b
                            && (a != keys[i].Key || b != keys[j].Key)
                            && (VenueNormalizer.SameName(a, b) || Alike(a, b))
                        )
                    )
                    {
                        Union(keys[i].Row, keys[j].Row);
                    }
                }
            }
        }

        // Rows of one show in one town were one place's, by different names ("BSU Field House" / "Brick Breeden Fieldhouse"), unless an
        // in-store or a late set: the LLM decides, together. Across towns a few mis-dated tapes
        // would chain a state's venues into one family; there each spelling's same_shows carry it.
        var index = Enumerable.Range(0, open.Count).ToDictionary(i => open[i].Uuid);
        foreach (var (a, b) in sameShows ?? [])
        {
            if (
                index.TryGetValue(a, out var i)
                && index.TryGetValue(b, out var j)
                && open[i].Locality is { Trusted: true } li
                && open[j].Locality is { Trusted: true } lj
                && li.Key == lj.Key
            )
            {
                Union(i, j);
            }
        }

        // A row without a town: into the one family its name has, else with its namesakes.
        var families = named.ToLookup(n => n.Id, n => n.Row);
        var weak = Enumerable.Range(0, open.Count).Except(trusted).ToList();
        var loose = new List<(string Id, int Row)>();
        foreach (var row in weak)
        {
            var ids = VenueNormalizer.Identities(open[row].Key!);
            var roots = ids.SelectMany(id => families[id]).Select(Find).Distinct().ToList();
            if (roots.Count == 1)
            {
                Union(row, roots[0]);
            }
            else
            {
                loose.AddRange(ids.Select(id => (id, row)));
            }
        }

        UnionAll(loose.GroupBy(l => l.Id, l => l.Row));

        return Enumerable
            .Range(0, open.Count)
            .GroupBy(Find)
            .Select(g => (IReadOnlyList<LegacyVenue>)g.Select(i => open[i]).ToList())
            .ToList();
    }

    /// Two names that could be one place, for the LLM to decide: initials ("MSG"); the same
    /// name with another kind of room ("Winterland Arena" / "Winterland Ballroom"); one name's
    /// words all within the other's, a typo or an abbreviation allowed ("Dobson Arena" / "Dobson
    /// Ice Arena", "Northrop Auditorium, U. of Minn." / "... U. of Minnesota"), sharing one
    /// distinctive word exactly or every word a typo apart ("The Garage" / "The Garrage").
    internal static bool Alike(string a, string b)
    {
        if (VenueNormalizer.IsInitialsOf(a, b) || VenueNormalizer.IsInitialsOf(b, a))
        {
            return true;
        }

        var (coreA, coreB) = (Core(a), Core(b));
        if (coreA.Length >= 3 && coreA == coreB)
        {
            return true;
        }

        var (wa, wb) = (
            VenueNormalizer.Words(a).Distinct().ToList(),
            VenueNormalizer.Words(b).Distinct().ToList()
        );
        var (small, large) = wa.Count <= wb.Count ? (wa, wb) : (wb, wa);
        if (
            small.All(w => large.Any(l => WordAlike(w, l)))
            && (
                small.Count == large.Count
                || (
                    small.Any(w =>
                        Distinctive(w) && large.Any(l => l == w || (w.Length >= 7 && Typo(w, l)))
                    ) && (small.Count >= 2 || small[0].Length >= 5)
                )
            )
        )
        {
            return true;
        }

        // Spaced differently and a typo apart ("13-13 Club" / "13x13 Club").
        var (fa, fb) = (VenueNormalizer.Words(a), VenueNormalizer.Words(b));
        var (ca, cb) = (string.Concat(fa), string.Concat(fb));
        return fa.Count != fb.Count
            && Math.Min(ca.Length, cb.Length) >= 6
            && TitleGrouper.BoundedLevenshtein(ca, cb, 2) <= (ca.Length >= 12 ? 2 : 1);
    }

    /// One word for another: the same, a doubled letter apart ("starr"), a typo apart (five letters or more, never "east" /
    /// "west"), or its abbreviation ("minn", "fac").
    private static bool WordAlike(string w, string other) =>
        w == other
        || (w.Length >= 4 && other.Length >= 4 && Undoubled(w) == Undoubled(other))
        || Typo(w, other)
        || Abbreviates(w, other)
        || Abbreviates(other, w);

    /// A letter or two wrong, missing or swapped: "elbo" / "elbow", "arean" / "arena",
    /// "woodmen" / "woodsman". Short words keep their first letter ("east" is not "west").
    private static bool Typo(string w, string other)
    {
        var shortest = Math.Min(w.Length, other.Length);
        if (shortest < 4 || (shortest < 5 && w[0] != other[0]) || w.Any(char.IsDigit))
        {
            return false;
        }

        var limit = shortest >= 7 ? 2 : 1;
        return TitleGrouper.BoundedLevenshtein(w, other, limit) <= limit || Swapped(w, other);
    }

    private static bool Swapped(string w, string other)
    {
        if (w.Length != other.Length)
        {
            return false;
        }

        var differ = Enumerable.Range(0, w.Length).Where(i => w[i] != other[i]).ToList();
        return differ.Count == 2
            && differ[1] == differ[0] + 1
            && w[differ[0]] == other[differ[1]]
            && w[differ[1]] == other[differ[0]];
    }

    private static bool Abbreviates(string w, string other) =>
        w.Length >= 3
        && other.Length > w.Length + 1
        && other.StartsWith(w, StringComparison.Ordinal)
        && !w.Any(char.IsDigit);

    /// Two names in one town sharing a word that names a place ("Schottenstein Center, OSU" /
    /// "Jerome Schottenstein Center"): not a generic word, the town's own name or a number.
    internal static bool ShareADistinctiveWord(
        string a,
        string b,
        IReadOnlySet<string> townWords
    ) =>
        VenueNormalizer
            .Words(a)
            .Intersect(VenueNormalizer.Words(b))
            .Any(w =>
                w.Length >= 4 && Distinctive(w) && !townWords.Contains(w) && !w.Any(char.IsDigit)
            );

    /// The name without its town's words ("Hampton Coliseum" in Hampton is "Coliseum",
    /// "Prudential Center Newark NJ" in Newark is "Prudential Center"); null when nothing is left.
    private static string? WithoutTown(string key, IReadOnlySet<string> townWords)
    {
        var words = key.Split(' ').Where(w => !townWords.Contains(w)).ToList();
        return words.Count > 0 ? string.Join(' ', words) : null;
    }

    /// "starr" → "star": a doubled letter written once.
    private static string Undoubled(string w) =>
        string.Concat(w.Where((c, i) => i == 0 || c != w[i - 1]));

    /// Whether keys are all one name, joined by shared identities.
    private static bool OneName(IReadOnlyList<string> keys)
    {
        var reached = new HashSet<string> { keys[0] };
        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var key in keys.Where(k => !reached.Contains(k)))
            {
                if (reached.Any(r => VenueNormalizer.SameName(r, key)))
                {
                    reached.Add(key);
                    grew = true;
                }
            }
        }

        return reached.Count == keys.Count;
    }

    private static readonly HashSet<string> GenericWords =
    [
        "theater",
        "arena",
        "ballroom",
        "auditorium",
        "hall",
        "music",
        "center",
        "club",
        "nightclub",
        "amphitheater",
        "pavilion",
        "stadium",
        "coliseum",
        "room",
        "lounge",
        "bar",
        "cafe",
        "tavern",
        "pub",
        "grill",
        "live",
        "venue",
        "stage",
        "tent",
        "and",
        "at",
        "of",
    ];

    /// Words too common to tie two names together: kinds of place, parts of a campus or a
    /// festival, directions.
    private static readonly HashSet<string> CommonWords =
    [
        "park",
        "festival",
        "st",
        "ave",
        "university",
        "college",
        "state",
        "company",
        "main",
        "international",
        "national",
        "memorial",
        "civic",
        "county",
        "city",
        "community",
        "fairground",
        "fair",
        "house",
        "field",
        "garden",
        "hotel",
        "casino",
        "church",
        "school",
        "high",
        "brewery",
        "inn",
        "square",
        "west",
        "east",
        "north",
        "south",
        "new",
        "old",
    ];

    private static bool Distinctive(string w) =>
        !GenericWords.Contains(w) && !CommonWords.Contains(w);

    private static string Core(string key) =>
        string.Join(' ', key.Split(' ').Where(w => !GenericWords.Contains(w)));

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

        if (!OneName(family.Select(r => r.Key!).Distinct().ToList()))
        {
            return false;
        }

        var trusted = family.Where(r => r.Locality is { Trusted: true }).ToList();
        // The same name in a town of one name in two states, neither dominant (Pelham, AL and
        // Pelham, TN): one of them is a typo, and only the LLM can tell which.
        if (
            trusted
                .GroupBy(r => VenueNormalizer.Normalize(r.Locality!.City))
                .Any(t => t.Select(r => r.Locality!.RegionKey).Distinct().Count() > 1)
        )
        {
            return false;
        }

        var byRegion = trusted.GroupBy(r => r.Locality!.RegionKey!).ToList();
        if (
            byRegion.Count == 0
            || byRegion.Any(g => g.Select(r => r.Locality!.Key).Distinct().Count() > 1)
        )
        {
            return false;
        }

        // A name already known in its state (in that town or a neighbouring one) is the LLM's
        // to place: an incremental run must not split a venue because uploaders wrote a
        // neighbouring town (Higher Ground in Burlington, then in South Burlington).
        if (byRegion.Any(g => plan.KnownInRegion(g.Key, family[0].Key!)))
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

        public bool KnownInRegion(string regionKey, string key) =>
            known.Any(n =>
                VenueNormalizer.SameName(n.Key, key)
                && state.Venues.TryGetValue(n.Venue, out var v)
                && v.City != null
                && new Locality(v.City, v.Region, v.Country).RegionKey == regionKey
            );

        private readonly Dictionary<(string, string), Guid> learned = [];

        public Guid? LearnedVenue((string, string) id) =>
            learned.TryGetValue(id, out var v) ? v : null;

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
                learned[id] = venue;
            }
        }

        /// A venue for rows of one name in one town: named by the spelling most uses carry.
        public Guid Create(IReadOnlyList<LegacyVenue> rows)
        {
            var venue = Guid.CreateVersion7();
            // The town most of its rows are filed under (one stray tape's "Chicago, IL" never
            // moves a stadium out of Charleston), named by its most used spelling there.
            var home = rows.Where(r => r.Locality != null)
                .GroupBy(r => r.Locality!.Key)
                // Rows are separate artists' and sources' say; uses only break a tie.
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Sum(r => Math.Max(r.Uses, 1)))
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .First()
                .Key;
            var display = rows.Where(r => r.Locality?.Key == home)
                .GroupBy(r => r.Name.Trim())
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
