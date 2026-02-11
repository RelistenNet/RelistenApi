using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StackExchange.Redis;

namespace Relisten.Services.Classification
{
    /// <summary>
    /// Wraps the OpenAI chat completions API for structured classification tasks.
    /// Uses GPT-4o-mini with JSON mode for cheap, fast, structured responses.
    /// </summary>
    public class LlmClassificationService
    {
        private readonly HttpClient _httpClient;
        private readonly RedisService _redis;
        private readonly ILogger<LlmClassificationService> _log;
        private const string Model = "gpt-4o-mini";

        public LlmClassificationService(
            HttpClient httpClient,
            RedisService redis,
            ILogger<LlmClassificationService> log)
        {
            _httpClient = httpClient;
            _redis = redis;
            _log = log;
        }

        /// <summary>
        /// Send a classification request to GPT-4o-mini with JSON mode.
        /// Results are cached in Redis for 30 days (classifications are stable).
        /// Returns the parsed JSON response, or null on failure.
        /// </summary>
        public async Task<T?> ClassifyAsync<T>(
            string systemPrompt,
            string userContent,
            string cachePrefix,
            CancellationToken ct = default) where T : class
        {
            // Check cache first
            var cacheKey = $"classify:{cachePrefix}:{ComputeHash(userContent)}";
            var cached = await _redis.db.StringGetAsync(cacheKey);
            if (cached.HasValue)
            {
                try
                {
                    return JsonConvert.DeserializeObject<T>(cached!);
                }
                catch
                {
                    // Cache corruption, fall through to API call
                }
            }

            try
            {
                var requestBody = new
                {
                    model = Model,
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userContent }
                    },
                    response_format = new { type = "json_object" },
                    temperature = 0.1, // Low temperature for consistent classification
                    max_tokens = 500
                };

                var json = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("chat/completions", content, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(ct);
                    _log.LogError("OpenAI classification API error {StatusCode}: {Body}",
                        response.StatusCode, errorBody);
                    return null;
                }

                var responseJson = await response.Content.ReadAsStringAsync(ct);
                var parsed = JObject.Parse(responseJson);

                var messageContent = parsed["choices"]?[0]?["message"]?["content"]?.ToString();
                if (string.IsNullOrEmpty(messageContent))
                {
                    _log.LogWarning("Empty response from classification API");
                    return null;
                }

                var result = JsonConvert.DeserializeObject<T>(messageContent);

                // Cache for 30 days - classifications are stable
                if (result != null)
                {
                    await _redis.db.StringSetAsync(cacheKey, messageContent, TimeSpan.FromDays(30));
                }

                return result;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to call OpenAI classification API");
                return null;
            }
        }

        private static string ComputeHash(string input)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes)[..32];
        }
    }
}
