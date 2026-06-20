using System.Net;
using System.Net.Http.Headers;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Relisten.UserApi.Models;
using Relisten.UserApi.Services;
using Relisten.UserApi.Serialization;

namespace RelistenUserApiTests;

[TestFixture]
public class UserLibraryContractTests
{
    [TestCase("/api/v3/library/users/me")]
    [TestCase("/api/v3/library/users/me/sessions")]
    [TestCase("/api/v3/library/playlists")]
    [TestCase("/api/v3/library/favorites")]
    [TestCase("/api/v3/library/settings")]
    [TestCase("/api/v3/library/sync")]
    [TestCase("/api/v3/library/history/recent")]
    public async Task AuthenticatedUserLibraryReads_ShouldDefaultToNoStore(string path)
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Test]
    public async Task UserOwnedTables_ShouldStayInUserDataSchema()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        await client.GetAsync("/health");

        await using var connection = NewDbService().CreateConnection();
        var userDataTables = (await connection.QueryAsync<string>(
            """
            SELECT tablename
            FROM pg_tables
            WHERE schemaname = 'user_data'
            """))
            .ToHashSet(StringComparer.Ordinal);
        var publicConflicts = await connection.QueryAsync<string>(
            """
            SELECT p.tablename
            FROM pg_tables p
            JOIN pg_tables u ON u.tablename = p.tablename
            WHERE p.schemaname = 'public'
              AND u.schemaname = 'user_data'
            ORDER BY p.tablename
            """);

        userDataTables.Should().Contain(ExpectedUserTables);
        publicConflicts.Should().BeEmpty();
    }

    private static async Task<AuthTokenResponse> SignIn(HttpClient client)
    {
        var response = await client.PostAsync(
            "/api/v3/library/auth/development/session",
            JsonContent(new DevelopmentSessionRequest
            {
                Username = $"contract_{Guid.NewGuid():N}"[..30],
                DisplayName = "Contract Test User",
                DeviceId = $"contract-device-{Guid.NewGuid():N}",
                DeviceName = "Contract Test Device",
                Platform = "ios"
            }));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonConvert.DeserializeObject<AuthTokenResponse>(
            body,
            UserLibraryJson.SerializerSettings)!;
    }

    private static StringContent JsonContent<T>(T value)
    {
        return new StringContent(
            JsonConvert.SerializeObject(value, UserLibraryJson.SerializerSettings),
            System.Text.Encoding.UTF8,
            "application/json");
    }

    private static WebApplicationFactory<Program> NewFactory()
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["UserData:DatabaseUrl"] = DatabaseUrl,
                        ["UserAuth:AccessTokenSigningKey"] = TestSigningKey
                    });
                });
            });
    }

    private static async Task EnsurePostgresOrIgnore()
    {
        await using var connection = NewDbService().CreateConnection();

        try
        {
            await connection.OpenAsync();
        }
        catch
        {
            Assert.Ignore("Local Postgres is not available for user-library contract integration tests.");
        }
    }

    private static UserApiDbService NewDbService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UserData:DatabaseUrl"] = DatabaseUrl
            })
            .Build();
        return new UserApiDbService(configuration);
    }

    private static readonly string[] ExpectedUserTables =
    [
        "user_service_migrations",
        "users",
        "user_auth_methods",
        "user_sessions",
        "refresh_tokens",
        "playlists",
        "playlist_blocks",
        "playlist_entries",
        "playlist_edit_log",
        "playlist_share_tokens",
        "playlist_mobile_access_grants",
        "playlist_collaborators",
        "playlist_followers",
        "user_favorites",
        "user_settings",
        "playback_history",
        "playback_history_ingest_keys",
        "playback_history_catalog_play_queue"
    ];

    private static string DatabaseUrl =>
        Environment.GetEnvironmentVariable("DATABASE_URL") ??
        "postgresql://relisten:local_dev_password@127.0.0.1:15432/relisten_db";

    private const string TestSigningKey = "test-access-token-signing-key-with-more-than-32-bytes";
}
