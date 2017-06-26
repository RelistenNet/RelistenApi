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
    }
}
