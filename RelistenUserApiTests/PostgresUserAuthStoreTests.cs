using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Relisten.UserApi.Models;
using Relisten.UserApi.Services;

namespace RelistenUserApiTests;

[TestFixture]
public class PostgresUserAuthStoreTests
{
    [Test]
    public async Task RotateRefreshToken_ShouldRotateAtomicallyAndRejectSecondRotation()
    {
        var store = await NewStoreOrIgnore();
        var now = DateTimeOffset.UtcNow;
        var username = UniqueUsername();
        var (user, authMethod) = await store.CreateUserWithProvider(
            "google",
            $"google-subject-{username}",
            username,
            "SQL Test User",
            now);
        var session = await store.CreateSession(
            user.UserUuid,
            new DeviceDescriptor($"device-{username}", "SQL Device", "ios"),
            now);
        var firstToken = new RefreshToken(
            $"{username}-selector.{username}-secret",
            $"{username}-selector",
            $"{username}-hash",
            now,
            now.AddDays(1));
        var firstRecord = await store.AddRefreshToken(session.SessionUuid, firstToken);
        var replacementToken = new RefreshToken(
            $"{username}-replacement-selector.{username}-replacement-secret",
            $"{username}-replacement-selector",
            $"{username}-replacement-hash",
            now,
            now.AddDays(1));

        var replacement = await store.RotateRefreshToken(
            firstRecord.RefreshTokenUuid,
            session.SessionUuid,
            replacementToken,
            now.AddMinutes(1));
        var secondAttempt = await store.RotateRefreshToken(
            firstRecord.RefreshTokenUuid,
            session.SessionUuid,
            new RefreshToken(
                $"{username}-second-selector.{username}-second-secret",
                $"{username}-second-selector",
                $"{username}-second-hash",
                now,
                now.AddDays(1)),
            now.AddMinutes(2));
        var rotated = await store.FindRefreshTokenBySelector(firstToken.Selector);

        replacement.Should().NotBeNull();
        replacement!.Selector.Should().Be(replacementToken.Selector);
        UuidTestAssertions.ShouldBeUuidV7(user.UserUuid);
        UuidTestAssertions.ShouldBeUuidV7(authMethod.AuthMethodUuid);
        UuidTestAssertions.ShouldBeUuidV7(session.SessionUuid);
        UuidTestAssertions.ShouldBeUuidV7(firstRecord.RefreshTokenUuid);
        UuidTestAssertions.ShouldBeUuidV7(replacement.RefreshTokenUuid);
        secondAttempt.Should().BeNull();
        rotated!.Status.Should().Be(RefreshTokenStatus.Rotated);
        rotated.ReplacedByTokenUuid.Should().Be(replacement.RefreshTokenUuid);
    }

    [Test]
    public async Task ListSessions_ShouldOmitRevokedSessions()
    {
        var store = await NewStoreOrIgnore();
        var now = DateTimeOffset.UtcNow;
        var username = UniqueUsername();
        var (user, _) = await store.CreateUserWithProvider(
            "apple",
            $"apple-subject-{username}",
            username,
            "SQL Test User",
            now);
        var first = await store.CreateSession(
            user.UserUuid,
            new DeviceDescriptor($"first-{username}", "First", "ios"),
            now);
        var second = await store.CreateSession(
            user.UserUuid,
            new DeviceDescriptor($"second-{username}", "Second", "ios"),
            now.AddMinutes(1));

        await store.RevokeSession(user.UserUuid, first.SessionUuid, now.AddMinutes(2));
        var sessions = await store.ListSessions(user.UserUuid);

        sessions.Select(session => session.SessionUuid)
            .Should()
            .BeEquivalentTo([second.SessionUuid]);
    }

    private static async Task<PostgresUserAuthStore> NewStoreOrIgnore()
    {
        var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(databaseUrl))
        {
            databaseUrl = "postgresql://relisten:local_dev_password@127.0.0.1:15432/relisten_db";
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UserData:DatabaseUrl"] = databaseUrl
            })
            .Build();
        var db = new UserApiDbService(configuration);

        try
        {
            await new UserDataSchemaInitializer(configuration, db).Initialize();
        }
        catch
        {
            Assert.Ignore("Local Postgres is not available for PostgresUserAuthStore integration tests.");
        }

        return new PostgresUserAuthStore(db);
    }

    private static string UniqueUsername()
    {
        return $"sql_{Guid.NewGuid():N}"[..30];
    }
}
