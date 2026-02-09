using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Relisten.Services.Search.Models;
using StackExchange.Redis;

namespace Relisten.Services.Search
{
    /// <summary>
    /// Hybrid search combining pgvector semantic search with Postgres full-text search,
    /// merged via Reciprocal Rank Fusion (RRF).
    /// </summary>
    public class HybridSearchService
    {
        private readonly DbService _db;
        private readonly EmbeddingService _embeddings;
        private readonly RedisService _redis;
        private readonly ILogger<HybridSearchService> _log;

        public HybridSearchService(
            DbService db,
            EmbeddingService embeddings,
            RedisService redis,
            ILogger<HybridSearchService> log)
        {
            _db = db;
            _embeddings = embeddings;
            _redis = redis;
            _log = log;
        }

        public async Task<HybridSearchResponse> SearchAsync(HybridSearchRequest req, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(req.Query) || req.Query.Length < 2)
            {
                return new HybridSearchResponse { Query = req.Query };
            }

            // 1. Check result cache
            var cacheKey = $"hsearch:{req.CacheKey()}";
            var cached = await _redis.db.StringGetAsync(cacheKey);
            if (cached.HasValue)
            {
                return JsonConvert.DeserializeObject<HybridSearchResponse>(cached!)!;
            }

            // 2. Get query embedding (also cached in Redis)
            var queryEmbedding = await _embeddings.GetQueryEmbeddingAsync(req.Query, ct);

            // 3. Execute hybrid query
            var results = await ExecuteHybridQuery(req, queryEmbedding, ct);

            // 4. Build response
            var response = new HybridSearchResponse
            {
                Query = req.Query,
                TotalResults = results.Count,
                Results = results,
            };

            // 5. Cache for 5 minutes
            await _redis.db.StringSetAsync(
                cacheKey,
                JsonConvert.SerializeObject(response),
                TimeSpan.FromMinutes(5));

            return response;
        }

        private async Task<List<HybridSearchResult>> ExecuteHybridQuery(
            HybridSearchRequest req,
            string? queryEmbedding,
            CancellationToken ct)
        {
            return await _db.WithConnection(async con =>
            {
                // Use a transaction so SET LOCAL is scoped correctly
                using var tx = con.BeginTransaction();

                // Tune HNSW search quality for this query
                await con.ExecuteAsync("SET LOCAL hnsw.ef_search = 100", transaction: tx);

                var sql = BuildHybridSql(queryEmbedding != null);

                var results = await con.QueryAsync<HybridSearchResult>(sql, new
                {
                    query_embedding = queryEmbedding,
                    query_text = req.Query,
                    filter_artist_id = req.ArtistId,
                    filter_year = req.Year,
                    filter_soundboard = req.Soundboard,
                    result_limit = req.Limit,
                    result_offset = req.Offset,
                }, transaction: tx);

                tx.Commit();
                return results.ToList();
            }, readOnly: true);
        }

        /// <summary>
        /// Build the hybrid search SQL. If no embedding is available (API key not set or call failed),
        /// falls back to keyword-only search.
        /// </summary>
        private static string BuildHybridSql(bool hasEmbedding)
        {
            var semanticCte = hasEmbedding ? @"
            semantic AS (
                SELECT
                    si.source_id,
                    si.show_id,
                    ROW_NUMBER() OVER (
                        ORDER BY si.embedding <=> @query_embedding::halfvec(1536)
                    ) AS rank,
                    1 - (si.embedding <=> @query_embedding::halfvec(1536)) AS similarity
                FROM search_index si
                WHERE (@filter_artist_id::int IS NULL OR si.artist_id = @filter_artist_id)
                  AND (@filter_year::smallint IS NULL OR si.show_year = @filter_year)
                  AND (@filter_soundboard::boolean IS NULL OR si.is_soundboard = @filter_soundboard)
                  AND si.embedding IS NOT NULL
                ORDER BY si.embedding <=> @query_embedding::halfvec(1536)
                LIMIT 200
            )," : "";

            var fusionSelect = hasEmbedding
                ? @"COALESCE(sem.source_id, kw.source_id) AS source_id,
                    COALESCE(sem.show_id, kw.show_id) AS show_id,
                    COALESCE(1.0 / (60 + sem.rank), 0) +
                    COALESCE(1.0 / (60 + kw.rank), 0) AS rrf_score,
                    CASE
                        WHEN sem.source_id IS NOT NULL AND kw.source_id IS NOT NULL THEN 'both'
                        WHEN sem.source_id IS NOT NULL THEN 'semantic'
                        ELSE 'keyword'
                    END AS match_type"
                : @"kw.source_id,
                    kw.show_id,
                    1.0 / (60 + kw.rank) AS rrf_score,
                    'keyword' AS match_type";

            var fusionFrom = hasEmbedding
                ? "FROM semantic sem FULL OUTER JOIN keyword kw ON sem.source_id = kw.source_id"
                : "FROM keyword kw";

            return $@"
            WITH
            {semanticCte}
            keyword AS (
                SELECT
                    si.source_id,
                    si.show_id,
                    ROW_NUMBER() OVER (
                        ORDER BY ts_rank_cd(si.search_tsv, websearch_to_tsquery('english', @query_text), 32) DESC
                    ) AS rank,
                    ts_rank_cd(si.search_tsv, websearch_to_tsquery('english', @query_text), 32) AS text_rank
                FROM search_index si
                WHERE si.search_tsv @@ websearch_to_tsquery('english', @query_text)
                  AND (@filter_artist_id::int IS NULL OR si.artist_id = @filter_artist_id)
                  AND (@filter_year::smallint IS NULL OR si.show_year = @filter_year)
                  AND (@filter_soundboard::boolean IS NULL OR si.is_soundboard = @filter_soundboard)
                ORDER BY ts_rank_cd(si.search_tsv, websearch_to_tsquery('english', @query_text), 32) DESC
                LIMIT 200
            ),
            fused AS (
                SELECT
                    {fusionSelect}
                {fusionFrom}
            ),
            ranked AS (
                SELECT
                    f.*,
                    ROW_NUMBER() OVER (
                        PARTITION BY f.show_id
                        ORDER BY f.rrf_score DESC
                    ) AS source_rank
                FROM fused f
            )
            SELECT
                r.show_id,
                r.source_id,
                r.rrf_score AS relevance_score,
                r.match_type,
                si.artist_id,
                si.artist_name,
                si.show_date,
                si.show_year,
                si.venue_name,
                si.venue_location,
                si.tour_name,
                si.is_soundboard,
                si.avg_rating,
                si.num_reviews,
                si.taper,
                si.track_titles
            FROM ranked r
            JOIN search_index si ON r.source_id = si.source_id
            WHERE r.source_rank = 1
            ORDER BY r.rrf_score DESC
            LIMIT @result_limit OFFSET @result_offset;";
        }
    }
}
