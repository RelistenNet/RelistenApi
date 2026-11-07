using System.Text.Json;
using Dapper;
using FluentAssertions;
using Npgsql;

namespace RelistenApiTests.Normalization;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestScopedSongMatches
{
    private const string Legacy = "norm.song_recording_matches(@artist,@songs::text[])";
    private const string Scoped =
        "norm.song_recording_matches(@artist,@songs::text[],@shows::uuid[])";
    private static readonly Guid Artist = Guid.Parse("00000000-0000-0000-0000-000000000301");
    private static readonly Guid Show = Guid.Parse("00000000-0000-0000-0000-000000000311");
    private MigrationTestDatabase database = null!;
    private string[] beforeMigration = null!;

    [OneTimeSetUp]
    public async Task Upgrade_populated_catalog()
    {
        MigratedTestDatabase.RequireLocalDatabase();
        database =
            await MigrationTestDatabase.CreateBeforeAsync<Migrations.ScopedSongRecordingMatches>();
        foreach (var name in new[] { "catalog.sql", "songs.sql" })
        {
            var sql = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "v4", name)
            );
            await database.Connection.ExecuteAsync(sql);
        }

        beforeMigration = await Matches(Legacy);
        beforeMigration.Should().HaveCount(4);
        database.ApplyMigration();
    }

    [OneTimeTearDown]
    public async Task Drop_catalog()
    {
        if (database is not null)
        {
            await database.DisposeAsync();
        }
    }

    [Test]
    public async Task Migration_preserves_legacy_rows_and_scopes_only_requested_shows()
    {
        (await Matches(Legacy)).Should().Equal(beforeMigration);
        (await Matches("norm.song_recording_matches(@artist)")).Should().Equal(beforeMigration);
        (await Matches(Scoped)).Should().Equal(beforeMigration);
        (await Matches(Scoped, [])).Should().BeEmpty();

        foreach (var show in new[] { Show, Guid.Parse("00000000-0000-0000-0000-000000000312") })
        {
            var expected = beforeMigration.Where(row =>
            {
                using var json = JsonDocument.Parse(row);
                return json.RootElement.GetProperty("show_uuid").GetGuid() == show;
            });
            (await Matches(Scoped, [show])).Should().Equal(expected);
        }
    }

    [Test]
    public async Task Scoped_matches_keep_the_full_artist_map_and_validate_sibling_parts()
    {
        string[] songs = ["song_00000101"];
        var original = await Matches(Scoped, [Show], songs);
        original
            .Select(Track)
            .Should()
            .Equal(
                Guid.Parse("00000000-0000-0000-0000-000000000341"),
                Guid.Parse("00000000-0000-0000-0000-000000000342")
            );
        using var multiSong = JsonDocument.Parse(original[0]);
        multiSong
            .RootElement.GetProperty("matching_part_ordinals")
            .EnumerateArray()
            .Select(part => part.GetInt32())
            .Should()
            .Equal(1, 3);

        // Fire is an unrequested sibling in file 341. Its valid interpretation must
        // survive the song filter, and its broken merge must invalidate the whole file.
        await using var transaction = await database.Connection.BeginTransactionAsync();
        await database.Connection.ExecuteAsync(
            """
            SET LOCAL norm.author_type='importer';
            SET LOCAL norm.author_ref='v4-review';
            SET LOCAL norm.run_uuid='00000000-0000-0000-0000-000000000099';
            UPDATE norm.songs SET merged_into='song_00000103' WHERE id='song_00000102';
            UPDATE norm.songs SET merged_into='song_00000102' WHERE id='song_00000103';
            """,
            transaction: transaction
        );
        var broken = await Matches(Scoped, [Show], songs, transaction);
        broken.Select(Track).Should().Equal(Guid.Parse("00000000-0000-0000-0000-000000000342"));
    }

    private async Task<string[]> Matches(
        string call,
        Guid[]? shows = null,
        string[]? songs = null,
        NpgsqlTransaction? transaction = null
    ) =>
        (
            await database.Connection.QueryAsync<string>(
                $"SELECT row_to_json(m)::text FROM {call} m ORDER BY m.song_id,m.track_uuid",
                new
                {
                    artist = Artist,
                    songs,
                    shows,
                },
                transaction
            )
        ).ToArray();

    private static Guid Track(string row)
    {
        using var json = JsonDocument.Parse(row);
        return json.RootElement.GetProperty("track_uuid").GetGuid();
    }
}
