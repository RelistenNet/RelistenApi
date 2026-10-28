using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Relisten;
using Relisten.Ingestion;
using Relisten.Ingestion.Workflows;
using Relisten.Normalization;
using Relisten.Normalization.Songs;
using Relisten.Normalization.Temporal;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace RelistenApiTests.Normalization;

/// A model that answers from the request alone: every group becomes one song named after its
/// first spelling, except "mystery jam" (an unnamed jam) and "unknown thing" (unsure).
internal sealed class ScriptedModel : ICatalogueModel
{
    public int Calls { get; private set; }

    public string ModelId => "scripted-1";

    public Task<ModelReply> CompleteAsync(
        string instructions,
        string data,
        string schemaName,
        string schemaJson,
        CancellationToken cancellationToken
    )
    {
        Calls++;
        var request = JsonNode.Parse(data)!;
        var known = request["known_songs"]!
            .AsArray()
            .ToDictionary(s => ((string)s!["name"]!).ToLowerInvariant(), s => (string)s!["id"]!);
        var newSongs = new JsonArray();
        var keys = new JsonArray();
        foreach (var group in request["groups"]!.AsArray())
        {
            var first = group!["keys"]!.AsArray()[0]!;
            var name = (string)first["text"]!;
            // "Uncle Song" is answered with a new song that already exists by name.
            if (name.Equals("Uncle Song", StringComparison.OrdinalIgnoreCase))
            {
                name = "Me And My Uncle";
                known.Remove("me and my uncle");
            }
            string? song = null;
            if (name.ToLowerInvariant() is not ("mystery jam" or "unknown thing"))
            {
                if (!known.TryGetValue(name.ToLowerInvariant(), out song))
                {
                    song = $"n{newSongs.Count + 1}";
                    newSongs.Add(
                        new JsonObject
                        {
                            ["id"] = song,
                            ["name"] = name,
                            ["kind"] = "song",
                            ["original_artist"] = null,
                        }
                    );
                }
            }

            foreach (var key in group["keys"]!.AsArray())
            {
                var text = ((string)key!["text"]!).ToLowerInvariant();
                keys.Add(
                    new JsonObject
                    {
                        ["id"] = (string)key["id"]!,
                        ["key_text"] = (string)key["text"]!,
                        ["songs"] = song == null ? new JsonArray() : new JsonArray(song),
                        ["song_names"] = song == null ? new JsonArray() : new JsonArray(name),
                        ["not_a_song"] =
                            text == "mystery jam" ? "jam"
                            : text == "unknown thing" ? "unknown"
                            : null,
                        ["confidence"] = text == "unknown thing" ? 0.3 : 0.95,
                        ["rationale"] = "scripted",
                    }
                );
            }
        }

        var reply = new JsonObject { ["new_songs"] = newSongs, ["keys"] = keys };
        return Task.FromResult(new ModelReply(reply.ToJsonString(), 100, 50));
    }
}

/// A model whose answer isn't the schema's shape.
internal sealed class BrokenModel : ICatalogueModel
{
    public string ModelId => "broken-1";

