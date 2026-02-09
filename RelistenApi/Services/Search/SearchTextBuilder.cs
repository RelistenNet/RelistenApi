using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Relisten.Services.Search
{
    /// <summary>
    /// Builds clean text from raw source data for FTS indexing and embedding.
    /// Source descriptions contain raw HTML that must be stripped before use.
    /// </summary>
    public static class SearchTextBuilder
    {
        private static readonly Regex HtmlTags = new(@"<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);

        /// <summary>
        /// Strip HTML tags and decode entities from source descriptions.
        /// </summary>
        public static string CleanHtml(string? html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";

            var text = HtmlTags.Replace(html, " ");
            text = System.Net.WebUtility.HtmlDecode(text);
            text = MultiSpace.Replace(text, " ");
            return text.Trim();
        }

        /// <summary>
        /// Build the search_text column value: cleaned description + notes + source metadata.
        /// This is stored in the DB and used as tsvector weight D.
        /// Does NOT include artist/venue/tracks (those have their own weighted tsvector entries).
        /// </summary>
        public static string BuildSearchText(
            string? sourceDescription,
            string? taperNotes,
            string? source,
            string? taper,
            string? transferrer,
            string? lineage,
            string? reviewText)
        {
            var parts = new List<string>();

            var cleanDesc = CleanHtml(sourceDescription);
            if (!string.IsNullOrEmpty(cleanDesc)) parts.Add(cleanDesc);

            var cleanNotes = CleanHtml(taperNotes);
            if (!string.IsNullOrEmpty(cleanNotes)) parts.Add(cleanNotes);

            if (!string.IsNullOrEmpty(source)) parts.Add($"Source: {source}");
            if (!string.IsNullOrEmpty(taper)) parts.Add($"Taper: {taper}");
            if (!string.IsNullOrEmpty(transferrer)) parts.Add($"Transfer: {transferrer}");
            if (!string.IsNullOrEmpty(lineage)) parts.Add($"Lineage: {lineage}");

            if (!string.IsNullOrEmpty(reviewText)) parts.Add(reviewText);

            return string.Join(" ", parts);
        }

        /// <summary>
        /// Build the full text used as input to the embedding model.
        /// Includes all fields (artist, venue, date, tracks, description, reviews)
        /// because embedding models benefit from full context.
        /// High-value fields come first since embedding models weight early tokens more.
        /// This is NOT stored — only used to generate the embedding vector.
        /// </summary>
        public static string BuildEmbeddingText(
            string artistName,
            DateTime? showDate,
            string? venueName,
            string? venueLocation,
            string? tourName,
            string? trackTitles,
            string searchText)
        {
            var parts = new List<string>();

            // High-value fields first
            if (!string.IsNullOrEmpty(artistName)) parts.Add(artistName);
            if (showDate.HasValue)
            {
                parts.Add(showDate.Value.ToString("yyyy-MM-dd"));
                parts.Add(showDate.Value.ToString("MMMM d, yyyy"));
            }
            if (!string.IsNullOrEmpty(venueName)) parts.Add(venueName);
            if (!string.IsNullOrEmpty(venueLocation)) parts.Add(venueLocation);
            if (!string.IsNullOrEmpty(tourName)) parts.Add(tourName);
            if (!string.IsNullOrEmpty(trackTitles)) parts.Add(trackTitles);

            // Description/notes/reviews last
            if (!string.IsNullOrEmpty(searchText)) parts.Add(searchText);

            return string.Join(" ", parts);
        }

        /// <summary>
        /// Truncate to stay within embedding model token limits.
        /// text-embedding-3-small supports 8,191 tokens.
        /// Rough estimate: 1 token ~ 4 chars for English.
        /// Target 7,500 tokens (~30,000 chars) to leave headroom.
        /// </summary>
        public static string TruncateForEmbedding(string text, int maxChars = 30_000)
        {
            if (text.Length <= maxChars) return text;
            return text[..maxChars];
        }
    }
}
