using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Relisten.Services.Classification
{
    /// <summary>
    /// Normalizes track titles for matching against canonical song names.
    /// Handles segue notation (>), disc/set prefixes, track numbers,
    /// common abbreviations, and non-song content detection.
    /// </summary>
    public static class TrackTitleNormalizer
    {
        // Segue arrow variants: >, ->, →, >>
        private static readonly Regex SeguePattern = new(
            @"\s*(?:->|→|>>|>)\s*",
            RegexOptions.Compiled);

        // Track number prefix: "01 ", "01. ", "1 - ", "d1t01 ", "D2T03 "
        private static readonly Regex TrackNumberPrefix = new(
            @"^(?:d?\d+t)?\d+[\.\)\-\s]+\s*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Set/disc prefix: "Set I: ", "Disc 2 - ", "E: " (encore), "S1: "
        private static readonly Regex SetPrefix = new(
            @"^(?:set\s*[IV\d]+|disc\s*\d+|e(?:ncore)?|s\d+)\s*[-:\.]\s*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Common suffixes: "(instrumental)", "(reprise)", "(jam)", "[tease]", "(>)"
        private static readonly Regex CommonSuffixes = new(
            @"\s*[\(\[](?:instrumental|reprise|jam|tease|>|cont(?:inued)?\.?|ending|start|finish|intro|outro|cut|incomplete|partial|snippet|fake|aborted)[\)\]]\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // File extension suffix
        private static readonly Regex FileExtension = new(
            @"\.(?:mp3|flac|ogg|wav|shn|m4a)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Non-song title patterns
        private static readonly Regex NonSongPatterns = new(
            @"^(?:banter|crowd|tuning|stage\s*banter|audience|applause|unknown|track\s*\d+|soundcheck|sound\s*check|intro(?:duction)?|encore\s*break|set\s*break|intermission|tape\s*flip|tape\s*change|(?:pre|post)[\s-]?show|drums?[\s/]+space)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Whitespace normalization
        private static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);

        /// <summary>
        /// Normalize a track title to a list of potential song names.
        /// Splits on segue notation and normalizes each segment.
        /// Returns empty list if the track is detected as a non-song.
        /// </summary>
        public static List<NormalizedTrackSegment> NormalizeTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return new List<NormalizedTrackSegment>();

            // Remove file extensions
            var cleaned = FileExtension.Replace(title, "");

            // Remove track number prefix
            cleaned = TrackNumberPrefix.Replace(cleaned, "");

            // Remove set/disc prefix
            cleaned = SetPrefix.Replace(cleaned, "");

            // Split on segue notation
            var segments = SeguePattern.Split(cleaned);

            var results = new List<NormalizedTrackSegment>();

            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i].Trim();
                if (string.IsNullOrWhiteSpace(segment)) continue;

                // Remove common suffixes
                segment = CommonSuffixes.Replace(segment, "");

                // Normalize whitespace
                segment = MultiSpace.Replace(segment, " ").Trim();

                if (string.IsNullOrWhiteSpace(segment)) continue;

                // Check if this is a non-song
                var trackType = DetectTrackType(segment);

                results.Add(new NormalizedTrackSegment
                {
                    OriginalTitle = title,
                    NormalizedName = segment,
                    Position = i,
                    IsSegue = segments.Length > 1,
                    TrackType = trackType,
                    Slug = Relisten.Import.SlugUtils.Slugify(segment)
                });
            }

            return results;
        }

        /// <summary>
        /// Detect if a track title represents a non-song segment.
        /// Returns the track type.
        /// </summary>
        public static string DetectTrackType(string normalizedName)
        {
            if (NonSongPatterns.IsMatch(normalizedName))
            {
                var lower = normalizedName.ToLowerInvariant();
                if (lower.Contains("banter") || lower.Contains("stage")) return "banter";
                if (lower.Contains("tuning")) return "tuning";
                if (lower.Contains("crowd") || lower.Contains("applause") || lower.Contains("audience")) return "crowd";
                if (lower.Contains("soundcheck") || lower.Contains("sound check")) return "soundcheck";
                if (lower.Contains("intro")) return "intro";
                if (lower.Contains("drums") || lower.Contains("space")) return "jam";
                return "unknown";
            }

            return "song";
        }

        /// <summary>
        /// Build a slug suitable for matching against SetlistSong.slug.
        /// This is the same slugification used in the import pipeline.
        /// </summary>
        public static string BuildMatchSlug(string name)
        {
            return Relisten.Import.SlugUtils.Slugify(name);
        }
    }

    public class NormalizedTrackSegment
    {
        /// <summary>The original track title before normalization.</summary>
        public string OriginalTitle { get; set; } = "";

        /// <summary>The cleaned/normalized song name.</summary>
        public string NormalizedName { get; set; } = "";

        /// <summary>Position within a segued track (0-indexed).</summary>
        public int Position { get; set; }

        /// <summary>Whether this segment was part of a segue chain.</summary>
        public bool IsSegue { get; set; }

        /// <summary>Detected track type: "song", "banter", "tuning", etc.</summary>
        public string TrackType { get; set; } = "song";

        /// <summary>Slug for matching against SetlistSong.slug.</summary>
        public string Slug { get; set; } = "";
    }
}
