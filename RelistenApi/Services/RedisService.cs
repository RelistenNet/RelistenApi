using StackExchange.Redis;

namespace Relisten
{
    public class RedisService
    {
        public ConnectionMultiplexer connection { get; }

        public IDatabase db { get; }

        public RedisService(string url)
        {
            connection = ConnectionMultiplexer.Connect(url);
            db = connection.GetDatabase();
        }
    }
}