    public Task<ModelReply> CompleteAsync(
        string instructions,
        string data,
        string schemaName,
        string schemaJson,
        CancellationToken cancellationToken
    ) => Task.FromResult(new ModelReply("not json", 100, 5));
}

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public class TestCatalogueLlm
{
    private DbService db = null!;
    private string? previousWrite;
    private string? previousRead;
    private Guid artist;
    private int artistId;

    [OneTimeSetUp]
    public async Task Start()
    {
        MigratedTestDatabase.RequireLocalDatabase();
        await (await MigratedTestDatabase.OpenAsync()).DisposeAsync();
        previousWrite = DbService.ConnStr;
        previousRead = DbService.ReadOnlyConnStr;
        db = new DbService(
            LocalTestDatabase.DatabaseUrl,
            null,
            null,
            Host.CreateApplicationBuilder().Environment,
            NullLogger<DbService>.Instance
        );
    }

    [OneTimeTearDown]
    public void Stop()
    {
        if (previousWrite != null)
        {
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead!;
        }
    }

    [SetUp]
    public async Task Seed()
    {
        artist = Guid.NewGuid();
        artistId = await db.WithWriteConnection(c =>
            c.QuerySingleAsync<int>(
                "INSERT INTO artists(name, slug, uuid, updated_at) VALUES ('Fixture Band', @slug, @artist, now()) RETURNING id",
                new { slug = "llm-" + artist, artist }
            )
        );
        var source = await db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                """
                INSERT INTO sources(artist_id, upstream_identifier, display_date, uuid, updated_at, is_soundboard, is_remaster, has_jamcharts)
                VALUES (@artistId, @upstream, '1977-05-08', @uuid, now(), false, false, false) RETURNING id
                """,
                new
                {
                    artistId,
                    upstream = "src-" + artist,
                    uuid = Guid.NewGuid(),
                }
            )
        );
        var titles = new[]
        {
            "Me And My Uncle",
            "Me & My Uncle >",
            "Mee and My Uncle",
            "Sugaree",
            "Tuning",
            "Mystery Jam",
            "Unknown Thing",
            "Sugaree > Unknown Thing",
        };
        for (var i = 0; i < titles.Length; i++)
        {
            await db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    """
                    INSERT INTO source_tracks(source_id, track_position, title, slug, mp3_url, updated_at, artist_id, uuid)
                    VALUES (@source, @position, @title, @slug, @url, now(), @artistId, @uuid)
                    """,
                    new
                    {
                        source,
                        position = i + 1,
                        title = titles[i],
                        slug = "t" + i,
                        url = $"https://x/{artist}/{i}.mp3",
                        artistId,
                        uuid = Guid.NewGuid(),
                    }
                )
            );
        }

        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "INSERT INTO setlist_songs (artist_id, name, slug, upstream_identifier, uuid, updated_at) VALUES (@artistId, 'Sugaree', 'sugaree', 'sugaree', @uuid, now())",
                new { artistId, uuid = Guid.NewGuid() }
            )
        );
    }

    private Task<IEnumerable<string>> Strings(string sql) =>
        db.WithWriteConnection(c => c.QueryAsync<string>(sql, new { artist, artistId }));

    [Test]
    public async Task AChunkBuildsSongsAliasesEvidenceAndTracks()
    {
        var model = new ScriptedModel();
        var llm = new CatalogueLlm(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db),
            model
        );

        var since = await db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<DateTime>("SELECT clock_timestamp()")
        );
        var first = await llm.RunChunkAsync(artist, [], CancellationToken.None);

        first.Done.Should().BeFalse();
        first.SongsCreated.Should().Be(2);
        first.Unresolved.Should().Equal("unknown thing");
        // The call's receipt: linked to the run, request and reply kept, its unsure key named.
        (
            await db.WithWriteConnection(c =>
                c.QueryAsync<string>(
                    """
                    SELECT job || ' ' || (run_uuid IS NOT NULL)::text || ' ' || (reply IS NOT NULL)::text || ' ' || (outcome->'unresolved')::text
                    FROM norm.model_calls WHERE called_at >= @since AND request::text LIKE '%Sugaree%'
                    """,
                    new { since }
                )
            )
        )
            .Should()
            .Equal("catalogue true true [\"unknown thing\"]");
        (await Strings("SELECT name FROM norm.songs WHERE artist_uuid = @artist ORDER BY name"))
            .Should()
            .Equal("Me And My Uncle", "Sugaree");
        (
            await Strings(
                "SELECT normalized FROM norm.song_aliases WHERE artist_uuid = @artist ORDER BY 1"
            )
        )
            .Should()
            .BeEmpty(); // song names match directly; the one-show typo is a ruling on its track
        (
            await Strings(
                "SELECT s.name FROM norm.song_evidence e JOIN norm.songs s ON s.uuid = e.song_uuid JOIN setlist_songs ss ON ss.uuid = e.setlist_song_uuid WHERE ss.artist_id = @artistId"
            )
        )
            .Should()
            .Equal("Sugaree");
        (
            await Strings(
                """
                SELECT t.title || ' = ' || coalesce(s.name, CASE WHEN ts.resolved THEN '-' ELSE '?' END) || ' (' || ts.basis || ')'
                FROM source_tracks t
                JOIN norm.track_songs ts ON ts.track_uuid = t.uuid LEFT JOIN norm.songs s ON s.uuid = ts.song_uuid
                WHERE t.artist_id = @artistId ORDER BY t.track_position, ts.part
                """
            )
        )
            .Should()
            .Equal(
                "Me And My Uncle = Me And My Uncle (song_name)",
                "Me & My Uncle > = Me And My Uncle (song_name)",
                "Mee and My Uncle = Me And My Uncle (ruling)",
                "Sugaree = Sugaree (song_name)",
                "Tuning = - (noise)",
                "Mystery Jam = - (ruling)",
                // the medley keeps its known part; the unknown one is open (?)
                "Sugaree > Unknown Thing = Sugaree (song_name)",
                "Sugaree > Unknown Thing = ? (open)"
            );
        (await Strings("SELECT DISTINCT author_type FROM norm.songs WHERE artist_uuid = @artist"))
            .Should()
            .Equal("llm");

        // What it wasn't sure of isn't asked again in the same run; then there's nothing left.
        first.Asked.Should().Contain("unknown thing").And.Contain("sugaree");
        var second = await llm.RunChunkAsync(artist, first.Asked, CancellationToken.None);
        second.Done.Should().BeTrue();
        model.Calls.Should().Be(1);
    }

    [Test]
    public async Task ANewUploadOfAKnownSongIsMatchedWithoutTheModel()
    {
        await new CatalogueLlm(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db),
            new ScriptedModel()
        ).RunChunkAsync(artist, [], CancellationToken.None);
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                INSERT INTO source_tracks(source_id, track_position, title, slug, mp3_url, updated_at, artist_id, uuid)
                SELECT source_id, 99, '08 Sugaree.mp3', 'new', 'https://x/new.mp3', now(), artist_id, @uuid
                FROM source_tracks WHERE artist_id = @artistId LIMIT 1
                """,
                new { artistId, uuid = Guid.NewGuid() }
            )
        );

        var matched = await new CatalogueMatch(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db)
        ).RunAsync(artist, CancellationToken.None);

        matched.TracksWritten.Should().Be(1);
        (
            await Strings(
                "SELECT ts.author_type FROM norm.track_songs ts JOIN source_tracks t ON t.uuid = ts.track_uuid WHERE t.slug = 'new' AND t.artist_id = @artistId"
            )
        )
            .Should()
            .Equal("algorithm");
    }

    [Test]
    public async Task ANewSongThatExistsByNameIsThatSong()
    {
        var llm = new CatalogueLlm(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db),
            new ScriptedModel()
        );
        await llm.RunChunkAsync(artist, [], CancellationToken.None);
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                INSERT INTO source_tracks(source_id, track_position, title, slug, mp3_url, updated_at, artist_id, uuid)
                SELECT source_id, 50, 'Uncle Song', 'uncle', 'https://x/uncle.mp3', now(), artist_id, @uuid
                FROM source_tracks WHERE artist_id = @artistId LIMIT 1
                """,
                new { artistId, uuid = Guid.NewGuid() }
            )
        );

        await llm.RunChunkAsync(artist, [], CancellationToken.None);

        (await Strings("SELECT name FROM norm.songs WHERE artist_uuid = @artist ORDER BY name"))
            .Should()
            .Equal("Me And My Uncle", "Sugaree");
        (
            await Strings(
                "SELECT s.name FROM norm.track_songs ts JOIN source_tracks t ON t.uuid = ts.track_uuid JOIN norm.songs s ON s.uuid = ts.song_uuid WHERE t.slug = 'uncle' AND t.artist_id = @artistId"
            )
        )
            .Should()
            .Equal("Me And My Uncle");
    }

    [Test]
    public async Task AnOpenMedleyIsCompletedOnceItsUnknownPartIsKnown()
    {
        await new CatalogueLlm(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db),
            new ScriptedModel()
        ).RunChunkAsync(artist, [], CancellationToken.None);

        // A person names the unknown part; the next match completes the open medley.
        await db.WithWriteConnection(async c =>
        {
            var connection = (Npgsql.NpgsqlConnection)c;
            await using var tx = await connection.BeginTransactionAsync();
            var store = await NormStore.BeginAsync(connection, tx, AuthorTypes.Moderator, "m1");
            var song = Guid.CreateVersion7();
            await store.WriteSongsAsync([new SongRecord(song, artist, "Unknown Thing")]);
            await store.WriteAliasesAsync([new AliasRecord(artist, "Unknown Thing", song)]);
            await tx.CommitAsync();
        });
        await new CatalogueMatch(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db)
        ).RunAsync(artist, CancellationToken.None);

        (
            await Strings(
                """
                SELECT coalesce(s.name, '?') || ':' || ts.resolved FROM source_tracks t
                JOIN norm.track_songs ts ON ts.track_uuid = t.uuid LEFT JOIN norm.songs s ON s.uuid = ts.song_uuid
                WHERE t.artist_id = @artistId AND t.title = 'Sugaree > Unknown Thing' ORDER BY ts.part
                """
            )
        ).Should().Equal("Sugaree:true", "Unknown Thing:true");
    }

    [Test]
    public async Task AnAnswerOfTheWrongShapeIsASpentChunkWithNothingWritten()
    {
        var chunk = await new CatalogueLlm(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db),
            new BrokenModel()
        ).RunChunkAsync(artist, [], CancellationToken.None);

        // Counted by the caller like any chunk (no retry outside the run's cap), its keys asked.
        chunk.Done.Should().BeFalse();
        chunk.Asked.Should().Contain("sugaree");
        chunk.Unresolved.Should().BeEquivalentTo(chunk.Asked);
        chunk.InputTokens.Should().Be(100);
        (await Strings("SELECT name FROM norm.songs WHERE artist_uuid = @artist"))
            .Should()
            .BeEmpty();
    }

    [Test]
    public async Task CompletingAnOpenMedleyKeepsItsOneOffPart()
    {
        var llm = new CatalogueLlm(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db),
            new ScriptedModel()
        );
        await llm.RunChunkAsync(artist, [], CancellationToken.None);
        // "Uncle Song" is one show's spelling: its part is resolved, but no alias remembers it.
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                INSERT INTO source_tracks(source_id, track_position, title, slug, mp3_url, updated_at, artist_id, uuid)
                SELECT source_id, 50, 'Uncle Song > Unknown Thing', 'uncle', 'https://x/uncle.mp3', now(), artist_id, @uuid
                FROM source_tracks WHERE artist_id = @artistId LIMIT 1
                """,
                new { artistId, uuid = Guid.NewGuid() }
            )
        );
        await llm.RunChunkAsync(artist, [], CancellationToken.None);

        await db.WithWriteConnection(async c =>
        {
            var connection = (Npgsql.NpgsqlConnection)c;
            await using var tx = await connection.BeginTransactionAsync();
            var store = await NormStore.BeginAsync(connection, tx, AuthorTypes.Moderator, "m1");
            var song = Guid.CreateVersion7();
            await store.WriteSongsAsync([new SongRecord(song, artist, "Unknown Thing")]);
            await store.WriteAliasesAsync([new AliasRecord(artist, "Unknown Thing", song)]);
            await tx.CommitAsync();
        });
        await new CatalogueMatch(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db)
        ).RunAsync(artist, CancellationToken.None);

        (
            await Strings(
                """
                SELECT coalesce(s.name, '?') || ':' || ts.resolved FROM source_tracks t
                JOIN norm.track_songs ts ON ts.track_uuid = t.uuid LEFT JOIN norm.songs s ON s.uuid = ts.song_uuid
                WHERE t.artist_id = @artistId AND t.slug = 'uncle' ORDER BY ts.part
                """
            )
        ).Should().Equal("Me And My Uncle:true", "Unknown Thing:true");
    }

    private Task AsModerator(Func<NormStore, Task> write) =>
        db.WithWriteConnection(async c =>
        {
            var connection = (Npgsql.NpgsqlConnection)c;
            await using var tx = await connection.BeginTransactionAsync();
            var store = await NormStore.BeginAsync(connection, tx, AuthorTypes.Moderator, "m1");
            await write(store);
            await store.FinishAsync();
            await tx.CommitAsync();
            return 0;
        });

    private Task AddTrack(string title, string slug) =>
        db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                INSERT INTO source_tracks(source_id, track_position, title, slug, mp3_url, updated_at, artist_id, uuid)
                SELECT source_id, 60 + (SELECT count(*) FROM source_tracks WHERE artist_id = @artistId), @title, @slug,
                       'https://x/' || @slug || '.mp3', now(), artist_id, @uuid
                FROM source_tracks WHERE artist_id = @artistId LIMIT 1
                """,
                new
                {
                    artistId,
                    title,
                    slug,
                    uuid = Guid.NewGuid(),
                }
            )
        );

    private Task<IEnumerable<string>> Placed(string slug) =>
        db.WithWriteConnection(c =>
            c.QueryAsync<string>(
                """
                SELECT coalesce(s.name, '-') || ' (' || ts.basis || ')' || coalesce(' [' || array_to_string(nullif(ts.qualifiers, '{}'), ',') || ']', '')
                FROM source_tracks t JOIN norm.track_songs ts ON ts.track_uuid = t.uuid LEFT JOIN norm.songs s ON s.uuid = ts.song_uuid
                WHERE t.artist_id = @artistId AND t.slug = @slug ORDER BY ts.part
                """,
                new { artistId, slug }
            )
        );

    private Task<Guid> SongNamed(string name) =>
        db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "SELECT uuid FROM norm.songs WHERE artist_uuid = @artist AND name = @name",
                new { artist, name }
            )
        );

    [Test]
    public async Task AnAliasOfAMergedSongPlacesItsSurvivor()
    {
        await new CatalogueLlm(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db),
            new ScriptedModel()
        ).RunChunkAsync(artist, [], CancellationToken.None);
        var sugaree = await SongNamed("Sugaree");
        var duplicate = Guid.CreateVersion7();
        await AsModerator(async s =>
        {
            await s.WriteSongsAsync([new SongRecord(duplicate, artist, "Sugaree Two")]);
            await s.WriteAliasesAsync([new AliasRecord(artist, "Big Sugar", duplicate)]);
        });
        await db.WithWriteConnection(async c =>
        {
            var connection = (Npgsql.NpgsqlConnection)c;
            await using var tx = await connection.BeginTransactionAsync();
            await NormStore.BeginAsync(connection, tx, AuthorTypes.Moderator, "m1", "merge");
            await connection.ExecuteAsync(
                "UPDATE norm.songs SET merged_into = @sugaree WHERE uuid = @duplicate",
                new { sugaree, duplicate },
                tx
            );
            await tx.CommitAsync();
            return 0;
        });
        await AddTrack("Big Sugar", "merged");

        await new CatalogueMatch(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db)
        ).RunAsync(artist, CancellationToken.None);

        (await Placed("merged")).Should().Equal("Sugaree (alias)");
    }

    [Test]
    public async Task AnExpertsMergeMovesEveryTrackToTheSurvivor()
    {
        await new CatalogueLlm(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db),
            new ScriptedModel()
        ).RunChunkAsync(artist, [], CancellationToken.None);
        var sugaree = await SongNamed("Sugaree");
        var (shorter, jam) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await AsModerator(s =>
            s.WriteSongsAsync([
                new SongRecord(shorter, artist, "Sugar"),
                new SongRecord(jam, artist, "Sugaree Jam"),
            ])
        );
        await AddTrack("Sugar", "short");
        await AddTrack("Sugaree Jam", "jam");
        var match = new CatalogueMatch(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db)
        );
        await match.RunAsync(artist, CancellationToken.None);
        (await Placed("short")).Should().Equal("Sugar (song_name)");

        // The longer name is the song; the jam is the song with the jam qualifier.
        await AsModerator(async s =>
        {
            await s.MergeSongAsync(shorter, sugaree);
            await s.MergeSongAsync(jam, sugaree, keepNameAsAlias: false);
        });
        await match.RunAsync(artist, CancellationToken.None);

        (await Placed("short")).Should().Equal("Sugaree (alias)");
        (await Placed("jam")).Should().Equal("Sugaree (song_name) [jam]");
    }

    [Test]
    public async Task AMatchAnswersAMachinesQuestionButNotAPersons()
    {
        await new CatalogueLlm(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db),
            new ScriptedModel()
        ).RunChunkAsync(artist, [], CancellationToken.None);
        var sugaree = await SongNamed("Sugaree");
        await db.WithWriteConnection(async c =>
        {
            var connection = (Npgsql.NpgsqlConnection)c;
            await using var tx = await connection.BeginTransactionAsync();
            var bot = await NormStore.BeginAsync(
                connection,
                tx,
                AuthorTypes.Llm,
                "catalogue-llm@5"
            );
            await bot.WriteReviewItemsAsync([
                new ReviewRecord(
                    artist,
                    "Big Sugar",
                    "big sugar",
                    Proposal: "→ Sugaree",
                    Confidence: 0.8f,
                    Shows: 4
                ),
                new ReviewRecord(artist, "Can't Pay The Price", "cant pay the price", Shows: 4),
            ]);
            await tx.CommitAsync();
            return 0;
        });
        await AsModerator(async s =>
        {
            // The expert's note takes the open item over: it is theirs now.
            await s.WriteReviewItemsAsync([
                new ReviewRecord(
                    artist,
                    "Can't Pay The Price",
                    "cant pay the price",
                    "maybe Throwing Stones; needs a listen"
                ),
            ]);
            await s.WriteAliasesAsync([new AliasRecord(artist, "Big Sugar", sugaree)]);
            await s.WriteAliasesAsync([new AliasRecord(artist, "Can't Pay The Price", sugaree)]);
        });

        await new CatalogueMatch(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db)
        ).RunAsync(artist, CancellationToken.None);

        (
            await Strings(
                "SELECT key || ':' || (resolved_at IS NOT NULL) || ':' || coalesce(note, '') || ':' || coalesce(shows::text, '') FROM norm.review_items WHERE artist_uuid = @artist ORDER BY key"
            )
        )
            .Should()
            .Equal(
                "big sugar:true::4",
                "cant pay the price:false:maybe Throwing Stones; needs a listen:4"
            );
    }

    [Test]
    public async Task AMatchAnswersAQuestionARulingSettled()
    {
        await new CatalogueLlm(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db),
            new ScriptedModel()
        ).RunChunkAsync(artist, [], CancellationToken.None);
        await db.WithWriteConnection(async c =>
        {
            var connection = (Npgsql.NpgsqlConnection)c;
            await using var tx = await connection.BeginTransactionAsync();
            var bot = await NormStore.BeginAsync(
                connection,
                tx,
                AuthorTypes.Llm,
                "catalogue-llm@5"
            );
            await bot.WriteReviewItemsAsync([
                new ReviewRecord(artist, "Mystery Jam", "mystery jam", Shows: 3),
            ]);
            await tx.CommitAsync();
            return 0;
        });

        // The chunk ruled "Mystery Jam" not a song: the question is answered.
        await new CatalogueMatch(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db)
        ).RunAsync(artist, CancellationToken.None);

        (
            await Strings(
                "SELECT (resolved_at IS NOT NULL)::text FROM norm.review_items WHERE artist_uuid = @artist"
            )
        )
            .Should()
            .Equal("true");
    }

    [Test]
    public async Task ARulingOnAMergedSongFollowsTheSurvivor()
    {
        await new CatalogueLlm(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db),
            new ScriptedModel()
        ).RunChunkAsync(artist, [], CancellationToken.None);
        var uncle = await SongNamed("Me And My Uncle");
        var survivor = Guid.CreateVersion7();
        await AsModerator(async s =>
        {
            await s.WriteSongsAsync([
                new SongRecord(survivor, artist, "Me and My Uncle (Phillips)"),
            ]);
            await s.MergeSongAsync(uncle, survivor);
        });

        await new CatalogueMatch(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db)
        ).RunAsync(artist, CancellationToken.None);

        // "Mee and My Uncle" was the LLM's one-off ruling on Me And My Uncle.
        (await Placed("t2"))
            .Should()
            .Equal("Me and My Uncle (Phillips) (ruling)");
    }

    [Test]
    public async Task AnExpertsAliasFixReachesEveryTrackButNoRuling()
    {
        await new CatalogueLlm(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db),
            new ScriptedModel()
        ).RunChunkAsync(artist, [], CancellationToken.None);
        var sugaree = await SongNamed("Sugaree");
        var uncle = await SongNamed("Me And My Uncle");
        await AsModerator(s =>
            s.WriteAliasesAsync([new AliasRecord(artist, "Big Sugar", sugaree)])
        );
        await AddTrack("Big Sugar", "big1");
        await AddTrack("05 - Big Sugar *", "big2");
        var match = new CatalogueMatch(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db)
        );
        await match.RunAsync(artist, CancellationToken.None);
        (await Placed("big2")).Should().Equal("Sugaree (alias)");

        // The expert finds "Big Sugar" is Me And My Uncle and fixes the alias once.
        await AsModerator(s => s.WriteAliasesAsync([new AliasRecord(artist, "Big Sugar", uncle)]));
        var rebuilt = await match.RunAsync(artist, CancellationToken.None);

        rebuilt.TracksWritten.Should().Be(2);
        (await Placed("big1")).Should().Equal("Me And My Uncle (alias)");
        (await Placed("big2")).Should().Equal("Me And My Uncle (alias)");
        // The LLM's ruling on a one-off spelling is a decision: the rebuild leaves it.
        (await Placed("t2"))
            .Should()
            .Equal("Me And My Uncle (ruling)");
        // Derived rows aren't logged; the run counts them.
        (
            await Strings(
                "SELECT DISTINCT c.after ->> 'basis' FROM norm.changes c JOIN source_tracks t ON t.uuid = (c.row_key ->> 'track_uuid')::uuid WHERE c.table_name = 'track_songs' AND c.after IS NOT NULL AND t.artist_id = @artistId"
            )
        )
            .Should()
            .Equal("ruling");
        (
            await Strings(
                "SELECT r.tracks_rebuilt::text FROM norm.runs r WHERE r.author_ref = 'title-matcher@2.2.0' ORDER BY r.started_at DESC LIMIT 1"
            )
        )
            .Should()
            .Equal("2");
    }

    [Test]
    public async Task AnExactAliasReplacesAMachinesRulingAndALostAliasClearsItsTracks()
    {
        await new CatalogueLlm(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db),
            new ScriptedModel()
        ).RunChunkAsync(artist, [], CancellationToken.None);
        var sugaree = await SongNamed("Sugaree");
        await AsModerator(s =>
            s.WriteAliasesAsync([new AliasRecord(artist, "Mystery Jam", sugaree)])
        );
        var match = new CatalogueMatch(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db)
        );
        await match.RunAsync(artist, CancellationToken.None);
        (await Placed("t5")).Should().Equal("Sugaree (alias)");

        await AsModerator(s => s.DeleteAliasAsync(artist, "Mystery Jam"));
        await match.RunAsync(artist, CancellationToken.None);

        // Nothing places it now: it is unresolved again, for the next LLM run.
        (await Placed("t5"))
            .Should()
            .BeEmpty();
    }

    [Test]
    public async Task AReprisesSongIsTheSongUnlessTheRepriseIsItsOwnSong()
    {
        await new CatalogueLlm(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db),
            new ScriptedModel()
        ).RunChunkAsync(artist, [], CancellationToken.None);
        await AddTrack("Sugaree Reprise >", "rep1");
        await AddTrack("Uncle Song Reprise", "rep2");
        await AsModerator(s =>
            s.WriteSongsAsync([new SongRecord(Guid.CreateVersion7(), artist, "Uncle Song Reprise")])
        );

        await new CatalogueMatch(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db)
        ).RunAsync(artist, CancellationToken.None);

        (await Placed("rep1")).Should().Equal("Sugaree (song_name) [reprise]");
        (await Placed("rep2")).Should().Equal("Uncle Song Reprise (song_name)");
    }
}
