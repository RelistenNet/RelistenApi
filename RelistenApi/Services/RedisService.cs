using System;
using System.Linq;
using System.Net;
using StackExchange.Redis;

namespace Relisten
{
    public class RedisService
    {
        public ConnectionMultiplexer connection { get; }

        public IDatabase db { get; }

        public RedisService(ConfigurationOptions opts)
        {
            connection = ConnectionMultiplexer.Connect(opts);
            db = connection.GetDatabase();
        }

        public static ConfigurationOptions BuildConfiguration(string redis_url)
        {
            var redisURL = new Uri(redis_url);

            //because of https://github.com/dotnet/corefx/issues/8768
            var dnsTask = Dns.GetHostAddressesAsync(redisURL.Host);
            var addresses = dnsTask.Result;
            var connect = string.Join(",", addresses.Select(x => x.MapToIPv4().ToString() + ":" + redisURL.Port));

            var configurationOptions = ConfigurationOptions.Parse($"{connect},syncTimeout=10000");

            if (redisURL.UserInfo != null && redisURL.UserInfo.Contains(":"))
            {
                configurationOptions.Password = redisURL.UserInfo.Split(':')[1];
            }

            return configurationOptions;
        }
    }
}
