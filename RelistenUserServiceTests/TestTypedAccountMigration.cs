using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;
using OpenIddict.EntityFrameworkCore.Models;
using Relisten.Identifiers;
using RelistenUserService.Identity;
using RelistenUserService.Library;
using RelistenUserService.Persistence;

namespace RelistenUserServiceTests;

/// <summary>Exercises the real pre-cutover schema and rows, not a second implementation of migration rules.</summary>
[TestFixture]
[NonParallelizable]
public sealed class TestTypedAccountMigration
{
    private readonly PostgresIntegrationDatabase _database = new();

    private static Guid OldId(int n) => Guid.Parse($"019a0000-0000-7000-8000-{n:000000000000}");

    [OneTimeSetUp]
    public async Task SetUp()
    {
        await _database.StartAsync("20260827052217_AddDurableBrowserSessions");
        await using var db = _database.CreateContext();
        await using (var connection = new NpgsqlConnection(_database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = new NpgsqlCommand(
                await File.ReadAllTextAsync(
                    Path.Combine(
                        TestContext.CurrentContext.TestDirectory,
                        "Fixtures/pre-typed-accounts.sql"
                    )
                ),
                connection
            );
            await seed.ExecuteNonQueryAsync();
        }
        await db.Database.MigrateAsync();
    }

    [OneTimeTearDown]
    public Task TearDown() => _database.StopAsync();

    [Test]
    public async Task Preserves_identity_membership_and_audit_with_native_text_keys()
    {
        await using var db = _database.CreateContext();
        var user = await db.Users.SingleAsync();
        user.Id.Should().Be(UserId.FromUuid(OldId(1)));
        user.Username.Should().Be("c2_listener");
        user.UsernameVersion.Should().Be(3);
        user.CreatedAt.Should().Be(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        user.UpdatedAt.Should().Be(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var provider = await db.ExternalIdentities.SingleAsync();
        provider.Id.Should().Be(OldId(2));
        provider.UserId.Should().Be(user.Id);
        provider.Issuer.Should().Be("https://accounts.google.com");
        provider.ProviderSubject.Should().Be("dev-google-alice");
        (await db.UsernameHolds.SingleAsync()).UserId.Should().Be(user.Id);
        var command = await db.UsernameCommandReceipts.SingleAsync();
        command.Id.Should().Be(CommandId.FromUuid(OldId(9)));
        command.PayloadHash.Should().Equal(Enumerable.Repeat((byte)0x12, 32));
        using var stored = JsonDocument.Parse(command.StoredResult);
        stored.RootElement.GetProperty("UsernameVersion").GetInt64().Should().Be(3);

        var favorites = await db.Favorites.OrderBy(f => f.Id).ToArrayAsync();
        favorites.Should().HaveCount(7);
        var kinds = new[]
        {
            "artist",
            "show",
            "source",
            "track",
            "imported_song",
            "tour",
            "imported_venue",
        };
        var targetUuid = Guid.Parse("777f627f-442e-9a6f-a366-ac734827dcc5");
        for (var i = 0; i < favorites.Length; i++)
        {
            favorites[i].Id.Should().Be(FavoriteId.FromUuid(OldId(101 + i)));
            favorites[i].UserId.Should().Be(user.Id);
            favorites[i].CatalogType.Should().Be(kinds[i]);
            favorites[i].CatalogId.Should().Be(UuidTypeId.Format(kinds[i], targetUuid));
        }
        var receipt = await db.FavoriteMutationReceipts.SingleAsync();
        receipt.Id.Should().Be(MutationId.FromUuid(OldId(201)));
        receipt.SubmittedFavoriteId.Should().Be(favorites[^1].Id);
        receipt.CanonicalFavoriteId.Should().Be(favorites[^1].Id);
        receipt.PayloadHash.Should().Equal(Enumerable.Repeat((byte)0x34, 32));
        receipt.LibraryRevision.Should().Be(4);
        var change = await db.LibraryChanges.SingleAsync();
        change.Id.Should().Be(LibraryChangeId.FromUuid(OldId(301)));
        change.FavoriteId.Should().Be(favorites[^1].Id);
        change.CatalogId.Should().Be(favorites[^1].CatalogId);
        (await db.LibraryStates.SingleAsync()).Revision.Should().Be(4);

        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand(
            """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema IN ('identity','user_data') AND data_type <> 'text'
                AND (column_name = 'user_id' OR
                    (column_name = 'id' AND table_name IN ('users','sessions','native_sessions',
                        'username_command_receipts','favorites','favorite_mutation_receipts','library_changes'))
                    OR column_name IN ('catalog_id','favorite_id','submitted_favorite_id','canonical_favorite_id','auth_sso_session_id'))
            """,
            connection
        );
        (await query.ExecuteScalarAsync()).Should().Be(0L);
    }

    [Test]
    public async Task Revokes_old_credentials_and_preserves_framework_keys_and_same_user_sso_fk()
    {
        await using var db = _database.CreateContext();
        var native = await db.NativeSessions.SingleAsync();
        native.Id.Should().Be(SessionId.FromUuid(OldId(5)));
        native.AuthorizationId.Should().Be(OldId(3));
        native.RevokedAt.Should().NotBeNull();
        var browser = await db.Sessions.OrderBy(s => s.Id).ToArrayAsync();
        browser.Should().OnlyContain(s => s.RevokedAt != null);
        browser[1].AuthSsoSessionId.Should().Be(browser[0].Id);
        var authorization = await db.Set<OpenIddictEntityFrameworkCoreAuthorization<Guid>>()
            .SingleAsync();
        authorization.Id.Should().Be(OldId(3));
        authorization.Subject.Should().Be(native.UserId.Value);
        authorization.Status.Should().Be("revoked");
        var token = await db.Set<OpenIddictEntityFrameworkCoreToken<Guid>>().SingleAsync();
        token.Id.Should().Be(OldId(4));
        token.Subject.Should().Be(native.UserId.Value);
        token.Status.Should().Be("revoked");
        token.Payload.Should().Be("opaque-pre-cutover-payload");

        // A real write must still reject an SSO parent from another account after type conversion.
        var otherUser = await _database.CreateUserAsync("c2_other");
        var action = () =>
            db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE identity.sessions SET user_id = {otherUser.Value} WHERE id = {browser[
                    1
                ].Id.Value}
                """
            );
        (await action.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23503");
    }
}
