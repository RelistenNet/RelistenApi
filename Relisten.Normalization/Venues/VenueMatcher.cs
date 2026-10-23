using System;
using System.Collections.Generic;
using System.Linq;

namespace Relisten.Normalization.Venues;

/// What a deterministic pass decided: venues to create, their names and ids, and each mapped
/// legacy row. Left are rows for the LLM (a locality with several spellings, or spellings
/// beside existing venues); Unplaced rows have no usable name or locality.
public sealed record VenuePlan(
    IReadOnlyList<VenueRecord> NewVenues,
    IReadOnlyList<VenueNameRecord> Names,
    IReadOnlyList<VenueExternalIdRecord> ExternalIds,
    IReadOnlyList<VenueEvidenceRecord> Evidence,
    IReadOnlyList<LegacyVenue> Left,
    IReadOnlyList<LegacyVenue> Unplaced
);

/// Deterministic venue matching (string-theory docs/matching.md T3, steps 1–2). A legacy row
/// joins an existing venue by external id, by name within its locality, or as a short name
/// made of exactly one venue's initials. A new venue is created only where nothing is in
/// doubt: rows tied by an external id, or a locality with one spelling and no venue yet. Two
/// spellings in one place ("Madison Square Garden", "MSG"; "Spectrum", "CoreStates Spectrum")
/// are the LLM's to judge.
public static class VenueMatcher
{
    public const string Author = "venue-matcher@1.0.0";
    public const float Exact = 0.95f;
    public const float Initials = 0.9f;

    public static VenuePlan Match(VenueState state)
    {
        var newVenues = new List<VenueRecord>();
        var names = new List<VenueNameRecord>();
        var ids = new List<VenueExternalIdRecord>();
        var evidence = new List<VenueEvidenceRecord>();
        var left = new List<LegacyVenue>();
        var unplaced = new List<LegacyVenue>();
        var known = state.Names.SelectMany(g => g.Select(v => (g.Key, v))).ToHashSet();
        var idsAdded = new HashSet<(string, string)>();

        void Place(LegacyVenue row, Guid venue, float confidence)
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

        // 1. Rows that an existing venue already explains.
        var open = new List<LegacyVenue>();
        foreach (var row in state.Unmapped)
        {
            if (row.ExternalId is { } id && state.ExternalIds.TryGetValue(id, out var byId))
            {
                Place(row, byId, Exact);
                continue;
            }

            if (row.Key == null || row.Locality == null)
            {
                unplaced.Add(row);
                continue;
            }

            var here = state.ByLocality[row.Locality.Key].ToList();
            var byName = state
                .Names[row.Key]
                .Where(v =>
                    state.Venues.TryGetValue(v, out var venue)
                    && venue.LocalityKey == row.Locality.Key
                )
                .Distinct()
                .ToList();
            if (byName.Count == 1)
            {
                Place(row, byName[0], Exact);
                continue;
            }

            var byInitials =
                byName.Count == 0
                    ? here.Where(v =>
                            known.Any(n =>
                                n.v == v.Uuid && VenueNormalizer.IsInitialsOf(row.Key, n.Key)
                            )
                        )
                        .Select(v => v.Uuid)
                        .Distinct()
                        .ToList()
                    : [];
            if (byInitials.Count == 1)
            {
                Place(row, byInitials[0], Initials);
                continue;
            }

            open.Add(row);
        }

        // 2. New venues where nothing is in doubt. Rows sharing an external id are one place;
        // so are rows sharing a spelling in one locality.
        var parent = open.ToDictionary(r => r.Uuid, r => r.Uuid);
        Guid Find(Guid x)
        {
            while (parent[x] != x)
            {
                x = parent[x] = parent[parent[x]];
            }

            return x;
        }

        void Union(Guid a, Guid b) => parent[Find(a)] = Find(b);
        foreach (var group in open.Where(r => r.ExternalId != null).GroupBy(r => r.ExternalId))
        {
            var first = group.First();
            foreach (var row in group.Skip(1))
            {
                Union(first.Uuid, row.Uuid);
            }
        }

        foreach (var group in open.GroupBy(r => (r.Key, r.Locality!.Key)))
        {
            var first = group.First();
            foreach (var row in group.Skip(1))
            {
                Union(first.Uuid, row.Uuid);
            }
        }

        var spellingsHere = open.GroupBy(r => r.Locality!.Key)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Key).Distinct().Count());
        foreach (var component in open.GroupBy(r => Find(r.Uuid)).Select(g => g.ToList()))
        {
            var tiedById =
                component.Any(r => r.ExternalId != null)
                && component.Select(r => r.ExternalId).Where(i => i != null).Distinct().Count()
                    == 1;
            var localities = component.Select(r => r.Locality!.Key).Distinct().ToList();
            var alone =
                localities.Count == 1
                && spellingsHere[localities[0]] == 1
                && !state.ByLocality[localities[0]].Any();
            if (!tiedById && !alone)
            {
                left.AddRange(component);
                continue;
            }

            var venue = Guid.CreateVersion7();
            // The display name and place: the spelling (summed over its rows) most uses carry.
            var display = component
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
            foreach (var row in component.OrderByDescending(r => r.Uses))
            {
                Place(row, venue, Exact);
            }
        }

        return new VenuePlan(newVenues, names, ids, evidence, left, unplaced);
    }
}
