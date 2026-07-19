using FluentAssertions;
using Npgsql;
using NUnit.Framework;
using Relisten.Accounts.Contracts.Library;
using RelistenUserService.Library;

namespace RelistenUserServiceTests;

[TestFixture]
[NonParallelizable]
public sealed class TestCatalogAvailabilityValidatorIntegration
{
    private readonly PostgresIntegrationDatabase _database = new();

    [OneTimeSetUp]
    public Task SetUp() => _database.StartAsync();

    [OneTimeTearDown]
    public Task TearDown() => _database.StopAsync();

    [Test]
    public async Task Availability_matches_the_resolver_for_every_favorite_type()
    {
        var scenario = await SeedCatalogScenarioAsync();
        await using var dbContext = _database.CreateContext();
        var validator = new CatalogAvailabilityValidator(dbContext);

        var unavailable = await validator.FindUnavailableAsync(
            scenario.Available.Concat(scenario.Unavailable).ToArray(),
            CancellationToken.None);

        unavailable.Should().BeEquivalentTo(scenario.Unavailable);
    }

    private async Task<CatalogScenario> SeedCatalogScenarioAsync()
    {
        var available = CatalogUuids.Create();
        var unavailable = CatalogUuids.Create();
        var flacOnlyTrackUuid = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(SeedCatalogSql, connection);
        AddCatalogParameters(command, "available", available);
        AddCatalogParameters(command, "unavailable", unavailable);
        command.Parameters.AddWithValue("flac_only_track_uuid", flacOnlyTrackUuid);
        await command.ExecuteNonQueryAsync();

        return new(
            available.ToReferences()
                .Append(new(FavoriteCatalogTypes.SourceTrack, flacOnlyTrackUuid))
                .ToArray(),
            unavailable.ToReferences());
    }

    private static void AddCatalogParameters(
        NpgsqlCommand command,
        string prefix,
        CatalogUuids catalog)
    {
        command.Parameters.AddWithValue($"{prefix}_artist_uuid", catalog.Artist);
        command.Parameters.AddWithValue($"{prefix}_show_uuid", catalog.Show);
        command.Parameters.AddWithValue($"{prefix}_source_uuid", catalog.Source);
        command.Parameters.AddWithValue($"{prefix}_track_uuid", catalog.SourceTrack);
        command.Parameters.AddWithValue($"{prefix}_song_uuid", catalog.Song);
        command.Parameters.AddWithValue($"{prefix}_tour_uuid", catalog.Tour);
        command.Parameters.AddWithValue($"{prefix}_venue_uuid", catalog.Venue);
    }

    private sealed record CatalogScenario(
        IReadOnlyList<CatalogReference> Available,
        IReadOnlyList<CatalogReference> Unavailable);

    private sealed record CatalogUuids(
        Guid Artist,
        Guid Show,
        Guid Source,
        Guid SourceTrack,
        Guid Song,
        Guid Tour,
        Guid Venue)
    {
        public static CatalogUuids Create() => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());

        public CatalogReference[] ToReferences() =>
        [
            new(FavoriteCatalogTypes.Artist, Artist),
            new(FavoriteCatalogTypes.Show, Show),
            new(FavoriteCatalogTypes.Source, Source),
            new(FavoriteCatalogTypes.SourceTrack, SourceTrack),
            new(FavoriteCatalogTypes.Song, Song),
            new(FavoriteCatalogTypes.Tour, Tour),
            new(FavoriteCatalogTypes.Venue, Venue)
        ];
    }

    private const string SeedCatalogSql = """
        WITH available_artist AS (
            INSERT INTO artists (uuid)
            VALUES (@available_artist_uuid)
            RETURNING id
        ), available_feature AS (
            INSERT INTO features (artist_id)
            SELECT id FROM available_artist
        ), available_year AS (
            INSERT INTO years (artist_id, year)
            SELECT id, '2024' FROM available_artist
            RETURNING id, artist_id
        ), available_show AS (
            INSERT INTO shows (uuid, artist_id, year_id, date)
            SELECT @available_show_uuid, artist_id, id, DATE '2024-01-01'
            FROM available_year
            RETURNING id, artist_id
        ), available_info AS (
            INSERT INTO show_source_information (show_id)
            SELECT id FROM available_show
        ), available_source AS (
            INSERT INTO sources (uuid, artist_id, show_id)
            SELECT @available_source_uuid, artist_id, id FROM available_show
            RETURNING id, artist_id
        ), available_set AS (
            INSERT INTO source_sets (uuid, source_id)
            SELECT gen_random_uuid(), id FROM available_source
            RETURNING id, source_id
        ), available_track AS (
            INSERT INTO source_tracks (
                uuid, source_id, source_set_id, mp3_url, flac_url, is_orphaned)
            SELECT
                @available_track_uuid,
                source_id,
                id,
                'https://media.example/track.mp3',
                NULL,
                false
            FROM available_set
        ), available_song AS (
            INSERT INTO setlist_songs (uuid, artist_id)
            SELECT @available_song_uuid, id FROM available_artist
        ), available_tour AS (
            INSERT INTO tours (uuid, artist_id, start_date, end_date)
            SELECT
                @available_tour_uuid,
                id,
                DATE '2024-01-01',
                DATE '2024-12-31'
            FROM available_artist
        ), available_venue AS (
            INSERT INTO venues (
                uuid, artist_id, name, location, upstream_identifier, slug)
            SELECT
                @available_venue_uuid,
                id,
                'The Venue',
                'Somewhere, USA',
                'venue-1',
                'the-venue'
            FROM available_artist
        ), unavailable_artist AS (
            INSERT INTO artists (uuid)
            VALUES (@unavailable_artist_uuid)
            RETURNING id
        ), unavailable_year AS (
            INSERT INTO years (artist_id, year)
            SELECT id, '2024' FROM available_artist
            RETURNING id, artist_id
        ), unavailable_show AS (
            INSERT INTO shows (uuid, artist_id, year_id, date)
            SELECT @unavailable_show_uuid, artist_id, id, DATE '2024-01-02'
            FROM unavailable_year
            RETURNING id, artist_id
        ), unavailable_source AS (
            INSERT INTO sources (uuid, artist_id, show_id)
            SELECT @unavailable_source_uuid, artist_id, id FROM unavailable_show
        ), unavailable_track AS (
            INSERT INTO source_tracks (
                uuid, source_id, source_set_id, mp3_url, flac_url, is_orphaned)
            SELECT
                @unavailable_track_uuid,
                source_id,
                id,
                'https://media.example/orphan.mp3',
                NULL,
                true
            FROM available_set
        ), flac_only_track AS (
            INSERT INTO source_tracks (
                uuid, source_id, source_set_id, mp3_url, flac_url, is_orphaned)
            SELECT
                @flac_only_track_uuid,
                source_id,
                id,
                NULL,
                'https://media.example/track.flac',
                false
            FROM available_set
        ), unavailable_song AS (
            INSERT INTO setlist_songs (uuid, artist_id)
            SELECT @unavailable_song_uuid, id FROM unavailable_artist
        ), unavailable_tour AS (
            INSERT INTO tours (uuid, artist_id, start_date, end_date)
            SELECT @unavailable_tour_uuid, id, DATE '2024-01-01', NULL
            FROM available_artist
        ), unavailable_venue AS (
            INSERT INTO venues (
                uuid, artist_id, name, location, upstream_identifier, slug)
            SELECT
                @unavailable_venue_uuid,
                id,
                'Broken Venue',
                'Somewhere, USA',
                'venue-2',
                NULL
            FROM available_artist
        )
        SELECT 1;
        """;
}
