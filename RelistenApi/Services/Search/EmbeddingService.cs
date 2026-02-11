using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StackExchange.Redis;

namespace Relisten.Services.Search
{
    /// <summary>
    /// Calls OpenAI's embedding API and caches results in Redis.
    /// Embeddings are passed to Postgres as string literals and cast to halfvec in SQL.
    /// </summary>
    public class EmbeddingService
    {
        private readonly HttpClient _httpClient;
        private readonly RedisService _redis;
        private readonly ILogger<EmbeddingService> _log;
        private const string Model = "text-embedding-3-small";
        private const int Dimensions = 1536;

        public EmbeddingService(HttpClient httpClient, RedisService redis, ILogger<EmbeddingService> log)
        {
            _httpClient = httpClient;
            _redis = redis;
            _log = log;
        }

        /// <summary>
        /// Get embedding for a single query string. Cached in Redis for 24 hours.
        /// Returns a pgvector-formatted string like "[0.1,0.2,...]" ready for SQL casting.
        /// Returns null if the API key is not configured or the call fails.
        /// </summary>
        public async Task<string?> GetQueryEmbeddingAsync(string text, CancellationToken ct = default)
        {
            var cacheKey = $"emb:v1:{ComputeHash(text)}";

            var cached = await _redis.db.StringGetAsync(cacheKey);
            if (cached.HasValue)
            {
                return cached.ToString();
            }

            var embeddings = await CallEmbeddingApiAsync(new[] { text }, ct);
            if (embeddings == null || embeddings.Count == 0)
                return null;

            var vectorStr = FormatVector(embeddings[0]);

            await _redis.db.StringSetAsync(cacheKey, vectorStr, TimeSpan.FromHours(24));

            return vectorStr;
        }

        /// <summary>
        /// Batch embed for the indexing pipeline. No caching (each text is unique).
        /// Returns pgvector-formatted strings, one per input text.
        /// </summary>
        public async Task<List<string?>> GetBatchEmbeddingsAsync(List<string> texts, CancellationToken ct = default)
        {
            if (texts.Count == 0) return new List<string?>();

            var embeddings = await CallEmbeddingApiAsync(texts, ct);
            if (embeddings == null)
                return texts.Select(_ => (string?)null).ToList();

            return embeddings.Select(e => (string?)FormatVector(e)).ToList();
        }

        /// <summary>
        /// Format a float array as a pgvector string literal: "[0.1,0.2,...]"
        /// This can be cast to vector or halfvec in SQL.
        /// </summary>
        public static string FormatVector(float[] vector)
        {
            var sb = new StringBuilder(vector.Length * 10);
            sb.Append('[');
            for (var i = 0; i < vector.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(vector[i].ToString("G", CultureInfo.InvariantCulture));
            }
            sb.Append(']');
            return sb.ToString();
        }

        private async Task<List<float[]>?> CallEmbeddingApiAsync(IEnumerable<string> texts, CancellationToken ct)
        {
            try
            {
                var requestBody = new
                {
                    model = Model,
                    input = texts.ToArray(),
                    dimensions = Dimensions
                };

                var json = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("embeddings", content, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(ct);
                    _log.LogError("OpenAI embedding API error {StatusCode}: {Body}",
                        response.StatusCode, errorBody);
                    return null;
                }

                var responseJson = await response.Content.ReadAsStringAsync(ct);
                var parsed = JObject.Parse(responseJson);

                var data = parsed["data"] as JArray;
                if (data == null) return null;

                return data
                    .OrderBy(d => (int)d["index"]!)
                    .Select(d => d["embedding"]!.ToObject<float[]>()!)
                    .ToList();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to call OpenAI embedding API");
                return null;
            }
        }

        private static string ComputeHash(string input)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes)[..32];
        }
    }
}
