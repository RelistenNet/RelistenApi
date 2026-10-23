using System;
using System.Collections.Generic;
using System.Linq;

namespace Relisten.Normalization.Titles;

/// A distinct part key with how often an artist's tracks use it, and up to a few raw titles
/// it came from (context for whoever decides what it is).
public sealed record KeyUsage(string Key, int Tracks, int Shows, IReadOnlyList<string> Examples);

/// Spellings that are probably one song: keys that are identical or a typo apart.
public sealed record TitleGroup(IReadOnlyList<KeyUsage> Keys)
{
    public int Shows => Keys.Sum(k => k.Shows);
    public int Tracks => Keys.Sum(k => k.Tracks);
}

/// Joins near-identical part keys so the spellings of one song arrive at the LLM together
/// ("porch song", "prch song", "porchsong"), without deciding anything: grouping only shapes
/// the requests, and the LLM may still split a group or map two groups to one song
/// (string-theory docs/llm.md §3). A key joins the group whose main spelling (its most
/// played) it is equal to without spaces, or within a small edit distance (1, or 2 for keys
/// of 10+ characters), starting with the same letter. Joining is never transitive: chains
/// of one-edit steps would snowball unrelated titles into one group.
public static class TitleGrouper
{
    public const string Version = "title-group/2";

    private const int MinLength = 4;

    public static IReadOnlyList<TitleGroup> Group(IEnumerable<KeyUsage> usages)
    {
        var groups = new List<List<KeyUsage>>();
        var byLetter = new Dictionary<char, List<List<KeyUsage>>>();

        // Most-played first, so each group's main spelling is its most common one.
        foreach (
            var key in usages
                .OrderByDescending(k => k.Shows)
                .ThenBy(k => k.Key, StringComparer.Ordinal)
        )
        {
            if (!byLetter.TryGetValue(key.Key[0], out var candidates))
            {
                byLetter[key.Key[0]] = candidates = [];
            }

            var home = candidates.FirstOrDefault(g => Similar(g[0].Key, key.Key));
            if (home == null)
            {
                home = [];
                candidates.Add(home);
                groups.Add(home);
            }

            home.Add(key);
        }

        return groups
            .Select(g => new TitleGroup(g))
            .OrderByDescending(g => g.Shows)
            .ThenBy(g => g.Keys[0].Key, StringComparer.Ordinal)
            .ToList();
    }

    /// The next request: groups most-played first, up to maxGroups groups or maxKeys keys
    /// (always at least one group), so the first chunk carries an artist's core repertoire
    /// and no request grows past what the model answers well.
    public static IReadOnlyList<TitleGroup> NextChunk(
        IEnumerable<TitleGroup> groups,
        int maxGroups,
        int maxKeys
    )
    {
        var chunk = new List<TitleGroup>();
        var keys = 0;
        foreach (var group in groups)
        {
            if (chunk.Count == maxGroups || (chunk.Count > 0 && keys + group.Keys.Count > maxKeys))
            {
                break;
            }

            chunk.Add(group);
            keys += group.Keys.Count;
        }

        return chunk;
    }

    private static bool Similar(string a, string b)
    {
        if (a.Replace(" ", "") == b.Replace(" ", ""))
        {
            return true;
        }

        if (Math.Min(a.Length, b.Length) < MinLength)
        {
            return false;
        }

        var limit = Math.Max(a.Length, b.Length) >= 10 ? 2 : 1;
        return BoundedLevenshtein(a, b, limit) <= limit;
    }

    /// Levenshtein distance, giving up (returning limit + 1) once every path exceeds limit.
    public static int BoundedLevenshtein(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit)
        {
            return limit + 1;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost
                );
                rowMin = Math.Min(rowMin, current[j]);
            }

            if (rowMin > limit)
            {
                return limit + 1;
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
