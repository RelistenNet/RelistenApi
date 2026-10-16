using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Relisten;
using Relisten.Ingestion;
using StackExchange.Redis;
using Temporalio.Client;
using Temporalio.Converters;

namespace RelistenApiTests.Ingestion;

/// <summary>Checks payload routing through the converter installed on the public Temporal client.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public class TestTemporalPayloadRedisRegistration
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task LargePayloadUsesConfiguredStoreOrSharedCache(bool dedicatedStore)
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Run ./test-local.sh to include local integration tests.");
        }

        var fallback = new RedisService(
            new ConfigurationOptions { EndPoints = { "127.0.0.1:16379" }, DefaultDatabase = 15 }
        );
        using var fallbackConnection = fallback.connection;
        var settings = new Dictionary<string, string?>
        {
            ["Temporal:Namespace"] = "registration-test-" + Guid.NewGuid().ToString("N"),
        };
        if (dedicatedStore)
        {
            settings["Temporal:PayloadRedisUrl"] = "redis://127.0.0.1:16379";
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(fallback);
        services.AddTemporalIngestionClient(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build()
        );
        using (var provider = services.BuildServiceProvider())
        {
            var converter = provider.GetRequiredService<ITemporalClient>().Options.DataConverter;
            var value = new string('x', 300_000);
            var payloads = await converter.ToPayloadsAsync([value]);
            using var reference = JsonDocument.Parse(payloads.Single().Data.Memory);
            var key = reference.RootElement.GetProperty("key").GetString()!;
            var selected = fallbackConnection.GetDatabase(dedicatedStore ? 0 : 15);
            var other = fallbackConnection.GetDatabase(dedicatedStore ? 15 : 0);

            try
            {
                (await selected.KeyExistsAsync(key)).Should().BeTrue();
                (await other.KeyExistsAsync(key)).Should().BeFalse();
                (await converter.ToSingleValueAsync<string>(payloads)).Should().Be(value);
            }
            finally
            {
                await selected.KeyDeleteAsync(key);
            }
        }

        (await fallback.db.PingAsync()).Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }
}
