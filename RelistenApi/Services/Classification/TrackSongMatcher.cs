using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Relisten.Api.Models;
using Relisten.Data;

namespace Relisten.Services.Classification
{
    /// <summary>
    /// Matches source tracks to canonical setlist songs using a multi-layer pipeline:
    ///   Layer 1: Exact slug match (fast, free, ~60% of cases)
    ///   Layer 2: Fuzzy string matching with Levenshtein distance (~20% of cases)
    ///   Layer 3: LLM classification for remaining ambiguous cases (~20% of cases)
    ///
    /// Also classifies track type (song, banter, tuning, etc.) using title patterns.
    /// </summary>
    public class TrackSongMatcher
    {
        private readonly DbService _db;
        private readonly LlmClassificationService _llm;
        private readonly ILogger<TrackSongMatcher> _log;

        private const float SlugMatchConfidence = 0.95f;
        private const float FuzzyMatchThreshold = 0.75f;
        private const float FuzzyMatchConfidence = 0.80f;
        private const float LlmConfidenceThreshold = 0.6f;

        public TrackSongMatcher(
            DbService db,
            LlmClassificationService llm,
            ILogger<TrackSongMatcher> log)
        {
            _db = db;
            _llm = llm;
            _log = log;
        }

        /// <summary>
        /// Match a batch of source tracks for a given artist to their canonical songs.
        /// Returns match results for each track.
        /// </summary>
        public async Task<List<TrackMatchResult>> MatchTracksAsync(
            int artistId,
            IList<SourceTrack> tracks,
            bool allowLlm = false,
            CancellationToken ct = default)
        {
            if (tracks.Count == 0)
                return new List<TrackMatchResult>();

            // Load all canonical songs for this artist
            var songs = await LoadArtistSongs(artistId, ct);
            var songsBySlug = songs
                .GroupBy(s => s.slug)
                .ToDictionary(g => g.Key, g => g.First());
            var songsByName = songs
                .GroupBy(s => s.name.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First());

            var results = new List<TrackMatchResult>();

            foreach (var track in tracks)
            {
                var segments = TrackTitleNormalizer.NormalizeTitle(track.title);
                var trackResult = new TrackMatchResult
                {
                    TrackId = track.id,
                    OriginalTitle = track.title,
                    TrackType = segments.Count > 0 ? segments[0].TrackType : "song",
                    Matches = new List<TrackSongMatch>()
                };

                // If track is detected as non-song, skip matching
                if (trackResult.TrackType != "song")
                {
                    results.Add(trackResult);
                    continue;
                }

                foreach (var segment in segments.Where(s => s.TrackType == "song"))
                {
                    // Layer 1: Exact slug match
                    var match = TrySlugMatch(segment, songsBySlug, songsByName);
                    if (match != null)
                    {
                        trackResult.Matches.Add(match);
                        continue;
                    }

                    // Layer 2: Fuzzy match
                    match = TryFuzzyMatch(segment, songs);
                    if (match != null)
                    {
                        trackResult.Matches.Add(match);
                        continue;
                    }

                    // Layer 3: LLM match (only if allowed and we have songs to match against)
                    if (allowLlm && songs.Count > 0)
                    {
                        match = await TryLlmMatch(segment, songs, ct);
                        if (match != null)
                        {
                            trackResult.Matches.Add(match);
                            continue;
                        }
                    }

                    // No match found
                    trackResult.Matches.Add(new TrackSongMatch
                    {
                        SegmentName = segment.NormalizedName,
                        Position = segment.Position,
                        SongId = null,
                        Confidence = 0,
                        Method = "none"
                    });
                }

                // Set the primary match (highest confidence match)
                var bestMatch = trackResult.Matches
                    .Where(m => m.SongId.HasValue)
                    .OrderByDescending(m => m.Confidence)
                    .FirstOrDefault();

                if (bestMatch != null)
                {
                    trackResult.PrimaryMatchSongId = bestMatch.SongId;
                    trackResult.PrimaryMatchConfidence = bestMatch.Confidence;
                    trackResult.PrimaryMatchMethod = bestMatch.Method;
                }
                else
                {
                    // Mark as processed with no match so we don't re-process on next run
                    trackResult.PrimaryMatchMethod = "none";
                    trackResult.PrimaryMatchConfidence = 0;
                }

                results.Add(trackResult);
            }

            return results;
        }

