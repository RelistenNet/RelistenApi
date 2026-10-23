using System;
using System.Collections.Generic;
using System.Linq;
using Relisten.Normalization.Titles;

namespace Relisten.Normalization.Songs;

/// How one title part resolves: to songs (usually one; an unparsed combination can be
/// several, in order), to nothing (not a song), with a confidence and the basis it rests on
/// (TrackBasis). Qualifier is one the lookup lifted out of the name ("reprise" for "Plane
/// Crash reprise"). Null = unresolved.
public sealed record PartDecision(
    IReadOnlyList<Guid> Songs,
    float Confidence,
    string Basis = TrackBasis.Ruling,
    string? Qualifier = null
)
{
    /// For a ruling kept by position: the normalised text it was written for.
    public string? Text { get; init; }
}

/// Rows to write, and tracks whose machine rows no longer follow from anything (an alias they
/// rested on is gone): cleared, they are unresolved again.
public sealed record MatchResult(
    IReadOnlyList<TrackPartRecord> Records,
    IReadOnlyList<Guid> Cleared
);

/// Deterministic title matching (string-theory docs/matching.md T1). Each part's song name is
/// looked up among the artist's song names, then its aliases (each also with a leading track
/// number folded away, TrackNumberFold, and with a trailing qualifier word lifted out), at
/// 0.95; then the callers' own decisions (the LLM's rulings); then the parser's noise hint
/// (tuning, crowd: not a song, 0.95); then a unique name or alias a typo away (0.9).
///
/// Rows that follow from songs and aliases are derived (basis song_name, alias, noise, open)
/// and rebuilt on every match, so a fixed alias reaches every track. A machine's ruling on a
/// track is kept unless an exact name or alias now decides that part; a person's row locks the
/// track.
public static class TrackMatcher
{
    public const string Author = "title-matcher@2.0.0";
    public const float Threshold = 0.9f;
    private const float Exact = 0.95f;

