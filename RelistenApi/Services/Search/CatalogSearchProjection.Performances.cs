using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Api.Models.Api.V4;
using Relisten.Identifiers;
using Relisten.Services.Popularity;

namespace Relisten.Search;

using static SearchText;
using Document = Dictionary<string, object>;

internal sealed partial class CatalogSearchProjection
{
    // Keep full shows together so grouping, reviews and preview selection see
    // every copy. Once a batch's raw rows are read, the same snapshot can load
    // full v4 facts for just its selected preview files.
    private const int ShowBatchSize = 1_000;
    private const int TrackDisplayBatchSize = 5_000;

    public async IAsyncEnumerable<Document> Performances(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        bool calibrate = false
    )
    {
        var mappings = (await _rows.Mappings(cancellationToken)).ToLookup(
            row => row.TrackUuid,
            row => row.SongId
        );

        var batches = _shows.Keys.Order().Chunk(ShowBatchSize).ToArray();
        var ranks = calibrate ? await PerformanceRanks(batches, mappings, cancellationToken) : null;
        foreach (var showIds in batches)
        {
            var documents = await BuildPerformances(showIds, mappings, cancellationToken);
            await foreach (var document in AttachPreviews(documents, cancellationToken))
            {
                yield return WithPercentile(document, ranks);
            }
        }
    }

    private async Task<IReadOnlyDictionary<double, double>> PerformanceRanks(
        long[][] batches,
        ILookup<Guid, string> mappings,
        CancellationToken cancellationToken
    )
    {
        var histogram = new SearchPercentiles();
        foreach (var showIds in batches)
        {
            // Count the same show/song groups as the document projection,
            // including groups without plays. Only keep one batch of totals.
            var totals = new Dictionary<(long Show, string Song), long>();
            foreach (var track in await _rows.RankingTracks(showIds, cancellationToken))
            {
                var plays = _plays.GetValueOrDefault(track.Uuid).Plays;
                foreach (var song in PerformanceSongs(RawTitle(track.Title), mappings[track.Uuid]))
                {
                    var key = (track.ShowId, song);
                    totals[key] = totals.GetValueOrDefault(key) + plays;
                }
            }
            foreach (var total in totals.Values)
            {
                histogram.Observe("performance", SignalBucket(total));
            }
        }

        return histogram.Calculate().GetValueOrDefault("performance") ?? [];
    }

    private async Task<List<Document>> BuildPerformances(
        long[] showIds,
        ILookup<Guid, string> mappings,
        CancellationToken cancellationToken
    )
    {
        var imports = (await _rows.ImportedSongsAtShows(showIds, cancellationToken)).ToLookup(
            song => (song.ShowId, Title: Fold(song.Name))
        );
        var popularity = await _resources.PerformancePopularity(
            showIds.Select(id => _shows[id].Row.Uuid).ToArray(),
            cancellationToken
        );
        var tracks = await _rows.Tracks(showIds, cancellationToken);
        var documents = new List<Document>();

        foreach (var atShow in tracks.GroupBy(track => track.ShowId))
        {
            var show = _shows[atShow.Key];
            var groups = new Dictionary<string, PerformanceGroup>(StringComparer.Ordinal);
            foreach (var track in atShow)
            {
                CollectTrack(show, groups, track, mappings[track.Uuid]);
            }

            documents.AddRange(FinishShow(show, groups, imports, popularity));
        }

        return documents;
    }

    private async IAsyncEnumerable<Document> AttachPreviews(
        IEnumerable<Document> documents,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        foreach (var batch in documents.Chunk(TrackDisplayBatchSize))
        {
            var selected = batch
                .Select(document => (Document: document, File: (SearchFile)document["preview"]))
                .Select(row => (row.Document, Uuid: UuidTypeId.Parse(row.File.TrackId, "track")))
                .ToArray();
            var tracks = await _resources.Tracks(
                selected.Select(row => row.Uuid).Distinct().ToArray(),
                _approvedArtists,
                cancellationToken
            );
            foreach (var (document, uuid) in selected)
            {
                document["display"] = CatalogSearchResourceProjection.Serialize(
                    V4CatalogProjection.Track(tracks[uuid])
                );
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
            track.Title,
            track.Slug
        );

        var raw = RawTitle(track.Title);
        foreach (var key in PerformanceSongs(raw, mappedSongs))
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

    private static IEnumerable<string> PerformanceSongs(
        string rawTitle,
        IEnumerable<string> mappedSongs
    ) =>
        mappedSongs
            .Distinct(StringComparer.Ordinal)
            .DefaultIfEmpty(PopularityPerformanceKey.RawTitle(Fold(rawTitle)));

    private IEnumerable<Document> FinishShow(
        ShowContext show,
        Dictionary<string, PerformanceGroup> groups,
        ILookup<(long ShowId, string Title), SearchProjectionRows.ImportedSongMembership> imports,
        IReadOnlyDictionary<(Guid Show, string Song), PopularityMetrics> popularity
    )
    {
        var mentions = ReviewMentions(show.Reviews, groups.Values);
        show.Reviews.Clear();
        foreach (var (key, group) in groups)
        {
            var files = group.Files.Select(candidate => candidate.File).ToArray();
            var count = mentions.GetValueOrDefault(group);
            var plays = files.Sum(file => file.Plays);
            var rawTitle = PopularityPerformanceKey.FoldedRawTitle(key);
            var playbackKey = PopularityPerformanceKey.ForStorage(key);
            var playback = popularity.GetValueOrDefault((show.Row.Uuid, playbackKey));
            var imported = rawTitle == null ? null : UniqueImport(imports[(show.Row.Id, rawTitle)]);

            var label = group.Label.Length == 0 ? "Untitled track" : group.Label;

            var preview = Preview(group.Files);
            var document = new Document(show.Fields)
            {
                ["id"] = PerformanceId(show.Row.Uuid, key),
                ["kind"] = "performance",
                ["label"] = label,
                ["name"] = Fold(label),
                ["aliases"] = Join(group.Aliases),
                ["song_id"] = rawTitle == null ? key : "",
                ["preview"] = preview,
                ["preview_track_id"] = preview?.TrackId ?? PerformanceId(show.Row.Uuid, key),
                ["soundboard"] = files.Any(file => file.Soundboard),
                ["soundcheck"] = files.All(file => file.Soundcheck),
                ["longest"] = files.Max(file => file.Duration),
                ["track_plays"] = plays,
                ["ranking_signal"] = plays,
                ["review_mentions"] = count,
                ["popularity"] = Math.Round(Math.Log(1d + plays) + 0.25 * Math.Log(1d + count), 5),
            };

            // Raw title equality alone cannot establish song identity. The
            // imported title must also have unambiguous membership in this show.
            if (rawTitle == null || imported is not null)
            {
                var id = imported is null ? key : UuidTypeId.Format("imported_song", imported.Uuid);
                document["display_song_id"] = id;
                document["display_song_title"] = imported?.Name ?? label;
            }
            CatalogSearchResourceProjection.AttachPopularity(document, playback);
            yield return document;
        }
    }

    private static SearchProjectionRows.ImportedSongMembership? UniqueImport(
        IEnumerable<SearchProjectionRows.ImportedSongMembership> matches
    )
    {
        var candidates = matches.Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static SearchFile Preview(List<FileCandidate> candidates)
    {
        // When plays tie, keep the recording we would have picked without that
        // signal if it is one of the tied candidates.
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
        string Label,
        // The track's URL slug within its recording. Documents indexed before
        // this field existed deserialize it as null until the next rebuild.
        string? Slug
    );
}