        /// <summary>
        /// Persist match results to the database, updating source_tracks and inserting source_track_songs.
        /// </summary>
        public async Task PersistMatchResults(List<TrackMatchResult> results, CancellationToken ct = default)
        {
            if (results.Count == 0) return;

            await _db.WithWriteConnection(async con =>
            {
                foreach (var result in results)
                {
                    // Update track_type and primary match on source_tracks
                    await con.ExecuteAsync(@"
                        UPDATE source_tracks SET
                            track_type = @track_type,
                            matched_song_id = @matched_song_id,
                            match_confidence = @match_confidence,
                            match_method = @match_method
                        WHERE id = @id
                    ", new
                    {
                        id = result.TrackId,
                        track_type = result.TrackType,
                        matched_song_id = result.PrimaryMatchSongId,
                        match_confidence = result.PrimaryMatchConfidence,
                        match_method = result.PrimaryMatchMethod
                    });

                    // Insert junction records for all matches
                    foreach (var match in result.Matches.Where(m => m.SongId.HasValue))
                    {
                        await con.ExecuteAsync(@"
                            INSERT INTO source_track_songs
                                (source_track_id, setlist_song_id, confidence, method, position)
                            VALUES
                                (@source_track_id, @setlist_song_id, @confidence, @method, @position)
                            ON CONFLICT (source_track_id, setlist_song_id) DO UPDATE SET
                                confidence = EXCLUDED.confidence,
                                method = EXCLUDED.method,
                                position = EXCLUDED.position
                        ", new
                        {
                            source_track_id = result.TrackId,
                            setlist_song_id = match.SongId!.Value,
                            confidence = match.Confidence,
                            method = match.Method,
                            position = (short)match.Position
                        });
                    }
                }
            }, longTimeout: true);
        }

        #region Layer 1: Slug Match

        private TrackSongMatch? TrySlugMatch(
            NormalizedTrackSegment segment,
            Dictionary<string, SetlistSong> songsBySlug,
            Dictionary<string, SetlistSong> songsByName)
        {
            // Try slug match first
            if (songsBySlug.TryGetValue(segment.Slug, out var song))
            {
                return new TrackSongMatch
                {
                    SegmentName = segment.NormalizedName,
                    Position = segment.Position,
                    SongId = song.id,
                    SongName = song.name,
                    Confidence = SlugMatchConfidence,
                    Method = "slug"
                };
            }

            // Try exact name match (case-insensitive)
            if (songsByName.TryGetValue(segment.NormalizedName.ToLowerInvariant(), out song))
            {
                return new TrackSongMatch
                {
                    SegmentName = segment.NormalizedName,
                    Position = segment.Position,
                    SongId = song.id,
                    SongName = song.name,
                    Confidence = SlugMatchConfidence,
                    Method = "slug"
                };
            }

            return null;
        }

        #endregion

        #region Layer 2: Fuzzy Match

        private TrackSongMatch? TryFuzzyMatch(
            NormalizedTrackSegment segment,
            List<SetlistSong> songs)
        {
            SetlistSong? bestSong = null;
            float bestScore = 0;

            foreach (var song in songs)
            {
                var score = CalculateSimilarity(
                    segment.NormalizedName.ToLowerInvariant(),
                    song.name.ToLowerInvariant());

                if (score > bestScore)
                {
                    bestScore = score;
                    bestSong = song;
                }
            }

            if (bestSong != null && bestScore >= FuzzyMatchThreshold)
            {
                return new TrackSongMatch
                {
                    SegmentName = segment.NormalizedName,
                    Position = segment.Position,
                    SongId = bestSong.id,
                    SongName = bestSong.name,
                    Confidence = bestScore * FuzzyMatchConfidence,
                    Method = "fuzzy"
                };
            }

            return null;
        }

        /// <summary>
        /// Calculate normalized similarity between two strings using Levenshtein distance.
        /// Returns value between 0.0 (completely different) and 1.0 (identical).
        /// </summary>
        internal static float CalculateSimilarity(string s, string t)
        {
            if (string.Equals(s, t, StringComparison.OrdinalIgnoreCase))
                return 1.0f;

            if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(t))
                return 0.0f;

            var maxLen = Math.Max(s.Length, t.Length);
            if (maxLen == 0) return 1.0f;

            var distance = LevenshteinDistance(s, t);
            return 1.0f - (float)distance / maxLen;
        }

        private static int LevenshteinDistance(string s, string t)
        {
            var n = s.Length;
            var m = t.Length;

            // Use single-row optimization
            var prev = new int[m + 1];
            var curr = new int[m + 1];

            for (var j = 0; j <= m; j++)
                prev[j] = j;

            for (var i = 1; i <= n; i++)
            {
                curr[0] = i;
                for (var j = 1; j <= m; j++)
                {
                    var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(
                        Math.Min(curr[j - 1] + 1, prev[j] + 1),
                        prev[j - 1] + cost);
                }

                (prev, curr) = (curr, prev);
            }

            return prev[m];
        }

