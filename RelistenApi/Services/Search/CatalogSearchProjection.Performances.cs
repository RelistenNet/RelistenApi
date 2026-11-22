using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Identifiers;

namespace Relisten.Search;

using static SearchText;
using Document = Dictionary<string, object>;

internal sealed partial class CatalogSearchProjection
{
    public async IAsyncEnumerable<Document> Performances(
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var mappings = (await _rows.Mappings(cancellationToken)).ToLookup(
            row => row.TrackUuid,
            row => row.SongId
        );
        var groups = new Dictionary<string, PerformanceGroup>(StringComparer.Ordinal);
        ShowContext? show = null;

        await foreach (var track in _rows.Tracks().WithCancellation(cancellationToken))
        {
            if (show?.Row.Id != track.ShowId)
            {
                if (show != null)
                {
                    foreach (var document in FinishShow(show, groups))
                    {
                        yield return document;
                    }

                    groups.Clear();
                }

                show = _shows[track.ShowId];
            }

            CollectTrack(show, groups, track, mappings[track.Uuid]);
        }

        if (show != null)
        {
            foreach (var document in FinishShow(show, groups))
            {
                yield return document;
            }
        }
    }

    private void CollectTrack(
        ShowContext show,
        Dictionary<string, PerformanceGroup> groups,
        SearchProjectionRows.Track track,
        IEnumerable<string> mappedSongs
    )
    {
        var recording = _recordings[track.SourceId];
        var plays = _plays.GetValueOrDefault(track.Uuid);
        if (plays.Plays > 0)
        {
            PlayedFiles++;
        }

        // Each live track contributes once to its show and source, even if the
        // catalog maps a medley file to more than one canonical song.
        show.Plays += plays.Plays;
        recording.Plays += plays.Plays;

        var file = new SearchFile(
            UuidTypeId.Format("track", track.Uuid),
            recording.Id,
            track.SetName,
            track.SetIndex,
            track.Position,
            track.Duration,
            plays.Plays,
            recording.Row.Soundboard,
            Soundcheck().IsMatch(track.Title),
            track.Title
        );

        var raw = RawTitle(track.Title);
        foreach (
            var key in mappedSongs
                .DefaultIfEmpty("raw:" + Fold(raw))
                .Distinct(StringComparer.Ordinal)
        )
        {
            if (!groups.TryGetValue(key, out var group))
            {
                var song = _songs.GetValueOrDefault(key);
                group = new PerformanceGroup(song?.Name ?? raw, song?.Aliases ?? []);
                groups.Add(key, group);
            }

            group.Files.Add(new FileCandidate(file, recording.Row.Rating));
        }
    }

    private static IEnumerable<Document> FinishShow(
        ShowContext show,
        Dictionary<string, PerformanceGroup> groups
    )
    {
        var mentions = ReviewMentions(show.Reviews, groups.Values);
        show.Reviews.Clear();
        foreach (var (key, group) in groups)
        {
            var files = group.Files.Select(candidate => candidate.File).ToArray();
            var count = mentions.GetValueOrDefault(group);
            var plays = files.Sum(file => file.Plays);
            var label = group.Label.Length == 0 ? "Untitled track" : group.Label;

            yield return new Document(show.Fields)
            {
                ["id"] = PerformanceId(show.Row.Uuid, key),
                ["kind"] = "performance",
                ["label"] = label,
                ["name"] = Fold(label),
                ["aliases"] = Join(group.Aliases),
                ["song_id"] = key.StartsWith("raw:", StringComparison.Ordinal) ? "" : key,
                ["files"] = files,
                ["preview"] = Preview(group.Files),
                ["soundboard"] = files.Any(file => file.Soundboard),
                ["soundcheck"] = files.All(file => file.Soundcheck),
                ["longest"] = files.Max(file => file.Duration),
                ["track_plays"] = plays,
                ["review_mentions"] = count,
                ["popularity"] = Math.Round(Math.Log(1d + plays) + 0.25 * Math.Log(1d + count), 5),
            };
        }
    }

    private static SearchFile Preview(List<FileCandidate> candidates)
    {
        // The prototype breaks otherwise equal play counts with the preview it
        // chose before joining plays. Preserve that choice for a fresh build.
        var original = candidates
            .MaxBy(candidate =>
                (
                    !candidate.File.Soundcheck,
                    candidate.File.Soundboard,
                    candidate.Quality,
                    candidate.File.Duration,
                    -candidate.File.Position,
                    candidate.File.TrackId
                )
            )!
            .File;
        return candidates
            .MaxBy(candidate =>
                (
                    !candidate.File.Soundcheck,
                    candidate.File.Soundboard,
                    candidate.File.Plays,
                    candidate.File == original,
                    candidate.File.Duration,
                    -candidate.File.Position,
                    candidate.File.TrackId
                )
            )!
            .File;
    }

    private static Dictionary<PerformanceGroup, int> ReviewMentions(
        List<string> reviews,
        IEnumerable<PerformanceGroup> groups
    )
    {
        var counts = new Dictionary<PerformanceGroup, int>();
        if (reviews.Count == 0)
        {
            return counts;
        }

        var titles = groups
            .SelectMany(group =>
                group
                    .Aliases.Prepend(group.Label)
                    .Select(title => (Group: group, Text: Fold(title)))
            )
            .Where(title => title.Text.Length > 0)
            .ToLookup(title => FirstWord(title.Text), StringComparer.Ordinal);

        foreach (var review in reviews)
        {
            // A review contributes at most once per song across all its aliases.
            var mentioned = new HashSet<PerformanceGroup>();
            for (var offset = 0; offset < review.Length; )
            {
                var remaining = review.AsSpan(offset);
                var word = FirstWord(remaining);
                foreach (var title in titles[word.ToString()])
                {
                    if (
                        remaining.StartsWith(title.Text, StringComparison.Ordinal)
                        && (
                            remaining.Length == title.Text.Length
                            || remaining[title.Text.Length] == ' '
                        )
                    )
                    {
                        mentioned.Add(title.Group);
                    }
                }

                offset += word.Length + 1;
            }

            foreach (var group in mentioned)
            {
                counts[group] = counts.GetValueOrDefault(group) + 1;
            }
        }

        return counts;
    }

    private static string FirstWord(string text) => FirstWord(text.AsSpan()).ToString();

    private static ReadOnlySpan<char> FirstWord(ReadOnlySpan<char> text)
    {
        var end = text.IndexOf(' ');
        return end < 0 ? text : text[..end];
    }

    // The key remains stable for the life of the show/song identity and is
    // independent of which source or file happens to supply the preview.
    private static string PerformanceId(Guid show, string key)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"{show:D}|{key}"), hash);
        return "perf_" + Convert.ToHexStringLower(hash[..12]);
    }

    private sealed class PerformanceGroup(string label, string[] aliases)
    {
        public string Label { get; } = label;
        public string[] Aliases { get; } = aliases;
        public List<FileCandidate> Files { get; } = [];
    }

    private sealed record FileCandidate(SearchFile File, float Quality);

    internal sealed record SearchFile(
        string TrackId,
        string SourceId,
        string Set,
        int SetIndex,
        int Position,
        int Duration,
        long Plays,
        bool Soundboard,
        bool Soundcheck,
        string Label
    );
}
