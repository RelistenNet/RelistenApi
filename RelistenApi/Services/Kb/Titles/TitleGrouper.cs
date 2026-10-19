using System;
using System.Collections.Generic;
using System.Linq;

namespace Relisten.Kb.Titles;

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
/// (string-theory docs/llm.md §3). Keys join when they are equal without spaces, or within a
/// small edit distance (1, or 2 for keys of 10+ characters) and start with the same letter.
public static class TitleGrouper
{
    public const string Version = "title-group/1";

    private const int MinLength = 4;

    public static IReadOnlyList<TitleGroup> Group(IEnumerable<KeyUsage> usages)
    {
        var keys = usages.ToList();
        var parent = Enumerable.Range(0, keys.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        void Union(int a, int b) => parent[Find(a)] = Find(b);

        // Blocking: only keys that share a first letter, compared within a length window.
        foreach (var block in Enumerable.Range(0, keys.Count).GroupBy(i => keys[i].Key[0]))
        {
            var sorted = block.OrderBy(i => keys[i].Key.Length).ToArray();
            for (var a = 0; a < sorted.Length; a++)
            {
                var ka = keys[sorted[a]].Key;
                for (var b = a + 1; b < sorted.Length; b++)
                {
                    var kb = keys[sorted[b]].Key;
                    if (kb.Length - ka.Length > 2)
                    {
                        break;
                    }

                    if (Similar(ka, kb))
                    {
                        Union(sorted[a], sorted[b]);
                    }
                }
            }
        }

        return Enumerable
            .Range(0, keys.Count)
            .GroupBy(Find)
            .Select(g => new TitleGroup(
                g.Select(i => keys[i]).OrderByDescending(k => k.Shows).ToList()
            ))
            .OrderByDescending(g => g.Shows)
            .ThenBy(g => g.Keys[0].Key, StringComparer.Ordinal)
            .ToList();
    }

    /// Requests of about `size` groups each, most-played first, so the first chunk carries an
    /// artist's core repertoire.
    public static IReadOnlyList<IReadOnlyList<TitleGroup>> Chunk(
        IReadOnlyList<TitleGroup> groups,
        int size
    ) => groups.Chunk(size).Select(c => (IReadOnlyList<TitleGroup>)c).ToList();

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