        #endregion

        #region Layer 3: LLM Match

        private const string TrackMatchSystemPrompt = @"You are an expert at matching live music track titles to canonical song names. Given a track title from a live recording and a list of possible songs by this artist, determine which song (if any) the track represents.

IMPORTANT CONSIDERATIONS:
- Track titles from archive.org are often abbreviated, misspelled, or formatted inconsistently
- Common abbreviations: ""GDTRFB"" = ""Goin' Down The Road Feeling Bad"", ""NFA"" = ""Not Fade Away"", ""PITB"" = ""Playing in the Band""
- Segue notation (>, ->): tracks may contain multiple songs separated by segue arrows
- ""Drums"" and ""Space"" are legitimate songs (Grateful Dead), not non-song content
- Ignore track numbers, disc numbers, and file extensions
- Some tracks are jams or improvisations that don't match any canonical song

If the track matches a song, respond with JSON:
{""match"": true, ""song_name"": ""<exact name from the list>"", ""confidence"": <0.0-1.0>, ""reasoning"": ""<brief>""}

If no match found:
{""match"": false, ""song_name"": null, ""confidence"": 0.0, ""reasoning"": ""<brief>""}";

        private async Task<TrackSongMatch?> TryLlmMatch(
            NormalizedTrackSegment segment,
            List<SetlistSong> songs,
            CancellationToken ct)
        {
            // Build a compact song list (limit to ~100 for token efficiency)
            var songNames = songs
                .Select(s => s.name)
                .OrderBy(n => n)
                .Take(200)
                .ToList();

            var userContent = $"Track title: \"{segment.NormalizedName}\"\n\nPossible songs:\n{string.Join("\n", songNames)}";

            var response = await _llm.ClassifyAsync<LlmTrackMatchResponse>(
                TrackMatchSystemPrompt,
                userContent,
                "trackmatch",
                ct);

            if (response == null || !response.match || string.IsNullOrWhiteSpace(response.song_name))
                return null;

            // Find the matched song in our list
            var matchedSong = songs.FirstOrDefault(s =>
                string.Equals(s.name, response.song_name, StringComparison.OrdinalIgnoreCase));

            if (matchedSong == null)
            {
                _log.LogWarning("LLM returned song name '{SongName}' not found in artist song list",
                    response.song_name);
                return null;
            }

            var confidence = Math.Clamp(response.confidence, 0f, 1f);
            if (confidence < LlmConfidenceThreshold)
                return null;

            return new TrackSongMatch
            {
                SegmentName = segment.NormalizedName,
                Position = segment.Position,
                SongId = matchedSong.id,
                SongName = matchedSong.name,
                Confidence = confidence,
                Method = "llm"
            };
        }

        #endregion

        #region Data Access

        private async Task<List<SetlistSong>> LoadArtistSongs(int artistId, CancellationToken ct)
        {
            return (await _db.WithConnection(async con =>
            {
                var results = await con.QueryAsync<SetlistSong>(@"
                    SELECT s.*, a.uuid as artist_uuid
                    FROM setlist_songs s
                    JOIN artists a ON a.id = s.artist_id
                    WHERE s.artist_id = @artistId
                ", new { artistId });
                return results.ToList();
            }, readOnly: true)).ToList();
        }

        #endregion
    }

    #region Models

    public class TrackMatchResult
    {
        public int TrackId { get; set; }
        public string OriginalTitle { get; set; } = "";
        public string TrackType { get; set; } = "song";
        public List<TrackSongMatch> Matches { get; set; } = new();

        /// <summary>The best single-song match for this track (stored on source_tracks).</summary>
        public int? PrimaryMatchSongId { get; set; }
        public float? PrimaryMatchConfidence { get; set; }
        public string? PrimaryMatchMethod { get; set; }
    }

    public class TrackSongMatch
    {
        public string SegmentName { get; set; } = "";
        public int Position { get; set; }
        public int? SongId { get; set; }
        public string? SongName { get; set; }
        public float Confidence { get; set; }
        public string Method { get; set; } = "none";
    }

    internal class LlmTrackMatchResponse
    {
        public bool match { get; set; }
        public string? song_name { get; set; }
        public float confidence { get; set; }
        public string? reasoning { get; set; }
    }

    #endregion
}
