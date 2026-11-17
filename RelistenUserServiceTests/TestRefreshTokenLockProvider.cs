using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RelistenUserService.Persistence;

namespace RelistenUserServiceTests;

[TestFixture]
[NonParallelizable]
public sealed class TestRefreshTokenLockProvider
{
    private readonly PostgresIntegrationDatabase _database = new();

    [OneTimeSetUp]
    public Task Start() => _database.StartAsync();

    [OneTimeTearDown]
    public Task Stop() => _database.StopAsync();

    [TestCase(null, 16)]
    [TestCase("1", 1)]
    public async Task Pool_limits_concurrent_leases_and_recovers_after_cancellation(
        string? configuredLimit,
        int expectedCapacity
    )
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ACCOUNTS_LOCK_DATABASE_URL"] = _database.ConnectionString,
                    ["ACCOUNTS_REFRESH_MAX_POOL_SIZE"] = configuredLimit,
                }
            )
            .Build();
        await using var provider = new RefreshTokenLockProvider(
            configuration,
            null!,
            NullLogger<RefreshTokenLockProvider>.Instance
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var leases = new List<IAsyncDisposable>();
        try
        {
            // Distinct tokens must not serialize behind the former global limit of two.
            for (var index = 0; index < expectedCapacity; index++)
            {
                leases.Add(await provider.AcquireAsync($"token-{index}", timeout.Token));
            }

            // Exhaustion must wait for capacity and honor cancellation, without losing a slot.
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            var acquireWhenFull = async () =>
            {
                await using var unexpected = await provider.AcquireAsync(
                    "overflow",
                    cancelled.Token
                );
            };
            await acquireWhenFull.Should().ThrowAsync<OperationCanceledException>();

            await leases[0].DisposeAsync();
            leases.RemoveAt(0);
            await using var recovered = await provider.AcquireAsync("token-0", timeout.Token);
        }
        finally
        {
            foreach (var lease in leases)
            {
                await lease.DisposeAsync();
            }
        }
    }
}
