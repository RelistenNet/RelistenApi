using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Hangfire.Console;
using Hangfire.Server;
using Microsoft.Extensions.Logging;

namespace Relisten.Services.Search
{
    /// <summary>
    /// Builds and maintains the search_index table.
    /// Called as a Hangfire recurring job to find stale/missing entries and re-index them.
    /// </summary>
    public class SearchIndexingService
    {
        private readonly DbService _db;
        private readonly EmbeddingService _embeddings;
        private readonly ILogger<SearchIndexingService> _log;

        private const int EmbedBatchSize = 100;
        private const string EmbeddingModel = "text-embedding-3-small";

        public SearchIndexingService(
            DbService db,
            EmbeddingService embeddings,
            ILogger<SearchIndexingService> log)
        {
            _db = db;
            _embeddings = embeddings;
            _log = log;
        }

        /// <summary>
        /// Main entry point: find all sources that need (re-)indexing and process them.
        /// </summary>
        public async Task IndexStaleSourcesAsync(PerformContext? context, CancellationToken ct = default)
        {
            var stopwatch = Stopwatch.StartNew();
            var unindexed = await GetUnindexedSources(ct);

            var message = $"Found {unindexed.Count} sources to index";
            _log.LogInformation(message);
            context?.WriteLine(message);

            if (unindexed.Count == 0) return;

            var processed = 0;
            var errors = 0;

            foreach (var batch in unindexed.Chunk(EmbedBatchSize))
            {
                try
                {
                    await ProcessBatch(batch, ct);
                }
                catch (Exception ex)
                {
                    errors++;
                    _log.LogError(ex, "Error processing batch starting at source {SourceId}", batch[0].source_id);
                    context?.WriteLine($"Error processing batch: {ex.Message}");
                }

                processed += batch.Length;

                if (processed % 1000 == 0 || processed == unindexed.Count)
                {
                    var progress = $"Progress: {processed}/{unindexed.Count} ({errors} errors)";
                    _log.LogInformation(progress);
                    context?.WriteLine(progress);
                }

                // Rate limiting: stay under OpenAI 3,000 RPM limit
                if (batch.Length == EmbedBatchSize)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
                }
            }

            stopwatch.Stop();
            var summary = $"Indexing complete: {processed} sources in {stopwatch.Elapsed.TotalSeconds:0.#}s ({errors} errors)";
            _log.LogInformation(summary);
            context?.WriteLine(summary);
        }

        private async Task ProcessBatch(SourceRow[] batch, CancellationToken ct)
        {
            // 1. Build search text and embedding text for each source
            var docs = batch.Select(src =>
            {
                var searchText = SearchTextBuilder.BuildSearchText(
                    src.description, src.taper_notes, src.source_field,
                    src.taper, src.transferrer, src.lineage, src.review_text);

                var embeddingText = SearchTextBuilder.BuildEmbeddingText(
                    src.artist_name, src.show_date, src.venue_name,
                    src.venue_location, src.tour_name, src.track_titles, searchText);

                return new IndexDocument
                {
                    source_id = src.source_id,
                    show_id = src.show_id,
                    artist_id = src.artist_id,
                    artist_name = src.artist_name,
                    show_date = src.show_date,
                    show_year = src.show_year,
                    venue_name = src.venue_name,
                    venue_location = src.venue_location,
                    tour_name = src.tour_name,
                    is_soundboard = src.is_soundboard,
                    recording_type = src.recording_type ?? "unknown",
                    avg_rating = src.avg_rating,
                    num_reviews = src.num_reviews,
                    taper = src.taper,
                    track_titles = src.track_titles,
                    search_text = searchText,
                    embedding_text = SearchTextBuilder.TruncateForEmbedding(embeddingText),
                };
            }).ToList();

            // 2. Batch embed non-empty texts
            var textsToEmbed = docs
                .Select(d => d.embedding_text)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            List<string?> embeddings = new();
            if (textsToEmbed.Count > 0)
            {
                embeddings = await _embeddings.GetBatchEmbeddingsAsync(textsToEmbed, ct);
            }

            // 3. Attach embeddings to docs
            var embIdx = 0;
            foreach (var doc in docs)
            {
                if (!string.IsNullOrWhiteSpace(doc.embedding_text) && embIdx < embeddings.Count)
                {
                    doc.embedding = embeddings[embIdx];
                    embIdx++;
                }
            }

            // 4. Upsert to search_index
            await UpsertBatch(docs, ct);
        }

        private async Task UpsertBatch(List<IndexDocument> docs, CancellationToken ct)
        {
            await _db.WithWriteConnection(async con =>
            {
                foreach (var doc in docs)
                {
                    await con.ExecuteAsync(UpsertSql, new
                    {
                        doc.source_id,
                        doc.show_id,
                        doc.artist_id,
                        doc.artist_name,
                        doc.show_date,
                        doc.show_year,
                        doc.venue_name,
                        doc.venue_location,
                        doc.tour_name,
                        doc.is_soundboard,
                        doc.recording_type,
                        doc.avg_rating,
                        doc.num_reviews,
                        doc.taper,
                        doc.track_titles,
                        doc.search_text,
                        doc.embedding,
                        indexed_at = DateTime.UtcNow,
                        embedding_model = doc.embedding != null ? EmbeddingModel : (string?)null,
                    });
                }
            }, longTimeout: true);
        }

