using System;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Relisten.Services.Popularity
{
    public class PopularityCacheEntry<T>
    {
        public DateTime generated_at { get; set; }
        public int stale_after_seconds { get; set; }
        public T data { get; set; } = default!;
    }

    public class PopularityCacheResult<T>
    {
        public bool HasValue { get; set; }
        public bool IsStale { get; set; }
        public PopularityCacheEntry<T>? Entry { get; set; }
    }

    public class PopularityCacheHeader
    {
        public DateTime generated_at { get; set; }
        public int stale_after_seconds { get; set; }
    }

    public class PopularityCacheHeaderResult
    {
        public bool HasValue { get; set; }
        public bool IsStale { get; set; }
        public PopularityCacheHeader? Header { get; set; }
    }

    public class PopularityCacheService
    {
        private readonly RedisService redisService;

        public PopularityCacheService(RedisService redisService)
        {
            this.redisService = redisService;
        }

        public async Task<PopularityCacheResult<T>> GetAsync<T>(string key)
        {
            var cached = await redisService.db.StringGetAsync(key);
            if (!cached.HasValue || cached.IsNullOrEmpty)
            {
                return new PopularityCacheResult<T> { HasValue = false };
            }

            var entry = JsonConvert.DeserializeObject<PopularityCacheEntry<T>>(cached!);
            if (entry == null)
            {
                return new PopularityCacheResult<T> { HasValue = false };
            }

            var isStale = DateTime.UtcNow - entry.generated_at >
                          TimeSpan.FromSeconds(entry.stale_after_seconds);

            return new PopularityCacheResult<T>
            {
                HasValue = true,
                IsStale = isStale,
                Entry = entry
            };
        }

        public Task SetAsync<T>(string key, PopularityCacheEntry<T> entry)
        {
            var json = JsonConvert.SerializeObject(entry);
            return redisService.db.StringSetAsync(key, json);
        }

        public async Task<PopularityCacheHeaderResult> GetHeaderAsync(string key)
        {
            var cached = await redisService.db.StringGetAsync(key);
            if (!cached.HasValue || cached.IsNullOrEmpty)
            {
                return new PopularityCacheHeaderResult { HasValue = false };
            }

            var header = JsonConvert.DeserializeObject<PopularityCacheHeader>(cached!);
            if (header == null)
            {
                return new PopularityCacheHeaderResult { HasValue = false };
            }

            var isStale = DateTime.UtcNow - header.generated_at >
                          TimeSpan.FromSeconds(header.stale_after_seconds);

            return new PopularityCacheHeaderResult
            {
                HasValue = true,
                IsStale = isStale,
                Header = header
            };
        }
    }
}
