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
            new SongRecord(song, artist, "Sugaree", Confidence: 0.95f),
        ]);
        await (await As(AuthorTypes.User, "u1")).WriteSongsAsync([
            new SongRecord(song, artist, "Sugaree (Jam)", Confidence: 0.95f),
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
            new SongRecord(song, artist, "Sugaree", Confidence: 0.95f),
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
            new SongRecord(a, artist, "St. Stephen", Confidence: 0.95f),
            new SongRecord(b, artist, "Saint Stephen", Confidence: 0.95f),
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
            new SongRecord(scarlet, artist, "Scarlet Begonias", Confidence: 0.95f),
            new SongRecord(fire, artist, "Fire on the Mountain", Confidence: 0.95f),
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
            new SongRecord(song, artist, "Dark Star", Confidence: 0.95f),
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

    [Test]
    public async Task APersonCanRevertARunExactly()
    {
        var (song, other) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await (await As(AuthorTypes.Importer, "bootstrap@1")).WriteSongsAsync([
            new SongRecord(song, artist, "Sugaree", Confidence: 0.95f),
        ]);
        var before = await Snapshot();

        var bad = await As(AuthorTypes.Llm, "catalogue-llm@1");
        await bad.WriteSongsAsync([
            new SongRecord(song, artist, "Sugar Magnolia", Confidence: 0.95f),
            new SongRecord(other, artist, "Deal", Confidence: 0.95f),
        ]);
        await bad.WriteAliasesAsync([new AliasRecord(artist, "Sugaree", song, 0.95f)]);

        var moderator = await As(AuthorTypes.Moderator, "m1");
        var (reverted, skipped) = await moderator.RevertRunAsync(bad.Run);

        (reverted, skipped).Should().Be((3, 0));
        (await Snapshot()).Should().Be(before);
        (
            await db.QueryAsync<string>(
                "SELECT DISTINCT author_ref FROM kb.changes WHERE run_uuid = @Run",
                new { moderator.Run },
                transaction: tx
            )
        )
            .Should()
            .Equal("m1");
    }

    [Test]
    public async Task ARevertSkipsASongLaterWorkDependsOn()
    {
        var song = Guid.CreateVersion7();
        var first = await As(AuthorTypes.Llm, "catalogue-llm@4");
        await first.WriteSongsAsync([new SongRecord(song, artist, "Sugaree", Confidence: 0.95f)]);
        await (await As(AuthorTypes.Llm, "catalogue-llm@5")).WriteAliasesAsync([
            new AliasRecord(artist, "Shugaree", song, 0.95f),
        ]);

        var (reverted, skipped) = await (await As(AuthorTypes.Moderator, "m1")).RevertRunAsync(
            first.Run
        );

        (reverted, skipped).Should().Be((0, 1));
    }

    [Test]
    public async Task ARevertSkipsARowWhoseSlugIsTakenSince()
    {
        var (song, later) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await (await As(AuthorTypes.Llm, "catalogue-llm@4")).WriteSongsAsync([
            new SongRecord(song, artist, "Deal", Confidence: 0.95f),
        ]);
        var rename = await As(AuthorTypes.Llm, "catalogue-llm@5");
        await rename.WriteSongsAsync([new SongRecord(song, artist, "Sugaree", Confidence: 0.95f)]);
        // A later song takes the slug the rename freed.
        await (await As(AuthorTypes.Llm, "catalogue-llm@5")).WriteSongsAsync([
            new SongRecord(later, artist, "Deal", Confidence: 0.95f),
        ]);

        var (reverted, skipped) = await (await As(AuthorTypes.Moderator, "m1")).RevertRunAsync(
            rename.Run
        );

        (reverted, skipped).Should().Be((0, 1));
    }

    [Test]
    public async Task ARevertLeavesRowsChangedSince()
    {
        var song = Guid.CreateVersion7();
        var bot = await As(AuthorTypes.Llm, "catalogue-llm@1");
        await bot.WriteSongsAsync([
            new SongRecord(song, artist, "Sugar Magnolia", Confidence: 0.95f),
        ]);
        await (await As(AuthorTypes.User, "u1")).WriteSongsAsync([
            new SongRecord(song, artist, "Sugaree", Confidence: 0.95f),
        ]);

        var (reverted, skipped) = await (await As(AuthorTypes.Moderator, "m1")).RevertRunAsync(
            bot.Run
        );

        (reverted, skipped).Should().Be((0, 1));
        (await NameOf(song)).Should().Be("Sugaree");
    }

    private Task<string> Snapshot() =>
        db.QuerySingleAsync<string>(
            """
            SELECT coalesce((SELECT string_agg(uuid || name, ',' ORDER BY uuid) FROM kb.songs WHERE artist_uuid = @artist), '')
                || '|' || coalesce((SELECT string_agg(normalized, ',') FROM kb.song_aliases WHERE artist_uuid = @artist), '')
            """,
            new { artist },
            tx
        );

    [Test]
    public async Task AReaskForgetsMachineDecisionsButKeepsPeoples()
    {
        var (song, track, kept) = (Guid.CreateVersion7(), Guid.NewGuid(), Guid.NewGuid());
        var bot = await As(AuthorTypes.Llm, "catalogue-llm@3");
        await bot.WriteSongsAsync([new SongRecord(song, artist, "Mellow Mood", Confidence: 0.95f)]);
        await bot.WriteAliasesAsync([new AliasRecord(artist, "Let Me Lie", song, 0.95f)]);
        await bot.WriteTrackSongsAsync([
            new TrackPartRecord(track, 1, song, "Let Me Lie", Confidence: 0.95f),
        ]);
        await (await As(AuthorTypes.User, "u1")).WriteTrackSongsAsync([
            new TrackPartRecord(kept, 1, song, "Let Me Lie"),
        ]);

        // The tracks aren't real source tracks here, so only the alias is found by artist.
        var forgotten = await (
            await As(AuthorTypes.Llm, "catalogue-llm@4")
        ).ForgetMachineDecisionsAsync(artist, ["Let Me Lie"]);

        forgotten.Aliases.Should().Be(1);
        (
            await db.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM kb.track_songs WHERE track_uuid = @kept",
                new { kept },
                tx
            )
        )
            .Should()
            .Be(1);
    }

    [Test]
    public async Task AReaskOfANumberedSpellingForgetsTheSpellingItFoldsTo()
    {
        var song = Guid.CreateVersion7();
        var bot = await As(AuthorTypes.Llm, "catalogue-llm@3");
        await bot.WriteSongsAsync([new SongRecord(song, artist, "Mellow Mood", Confidence: 0.95f)]);
        await bot.WriteAliasesAsync([new AliasRecord(artist, "Let Me Lie", song, 0.95f)]);

        var forgotten = await (
            await As(AuthorTypes.Llm, "catalogue-llm@4")
        ).ForgetMachineDecisionsAsync(artist, ["03 Let Me Lie"]);

        forgotten.Aliases.Should().Be(1);
    }

    [Test]
    public async Task FillOnlyCompletesAnOpenTrackButNotASettledOne()
    {
        var (a, b, open, settled) = (
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.NewGuid(),
            Guid.NewGuid()
        );
        var first = await As(AuthorTypes.Llm, "catalogue-llm@4");
        await first.WriteSongsAsync([
            new SongRecord(a, artist, "Funky Bitch", Confidence: 0.95f),
            new SongRecord(b, artist, "The Pendulum", Confidence: 0.95f),
        ]);
        await first.WriteTrackSongsAsync(
            [
                new TrackPartRecord(open, 1, a, "Funky Bitch", true, Confidence: 0.95f),
                new TrackPartRecord(
                    open,
                    2,
                    null,
                    "The Pendulum",
                    Confidence: 0.95f,
                    Resolved: false
                ),
                new TrackPartRecord(settled, 1, a, "Funky Bitch", Confidence: 0.95f),
            ],
            KbWriteMode.FillOnly
        );

        var later = await As(AuthorTypes.Llm, "catalogue-llm@4");
        await later.WriteTrackSongsAsync(
            [
                new TrackPartRecord(open, 1, a, "Funky Bitch", true, Confidence: 0.95f),
                new TrackPartRecord(open, 2, b, "The Pendulum", Confidence: 0.95f),
                new TrackPartRecord(settled, 1, b, "Funky Bitch", Confidence: 0.99f),
            ],
            KbWriteMode.FillOnly
        );

        (
            await db.QueryAsync<(Guid, int, Guid?, bool)>(
                "SELECT track_uuid, part, song_uuid, resolved FROM kb.track_songs WHERE track_uuid IN (@open, @settled) ORDER BY track_uuid = @settled, part",
                new { open, settled },
                tx
            )
        )
            .Should()
            .Equal(
                (open, 1, (Guid?)a, true),
                (open, 2, (Guid?)b, true),
                (settled, 1, (Guid?)a, true)
            );
    }
}