        private async Task<List<SourceRow>> GetUnindexedSources(CancellationToken ct)
        {
            return await _db.WithConnection(async con =>
            {
                var results = await con.QueryAsync<SourceRow>(UnindexedSourcesSql,
                    commandTimeout: 300);
                return results.ToList();
            }, longTimeout: true, readOnly: true);
        }

        private const string UpsertSql = @"
            INSERT INTO search_index (
                source_id, show_id, artist_id, artist_name,
                show_date, show_year, venue_name, venue_location, tour_name,
                is_soundboard, recording_type, avg_rating, num_reviews, taper,
                track_titles, search_text,
                embedding,
                indexed_at, embedding_model
            ) VALUES (
                @source_id, @show_id, @artist_id, @artist_name,
                @show_date, @show_year, @venue_name, @venue_location, @tour_name,
                @is_soundboard, @recording_type, @avg_rating, @num_reviews, @taper,
                @track_titles, @search_text,
                CASE WHEN @embedding IS NULL THEN NULL ELSE @embedding::halfvec(1536) END,
                @indexed_at, @embedding_model
            )
            ON CONFLICT (source_id) DO UPDATE SET
                show_id = EXCLUDED.show_id,
                artist_id = EXCLUDED.artist_id,
                artist_name = EXCLUDED.artist_name,
                show_date = EXCLUDED.show_date,
                show_year = EXCLUDED.show_year,
                venue_name = EXCLUDED.venue_name,
                venue_location = EXCLUDED.venue_location,
                tour_name = EXCLUDED.tour_name,
                is_soundboard = EXCLUDED.is_soundboard,
                recording_type = EXCLUDED.recording_type,
                avg_rating = EXCLUDED.avg_rating,
                num_reviews = EXCLUDED.num_reviews,
                taper = EXCLUDED.taper,
                track_titles = EXCLUDED.track_titles,
                search_text = EXCLUDED.search_text,
                embedding = EXCLUDED.embedding,
                indexed_at = EXCLUDED.indexed_at,
                embedding_model = EXCLUDED.embedding_model;";

        private const string UnindexedSourcesSql = @"
            SELECT
                src.id AS source_id,
                src.show_id,
                s.artist_id,
                a.name AS artist_name,
                s.date AS show_date,
                EXTRACT(YEAR FROM s.date)::smallint AS show_year,
                v.name AS venue_name,
                v.location AS venue_location,
                t.name AS tour_name,
                src.is_soundboard,
                src.recording_type,
                src.avg_rating,
                src.num_reviews,
                src.taper,
                src.description,
                src.taper_notes,
                src.source AS source_field,
                src.transferrer,
                src.lineage,
                (
                    SELECT string_agg(DISTINCT st.title, ' | ' ORDER BY st.title)
                    FROM source_tracks st
                    WHERE st.source_id = src.id
                ) AS track_titles,
                (
                    SELECT string_agg(
                        coalesce(sr.title, '') || ' ' || coalesce(sr.review, ''), ' '
                    )
                    FROM source_reviews sr
                    WHERE sr.source_id = src.id
                ) AS review_text
            FROM sources src
            JOIN shows s ON src.show_id = s.id
            JOIN artists a ON s.artist_id = a.id
            LEFT JOIN venues v ON s.venue_id = v.id
            LEFT JOIN tours t ON s.tour_id = t.id
            LEFT JOIN search_index si ON si.source_id = src.id
            WHERE si.source_id IS NULL
               OR src.updated_at > si.indexed_at
            ORDER BY src.id";

        // Internal models for the indexing pipeline

        internal class SourceRow
        {
            public long source_id { get; set; }
            public long show_id { get; set; }
            public int artist_id { get; set; }
            public string artist_name { get; set; } = "";
            public DateTime? show_date { get; set; }
            public short? show_year { get; set; }
            public string? venue_name { get; set; }
            public string? venue_location { get; set; }
            public string? tour_name { get; set; }
            public bool is_soundboard { get; set; }
            public string? recording_type { get; set; }
            public double avg_rating { get; set; }
            public int num_reviews { get; set; }
            public string? taper { get; set; }
            public string? description { get; set; }
            public string? taper_notes { get; set; }
            public string? source_field { get; set; }
            public string? transferrer { get; set; }
            public string? lineage { get; set; }
            public string? track_titles { get; set; }
            public string? review_text { get; set; }
        }

        internal class IndexDocument
        {
            public long source_id { get; set; }
            public long show_id { get; set; }
            public int artist_id { get; set; }
            public string artist_name { get; set; } = "";
            public DateTime? show_date { get; set; }
            public short? show_year { get; set; }
            public string? venue_name { get; set; }
            public string? venue_location { get; set; }
            public string? tour_name { get; set; }
            public bool is_soundboard { get; set; }
            public string recording_type { get; set; } = "unknown";
            public double avg_rating { get; set; }
            public int num_reviews { get; set; }
            public string? taper { get; set; }
            public string? track_titles { get; set; }
            public string search_text { get; set; } = "";
            public string embedding_text { get; set; } = "";
            public string? embedding { get; set; }
        }
    }
}
