using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Relisten.Accounts.Contracts.Accounts;
using RelistenUserService.Identity.Usernames;
using RelistenUserService.Persistence;

namespace RelistenUserServiceTests;

[TestFixture]
[NonParallelizable]
public sealed class TestUsernameCommandIntegration
{
    private readonly PostgresIntegrationDatabase _database = new();

    [OneTimeSetUp]
    public Task SetUp() => _database.StartAsync();

    [OneTimeTearDown]
    public Task TearDown() => _database.StopAsync();

    [Test]
    public async Task Concurrent_commands_with_the_same_version_allow_exactly_one_rename()
    {
        var userId = await _database.CreateUserAsync(UniqueUsername("initial"));
        await using var firstContext = _database.CreateContext();
        await using var secondContext = _database.CreateContext();

        // The authorization handler tracks the user before the controller invokes the command.
        // Load both copies first so this test exercises that request-level tracking behavior.
        await firstContext.Users.SingleAsync(user => user.Id == userId);
        await secondContext.Users.SingleAsync(user => user.Id == userId);

        var firstUsername = UniqueUsername("first");
        var secondUsername = UniqueUsername("second");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = ExecuteAfterAsync(
            start.Task,
            firstContext,
            userId,
            new UpdateUsernameRequest(1, Guid.CreateVersion7(), 1, firstUsername));
        var second = ExecuteAfterAsync(
            start.Task,
            secondContext,
            userId,
            new UpdateUsernameRequest(1, Guid.CreateVersion7(), 1, secondUsername));

        start.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        results.Should().ContainSingle(result => result.Status == UsernameCommandStatus.Success);
        var stale = results.Should()
            .ContainSingle(result => result.Status == UsernameCommandStatus.UsernameVersionStale)
            .Which;
        stale.User.UsernameVersion.Should().Be(2);

        await using var verificationContext = _database.CreateContext();
        var storedUser = await verificationContext.Users.SingleAsync(user => user.Id == userId);
        storedUser.Username.Should().BeOneOf(firstUsername, secondUsername);
        storedUser.Username.Should().Be(
            results.Single(result => result.Status == UsernameCommandStatus.Success).User.Username);
        storedUser.UsernameVersion.Should().Be(2);
        (await verificationContext.UsernameCommandReceipts.CountAsync(
            receipt => receipt.UserId == userId)).Should().Be(1);
    }

    private static async Task<UsernameCommandResult> ExecuteAfterAsync(
        Task start,
        AccountsDbContext dbContext,
        Guid userId,
        UpdateUsernameRequest request)
    {
        await start;
        var policy = new UsernamePolicy();
        var advisoryLocks = new AdvisoryLockService(dbContext);
        var service = new UsernameCommandService(
            dbContext,
            advisoryLocks,
            new UsernameReservationService(dbContext, advisoryLocks, policy),
            policy,
            TimeProvider.System);
        return await service.ExecuteAsync(userId, request, CancellationToken.None);
    }

    private static string UniqueUsername(string prefix) =>
        $"{prefix}_{Guid.NewGuid():N}"[..30];
}
