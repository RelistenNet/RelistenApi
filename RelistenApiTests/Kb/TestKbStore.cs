using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Kb;

namespace RelistenApiTests.Kb;

[Category("LocalIngestion")]
public class TestKbStore
{
    private NpgsqlConnection db = null!;
    private NpgsqlTransaction tx = null!;
    private readonly Guid artist = Guid.NewGuid();

    [SetUp]
    public async Task Open()
    {
        MigratedTestDatabase.RequireLocalDatabase();
        db = await MigratedTestDatabase.OpenAsync();
        tx = await db.BeginTransactionAsync();
    }

    [TearDown]
    public async Task Close()
    {
        if (db != null)
        {
            await tx.RollbackAsync();
            await db.DisposeAsync();
        }
    }

    // Each store is its own run; a later one resets the transaction's author.
    private Task<KbStore> As(string authorType, string authorRef) =>
        KbStore.BeginAsync(db, tx, authorType, authorRef);

    private Task<string> NameOf(Guid song) =>
        db.QuerySingleAsync<string>(
            "SELECT name FROM kb.songs WHERE uuid = @song",
            new { song },
            tx
        );

    private Task<long> Changes() =>
        db.ExecuteScalarAsync<long>("SELECT count(*) FROM kb.changes", transaction: tx);

    [Test]
    public async Task ReRunningABotChangesAndLogsNothing()
    {
        var song = Guid.CreateVersion7();
        var setlistSong = Guid.NewGuid();
        async Task<int> Bootstrap()
        {
            var bot = await As(AuthorTypes.Llm, "catalogue-llm@1+model");
            return await bot.WriteSongsAsync([
                    new SongRecord(song, artist, "One More Saturday Night", Confidence: 0.95f),
                ])
                + await bot.WriteAliasesAsync([new AliasRecord(artist, "OMSN", song, 0.95f)])
                + await bot.WriteEvidenceAsync([new EvidenceRecord(setlistSong, song, 0.95f)]);
        }

        (await Bootstrap()).Should().Be(3);
        var logged = await Changes();

        (await Bootstrap()).Should().Be(0);
        (await Changes()).Should().Be(logged);
    }

    [Test]
    public async Task AMachineLeavesAPersonsEditAlone()
    {
        var song = Guid.CreateVersion7();
        await (await As(AuthorTypes.Importer, "bootstrap@1")).WriteSongsAsync([
            new SongRecord(song, artist, "Sugaree"),
        ]);
        await (await As(AuthorTypes.User, "u1")).WriteSongsAsync([
            new SongRecord(song, artist, "Sugaree (Jam)"),
        ]);

        var written = await (await As(AuthorTypes.Algorithm, "matcher@1")).WriteSongsAsync([
            new SongRecord(song, artist, "Sugaree", Confidence: 0.99f),
        ]);

        written.Should().Be(0);
        (await NameOf(song)).Should().Be("Sugaree (Jam)");
    }

    [Test]
    public async Task TheTriggerRefusesAMachineThatBypassesTheStore()
    {
        var song = Guid.CreateVersion7();
        await (await As(AuthorTypes.User, "u1")).WriteSongsAsync([
            new SongRecord(song, artist, "Sugaree"),
        ]);
        await As(AuthorTypes.Algorithm, "matcher@1");

        var delete = () =>
            db.ExecuteAsync("DELETE FROM kb.songs WHERE uuid = @song", new { song }, tx);

        await delete.Should().ThrowAsync<PostgresException>().WithMessage("*set by a person*");
    }

    [Test]
    public async Task FillOnlyNeverChangesARow()
    {
        var song = Guid.CreateVersion7();
        await (await As(AuthorTypes.Llm, "catalogue-llm@1")).WriteSongsAsync([
            new SongRecord(song, artist, "Sugaree", Confidence: 0.95f),
        ]);

        var written = await (await As(AuthorTypes.Llm, "catalogue-llm@2")).WriteSongsAsync(
            [new SongRecord(song, artist, "Sugaree Jam", Confidence: 0.97f)],
            KbWriteMode.FillOnly
        );

        written.Should().Be(0);
        (await NameOf(song)).Should().Be("Sugaree");
    }

    [Test]
    public async Task SlugsAreUniquePerArtist()
    {
        var (a, b) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await (await As(AuthorTypes.Importer, "bootstrap@1")).WriteSongsAsync([
            new SongRecord(a, artist, "St. Stephen"),
            new SongRecord(b, artist, "Saint Stephen"),
        ]);

        (
            await db.QueryAsync<string>(
                "SELECT slug FROM kb.songs WHERE artist_uuid = @artist ORDER BY uuid",
                new { artist },
                tx
            )
        )
            .Should()
            .Equal("saint-stephen", "saint-stephen-2");
    }

    [Test]
    public async Task ATracksPartsAreReplacedTogetherAndLogged()
    {
        var (scarlet, fire, track) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.NewGuid());
        var bot = await As(AuthorTypes.Algorithm, "title-matcher@1");
        await bot.WriteSongsAsync([
            new SongRecord(scarlet, artist, "Scarlet Begonias"),
            new SongRecord(fire, artist, "Fire on the Mountain"),
        ]);
        await bot.WriteTrackSongsAsync([
            new TrackPartRecord(
                track,
                1,
                scarlet,
                "Scarlet Begonias",
                SegueAfter: true,
                Confidence: 0.95f
            ),
            new TrackPartRecord(track, 2, fire, "Fire", Qualifiers: ["partial"], Confidence: 0.95f),
            new TrackPartRecord(track, 3, null, "crowd", Confidence: 0.95f),
        ]);

        await (await As(AuthorTypes.Algorithm, "title-matcher@2")).WriteTrackSongsAsync([
            new TrackPartRecord(
                track,
                1,
                scarlet,
                "Scarlet Begonias",
                SegueAfter: true,
                Confidence: 0.95f
            ),
            new TrackPartRecord(track, 2, fire, "Fire", Qualifiers: ["partial"], Confidence: 0.95f),
        ]);

        (
            await db.QueryAsync<(int, string)>(
                "SELECT part, array_to_string(qualifiers, ',') FROM kb.track_songs WHERE track_uuid = @track ORDER BY part",
                new { track },
                tx
            )
        )
            .Should()
            .Equal((1, ""), (2, "partial"));
        (
            await db.QueryAsync<string>(
                "SELECT op FROM kb.changes WHERE table_name = 'track_songs' AND row_key ->> 'track_uuid' = @t ORDER BY id",
                new { t = track.ToString() },
                tx
            )
        )
            .Should()
            .Equal("insert", "insert", "insert", "delete");
    }

    [Test]
    public async Task APersonsTrackIsLeftToThem()
    {
        var (song, track) = (Guid.CreateVersion7(), Guid.NewGuid());
        await (await As(AuthorTypes.User, "u1")).WriteSongsAsync([
            new SongRecord(song, artist, "Dark Star"),
        ]);
        await (await As(AuthorTypes.User, "u1")).WriteTrackSongsAsync([
            new TrackPartRecord(track, 1, song, "Dark Star"),
        ]);

        var written = await (
            await As(AuthorTypes.Algorithm, "title-matcher@1")
        ).WriteTrackSongsAsync([
            new TrackPartRecord(track, 1, null, "Dark Star Jam", Confidence: 0.99f),
        ]);

        written.Should().Be(0);
    }
}