    public static MatchResult Match(
        CatalogueState state,
        IReadOnlyDictionary<string, PartDecision>? extra = null
    )
    {
        var known = state
            .SongNames.Select(n => (n.Key, Song: n.Value, Basis: TrackBasis.SongName))
            .Concat(
                state
                    .Aliases.Where(a => !state.SongNames.ContainsKey(a.Key))
                    .Select(a => (a.Key, Song: a.Value, Basis: TrackBasis.Alias))
            )
            .ToDictionary(k => k.Key, k => (k.Song, k.Basis));
        var typoIndex = known.Keys.GroupBy(k => k[0]).ToDictionary(g => g.Key, g => g.ToList());
        var cache = new Dictionary<TitlePart, PartDecision?>();

        PartDecision? Lookup(string key, string? qualifier)
        {
            key = TrackNumberFold.Fold(
                key,
                k => known.ContainsKey(k) || extra?.ContainsKey(k) == true
            );
            if (known.TryGetValue(key, out var hit))
            {
                return new PartDecision([hit.Song], Exact, hit.Basis, qualifier);
            }

            return extra != null && extra.TryGetValue(key, out var given)
                ? given with
                {
                    Basis = TrackBasis.Ruling,
                    Qualifier = qualifier,
                }
                : null;
        }

        PartDecision? Decide(TitlePart part)
        {
            if (cache.TryGetValue(part, out var cached))
            {
                return cached;
            }

            var decision =
                Lookup(part.Key, null)
                ?? (part.Bare != null ? Lookup(part.Bare, part.BareQualifier) : null);
            if (decision == null && part.Hint == PartHint.Noise)
            {
                decision = new PartDecision([], Exact, TrackBasis.Noise);
            }
            else if (decision == null && TypoMatch(part.Key, typoIndex) is { } near)
            {
                decision = new PartDecision([known[near].Song], Threshold, known[near].Basis);
            }

            cache[part] = decision;
            return decision;
        }

        var records = new List<TrackPartRecord>();
        var cleared = new List<Guid>();
        foreach (var track in state.Tracks.Where(t => !t.Locked))
        {
            // Retitled to a bare file name or label: whatever was written for the old title
            // no longer follows from it.
            if (!track.Title.HasText)
            {
                if (track.Existing.Count > 0)
                {
                    cleared.Add(track.Uuid);
                }

                continue;
            }

            var (rulings, byPosition) = Rulings(track);
            // When the parse has as many parts as the written rows (the text moved under a newer
            // parser), a ruling no text matches is kept at its position.
            var aligned = byPosition.Count == track.Title.Parts.Count;
            var decisions = track
                .Title.Parts.Select(
                    (p, i) =>
                    {
                        var d = Decide(p);
                        var ruling =
                            rulings.TryGetValue(p.Key, out var queue) && queue.Count > 0
                                ? queue.Dequeue()
                            // Kept by position only while its text still names this part
                            // ("12 - nebraska" for "nebraska"): a retitled track ("Foo" now
                            // "Bar") isn't the decision it was.
                            : aligned
                            && byPosition[i] is { Text: { } was }
                            && $" {was} ".Contains($" {p.Key} ")
                                ? byPosition[i]
                            : null;
                        // An exact name or alias decides a part over a ruling; a guess doesn't.
                        if (
                            d is
                            { Basis: TrackBasis.SongName or TrackBasis.Alias, Confidence: >= Exact }
                        )
                        {
                            return (Part: p, Decision: d);
                        }

                        return (
                            Part: p,
                            Decision: ruling ?? (d is { } g && g.Confidence >= Threshold ? g : null)
                        );
                    }
                )
                .ToList();

            // A track is written when every part resolves, or, for a medley, when at least one
            // part resolves to a song: the known parts are kept and the rest left open, so
            // "(Check) Funky Bitch > Dream On > X" still shows Funky Bitch and Dream On.
            var anySong = decisions.Any(d => d.Decision is { Songs.Count: > 0 });
            if (decisions.Any(d => d.Decision == null) && !(anySong && decisions.Count > 1))
            {
                if (track.Existing.Count > 0)
                {
                    cleared.Add(track.Uuid);
                }

                continue;
            }

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
                            Threshold,
                            Resolved: false,
                            Basis: TrackBasis.Open,
                            Notes: part.Notes
                        )
                    );
                    continue;
                }

                var qualifiers =
                    decision.Qualifier == null || part.Qualifiers.Contains(decision.Qualifier)
                        ? part.Qualifiers
                        : [.. part.Qualifiers, decision.Qualifier];
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
                            last ? qualifiers : [],
                            decision.Confidence,
                            Basis: decision.Basis,
                            Notes: last ? part.Notes : []
                        )
                    );
                }
            }
        }

        return new MatchResult(records, cleared);
    }

    /// A track's written rows as decisions, in title order. Consecutive rows of one kind
    /// (ruling, derived, open) with one text, joined by segues, are one decision: a part naming
    /// several songs. Unless the current title names that part as many times in a row ("Rebula
    /// > Rebula"): then each occurrence is its own decision.
    internal static List<(string Key, IReadOnlyList<ExistingPart> Rows)> Written(
        CatalogueTrack track
    )
    {
        // How many times in a row the title names each part, run by run.
        var parsedRuns = new Dictionary<string, Queue<int>>();
        var parts = track.Title.Parts;
        for (var i = 0; i < parts.Count; )
        {
            var j = i;
            while (j < parts.Count && parts[j].Key == parts[i].Key)
            {
                j++;
            }

            if (!parsedRuns.TryGetValue(parts[i].Key, out var runs))
            {
                parsedRuns[parts[i].Key] = runs = new Queue<int>();
            }

            runs.Enqueue(j - i);
            i = j;
        }

        var written = new List<(string Key, IReadOnlyList<ExistingPart> Rows)>();
        var run = new List<ExistingPart>();

        void Close()
        {
            if (run.Count == 0)
            {
                return;
            }

            var key = run[0].Text == null ? "" : KeyOf(run[0].Text!);
            var times =
                parsedRuns.TryGetValue(key, out var runs) && runs.Count > 0 ? runs.Dequeue() : 1;
            var size = times > 1 && run.Count % times == 0 ? run.Count / times : run.Count;
            for (var k = 0; k < run.Count; k += size)
            {
                written.Add((key, run.Skip(k).Take(size).ToList()));
            }

            run.Clear();
        }

        static string Kind(ExistingPart p) =>
            p.Basis == TrackBasis.Ruling && p.Resolved ? "ruling"
            : p.Resolved ? "derived"
            : "open";
        foreach (var part in track.Existing)
        {
            if (
                run.Count > 0
                && (part.Text != run[0].Text || Kind(part) != Kind(run[0]) || !run[^1].SegueAfter)
            )
            {
                Close();
            }

            run.Add(part);
        }

        Close();
        return written;
    }

    /// A machine's rulings on a track, by song name, in title order: the decision that
    /// resolved a one-off spelling has no alias to rediscover. A text that recurs (a sandwich,
    /// "Rebula > Jam > Rebula") is one decision per occurrence. Also returns the written parts
    /// in order, as rulings or null (derived), for a parse whose text moved.
    private static (
        Dictionary<string, Queue<PartDecision>> ByKey,
        List<PartDecision?> ByPosition
    ) Rulings(CatalogueTrack track)
    {
        var rulings = new Dictionary<string, Queue<PartDecision>>();
        var byPosition = new List<PartDecision?>();
        foreach (var (key, rows) in Written(track))
        {
            if (!(rows[0].Basis == TrackBasis.Ruling && rows[0].Resolved && rows[0].Text != null))
            {
                byPosition.Add(null);
                continue;
            }

            if (!rulings.TryGetValue(key, out var queue))
            {
                rulings[key] = queue = new Queue<PartDecision>();
            }

            // A part never names one song twice in a row: repeats are a duplicated ruling.
            var songs = rows.Select(e => e.SongUuid).OfType<Guid>().ToList();
            var decision = new PartDecision(
                songs.Where((s, i) => i == 0 || s != songs[i - 1]).ToList(),
                rows.Min(e => e.Confidence) ?? Threshold
            );
            queue.Enqueue(decision);
            byPosition.Add(decision with { Text = TitleNormalizer.Normalize(rows[0].Text!) });
        }

        return (rulings, byPosition);
    }

    /// A written part's key as the current parser reads its text, so a ruling written under an
    /// earlier parse ("(Check) Jam", before soundchecks were lifted out) still matches.
    public static string KeyOf(string text)
    {
        var parsed = TitleParser.Parse(text).Parts;
        return parsed.Count == 1 ? parsed[0].Key : TitleNormalizer.Normalize(text);
    }

    /// Upstream setlist songs whose name is a song's name or an alias.
    public static IReadOnlyList<EvidenceRecord> Evidence(
        CatalogueState state,
        float confidence = Exact
    ) =>
        state
            .UnmappedSetlistSongs.Select(s =>
                (
                    s.Uuid,
                    Song: state.SongNames.TryGetValue(s.Key, out var named) ? named
                    : state.Aliases.TryGetValue(s.Key, out var aliased) ? aliased
                    : (Guid?)null
                )
            )
            .Where(s => s.Song != null)
            .Select(s => new EvidenceRecord(s.Uuid, s.Song!.Value, confidence))
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
