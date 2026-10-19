using System;
using System.Collections.Generic;
using System.Linq;
using Relisten.Kb.Titles;

namespace Relisten.Kb.Catalogue;

/// How one title part resolves: to songs (usually one; an unparsed combination can be
/// several, in order), to nothing (not a song), with a confidence. Null = unresolved.
public sealed record PartDecision(IReadOnlyList<Guid> Songs, float Confidence);

/// Deterministic title matching (string-theory docs/matching.md T1): each part of an
/// unresolved track resolves by exact alias (0.95; a leading track number folds away when the
/// rest is a known spelling, TrackNumberFold), then a unique alias a typo away (0.9),
/// then the parser's noise hint (tuning, crowd, banter: not a song, 0.95). Callers can add
/// decisions of their own (the LLM's answers for keys the alias table can't hold). A track
/// is written only when every part resolves at or above the threshold.
public static class TrackMatcher
{
    public const string Author = "title-matcher@1.0.0";
    public const float Threshold = 0.9f;

    public static IReadOnlyList<TrackPartRecord> Match(
        CatalogueState state,
        IReadOnlyDictionary<string, PartDecision>? extra = null
    )
    {
        var typoIndex = state
            .Aliases.Keys.GroupBy(k => k[0])
            .ToDictionary(g => g.Key, g => g.ToList());
        var cache = new Dictionary<TitlePart, PartDecision?>();

        PartDecision? Decide(TitlePart part)
        {
            if (cache.TryGetValue(part, out var known))
            {
                return known;
            }

            PartDecision? decision = null;
            var key = TrackNumberFold.Fold(
                part.Key,
                k => state.Aliases.ContainsKey(k) || extra?.ContainsKey(k) == true
            );
            if (extra != null && extra.TryGetValue(key, out var given))
            {
                decision = given;
            }
            else if (state.Aliases.TryGetValue(key, out var song))
            {
                decision = new PartDecision([song], 0.95f);
            }
            else if (part.Hint == PartHint.Noise)
            {
                decision = new PartDecision([], 0.95f);
            }
            else if (TypoMatch(key, typoIndex) is { } near)
            {
                decision = new PartDecision([state.Aliases[near]], 0.9f);
            }

            cache[part] = decision;
            return decision;
        }

        var records = new List<TrackPartRecord>();
        foreach (var track in state.UnresolvedTracks.Where(t => t.Title.HasText))
        {
            var decisions = track
                .Title.Parts.Select(p =>
                    (Part: p, Decision: Decide(p) is { } d && d.Confidence >= Threshold ? d : null)
                )
                .ToList();

            // A track is written when every part resolves, or, for a medley, when at least one
            // part resolves to a song: the known parts are kept and the rest marked unresolved,
            // so "(Check) Funky Bitch > Dream On > X" still shows Funky Bitch and Dream On.
            var anySong = decisions.Any(d => d.Decision is { Songs.Count: > 0 });
            if (decisions.Any(d => d.Decision == null) && !(anySong && decisions.Count > 1))
            {
                continue;
            }

            var confidence = decisions
                .Where(d => d.Decision != null)
                .Min(d => d.Decision!.Confidence);
            var number = 1;
            foreach (var (part, decision) in decisions)
            {
                if (decision == null)
                {
                    records.Add(
                        new TrackPartRecord(
                            track.Uuid,
                            number++,
                            null,
                            part.Text,
                            part.SegueAfter,
                            part.Qualifiers,
                            confidence,
                            Resolved: false
                        )
                    );
                    continue;
                }

                // A part naming several songs (an unparsed "space wrangler walkin") becomes one
                // record per song, each segueing into the next.
                var songs =
                    decision.Songs.Count == 0
                        ? [(Guid?)null]
                        : decision.Songs.Select(s => (Guid?)s).ToList();
                for (var i = 0; i < songs.Count; i++)
                {
                    var last = i == songs.Count - 1;
                    records.Add(
                        new TrackPartRecord(
                            track.Uuid,
                            number++,
                            songs[i],
                            part.Text,
                            last ? part.SegueAfter : true,
                            last ? part.Qualifiers : [],
                            confidence
                        )
                    );
                }
            }
        }

        return records;
    }

    /// Upstream setlist songs whose name is now an alias.
    public static IReadOnlyList<EvidenceRecord> Evidence(
        CatalogueState state,
        float confidence = 0.95f
    ) =>
        state
            .UnmappedSetlistSongs.Where(s => state.Aliases.ContainsKey(s.Key))
            .Select(s => new EvidenceRecord(s.Uuid, state.Aliases[s.Key], confidence))
            .ToList();

    private static string? TypoMatch(string key, IReadOnlyDictionary<char, List<string>> index)
    {
        if (key.Length < 4 || !index.TryGetValue(key[0], out var candidates))
        {
            return null;
        }

        var limit = key.Length >= 10 ? 2 : 1;
        var near = candidates
            .Where(c => TitleGrouper.BoundedLevenshtein(key, c, limit) <= limit)
            .Take(2)
            .ToList();
        return near.Count == 1 ? near[0] : null; // ambiguous typos stay unresolved
    }
}
