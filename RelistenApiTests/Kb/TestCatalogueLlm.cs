using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Relisten;
using Relisten.Ingestion;
using Relisten.Ingestion.Workflows;
using Relisten.Kb;
using Relisten.Kb.Catalogue;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;

namespace RelistenApiTests.Kb;

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
        var llm = new CatalogueLlm(db, model);

        var first = await llm.RunChunkAsync(artist, [], CancellationToken.None);

        first.Done.Should().BeFalse();
        first.SongsCreated.Should().Be(2);
        first.Unresolved.Should().Equal("unknown thing");
        (await Strings("SELECT name FROM kb.songs WHERE artist_uuid = @artist ORDER BY name"))
            .Should()
            .Equal("Me And My Uncle", "Sugaree");
        (
            await Strings(
                "SELECT normalized FROM kb.song_aliases WHERE artist_uuid = @artist ORDER BY 1"
            )
        )
            .Should()
            .Equal("me and my uncle", "sugaree"); // the one-show typo resolved its track, no alias
        (
            await Strings(
                "SELECT s.name FROM kb.song_evidence e JOIN kb.songs s ON s.uuid = e.song_uuid JOIN setlist_songs ss ON ss.uuid = e.setlist_song_uuid WHERE ss.artist_id = @artistId"
            )
        )
            .Should()
            .Equal("Sugaree");
        (
            await Strings(
                """
                SELECT t.title || ' = ' || coalesce(s.name, CASE WHEN ts.resolved THEN '-' ELSE '?' END) FROM source_tracks t
                JOIN kb.track_songs ts ON ts.track_uuid = t.uuid LEFT JOIN kb.songs s ON s.uuid = ts.song_uuid
                WHERE t.artist_id = @artistId ORDER BY t.track_position, ts.part
                """
            )
        )
            .Should()
            .Equal(
                "Me And My Uncle = Me And My Uncle",
                "Me & My Uncle > = Me And My Uncle",
                "Mee and My Uncle = Me And My Uncle",
                "Sugaree = Sugaree",
                "Tuning = -",
                "Mystery Jam = -",
                // the medley keeps its known part; the unknown one is open (?)
                "Sugaree > Unknown Thing = Sugaree",
                "Sugaree > Unknown Thing = ?"
            );
        (await Strings("SELECT DISTINCT author_type FROM kb.songs WHERE artist_uuid = @artist"))
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
        await new CatalogueLlm(db, new ScriptedModel()).RunChunkAsync(
            artist,
            [],
            CancellationToken.None
        );
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

        var matched = await new CatalogueMatch(db).RunAsync(artist, CancellationToken.None);

        matched.TracksWritten.Should().Be(1);
        (
            await Strings(
                "SELECT ts.author_type FROM kb.track_songs ts JOIN source_tracks t ON t.uuid = ts.track_uuid WHERE t.slug = 'new' AND t.artist_id = @artistId"
            )
        )
            .Should()
            .Equal("algorithm");
    }

    [Test]
    public async Task ANewSongThatExistsByNameIsThatSong()
    {
        var llm = new CatalogueLlm(db, new ScriptedModel());
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

        (await Strings("SELECT name FROM kb.songs WHERE artist_uuid = @artist ORDER BY name"))
            .Should()
            .Equal("Me And My Uncle", "Sugaree");
        (
            await Strings(
                "SELECT s.name FROM kb.track_songs ts JOIN source_tracks t ON t.uuid = ts.track_uuid JOIN kb.songs s ON s.uuid = ts.song_uuid WHERE t.slug = 'uncle' AND t.artist_id = @artistId"
            )
        )
            .Should()
            .Equal("Me And My Uncle");
    }

    [Test]
    public async Task AnOpenMedleyIsCompletedOnceItsUnknownPartIsKnown()
    {
        await new CatalogueLlm(db, new ScriptedModel()).RunChunkAsync(
            artist,
            [],
            CancellationToken.None
        );

        // A person names the unknown part; the next match completes the open medley.
        await db.WithWriteConnection(async c =>
        {
            var connection = (Npgsql.NpgsqlConnection)c;
            await using var tx = await connection.BeginTransactionAsync();
            var store = await KbStore.BeginAsync(connection, tx, AuthorTypes.Moderator, "m1");
            var song = Guid.CreateVersion7();
            await store.WriteSongsAsync([new SongRecord(song, artist, "Unknown Thing")]);
            await store.WriteAliasesAsync([new AliasRecord(artist, "Unknown Thing", song)]);
            await tx.CommitAsync();
        });
        await new CatalogueMatch(db).RunAsync(artist, CancellationToken.None);

        (
            await Strings(
                """
                SELECT coalesce(s.name, '?') || ':' || ts.resolved FROM source_tracks t
                JOIN kb.track_songs ts ON ts.track_uuid = t.uuid LEFT JOIN kb.songs s ON s.uuid = ts.song_uuid
                WHERE t.artist_id = @artistId AND t.title = 'Sugaree > Unknown Thing' ORDER BY ts.part
                """
            )
        ).Should().Equal("Sugaree:true", "Unknown Thing:true");
    }
}

public class TestCatalogueAnswers
{
    private static CatalogueRequest Request() =>
        new()
        {
            Data = "{}",
            SongIds = new Dictionary<string, Guid> { ["s1"] = Guid.NewGuid() },
            SongNames = new Dictionary<string, string> { ["s1"] = "Sugaree" },
            KeyTexts = new Dictionary<string, string> { ["k1"] = "Sugaree", ["k2"] = "Tuning" },
            KeyIds = new Dictionary<string, string> { ["k1"] = "sugaree", ["k2"] = "tuning" },
        };

    [TestCase(
        """{"new_songs": [], "keys": [{"id": "k1", "songs": ["s9"], "not_a_song": null, "confidence": 0.95, "rationale": ""}]}"""
    )]
    [TestCase(
        """{"new_songs": [], "keys": [{"id": "k7", "songs": ["s1"], "not_a_song": null, "confidence": 0.95, "rationale": ""}]}"""
    )]
    [TestCase(
        """{"new_songs": [], "keys": [{"id": "k1", "songs": ["s1"], "not_a_song": null, "confidence": 0.95, "rationale": ""}, {"id": "k1", "songs": ["s1"], "not_a_song": null, "confidence": 0.95, "rationale": ""}]}"""
    )]
    [TestCase(
        """{"new_songs": [], "keys": [{"id": "k1", "songs": ["s1"], "not_a_song": "crowd", "confidence": 0.95, "rationale": ""}]}"""
    )]
    [TestCase(
        """{"new_songs": [], "keys": [{"id": "k1", "songs": [], "not_a_song": null, "confidence": 0.95, "rationale": ""}]}"""
    )]
    [TestCase(
        """{"new_songs": [], "keys": [{"id": "k1", "songs": ["s1"], "song_names": ["Crazy Fingers"], "not_a_song": null, "confidence": 0.99, "rationale": ""}]}"""
    )]
    [TestCase(
        """{"new_songs": [], "keys": [{"id": "k1", "key_text": "Tuning", "songs": ["s1"], "song_names": ["Sugaree"], "not_a_song": null, "confidence": 0.99, "rationale": ""}]}"""
    )]
    public void AKeyWhoseAnswerBreaksTheRulesStaysUnresolved(string json)
    {
        var answer = CatalogueRequest.Parse(json, Request());

        answer.Keys.Should().NotContainKey("sugaree");
        answer.Rejected.Should().Be(2);
    }

    [TestCase("not json")]
    [TestCase("""{"keys": []}""")]
    public void AnAnswerOfTheWrongShapeThrows(string json) =>
        FluentActions
            .Invoking(() => CatalogueRequest.Parse(json, Request()))
            .Should()
            .Throw<CatalogueAnswerException>();

    [Test]
    public void AValidAnswerParses()
    {
        var answer = CatalogueRequest.Parse(
            """{"new_songs": [{"id": "n1", "name": "Tuning Jam", "kind": "jam", "original_artist": null}], "keys": [{"id": "k1", "key_text": "Sugaree", "songs": ["s1"], "song_names": ["Sugaree"], "not_a_song": null, "confidence": 0.97, "rationale": "x"}, {"id": "k2", "key_text": "Tuning", "songs": [], "song_names": [], "not_a_song": "tuning", "confidence": 0.99, "rationale": "y"}]}""",
            Request()
        );

        answer.Keys["sugaree"].SongRefs.Should().Equal("s1");
        answer.Keys["tuning"].SongRefs.Should().BeEmpty();
    }
}

/// The workflow loops until a step reports nothing left, carrying unresolved keys forward,
/// then runs the deterministic match.
[TestFixture, NonParallelizable]
public class TestKbCatalogueWorkflow
{
    private WorkflowEnvironment environment = null!;

    [OneTimeSetUp]
    public async Task StartServer() => environment = await WorkflowEnvironment.StartLocalAsync();

    [OneTimeTearDown]
    public async Task StopServer() => await environment.DisposeAsync();

    public class Steps
    {
        public List<int> SkipSizes { get; } = [];
        public List<string> Forgotten { get; } = [];
        public List<int?> OnlySizes { get; } = [];
        public int Matches { get; private set; }

        [Activity("Kb.CatalogueChunk")]
        public CatalogueChunkResult CatalogueChunk(KbCatalogueChunkInput input)
        {
            SkipSizes.Add(input.Skip.Count);
            OnlySizes.Add(input.Only?.Count);
            return SkipSizes.Count <= 2
                ? new CatalogueChunkResult(
                    false,
                    50,
                    40,
                    [$"key{SkipSizes.Count}"],
                    [$"key{SkipSizes.Count}", $"sure{SkipSizes.Count}"],
                    [new ReviewItem($"key{SkipSizes.Count}", "Key", 4)],
                    3,
                    100,
                    1000,
                    200
                )
                : new CatalogueChunkResult(true, 0, 0, [], [], [], 0, 0, 0, 0);
        }

        [Activity("Kb.ForgetDecisions")]
        public int ForgetDecisions(KbForgetInput input)
        {
            Forgotten.AddRange(input.Spellings);
            return 1;
        }

        [Activity("Kb.MatchTracks")]
        public CatalogueMatchResult MatchTracks(Guid artist)
        {
            Matches++;
            return new CatalogueMatchResult(7, 0);
        }
    }

    [Test]
    public async Task LoopsUntilDoneThenMatches()
    {
        var steps = new Steps();
        using var workflows = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(IngestionQueues.Workflows).AddWorkflow<KbCatalogueWorkflow>()
        );
        using var database = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(IngestionQueues.Database).AddAllActivities(steps)
        );

        var result = await workflows.ExecuteAsync(() =>
            database.ExecuteAsync(() =>
                environment.Client.ExecuteWorkflowAsync(
                    (KbCatalogueWorkflow w) =>
                        w.RunAsync(new BuildKbCatalogue(new ArtistId(Guid.NewGuid()))),
                    new WorkflowOptions("test/" + Guid.NewGuid(), IngestionQueues.Workflows)
                )
            )
        );

        // First pass: two chunks, then done; review pass: done at once, with a fresh skip list.
        steps.SkipSizes.Should().Equal(0, 2, 4, 0);
        steps.Matches.Should().Be(1);
        result
            .Should()
            .BeEquivalentTo(
                new KbCatalogueResult(2, 6, 80, 2, 207, 2000, 400),
                o => o.Excluding(r => r.TracksPerChunk).Excluding(r => r.StillUnsure)
            );
        result.TracksPerChunk.Should().Equal(100, 100);
    }

    [Test]
    public async Task AReaskForgetsThenAsksOnlyThoseTitles()
    {
        var steps = new Steps();
        using var workflows = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(IngestionQueues.Workflows).AddWorkflow<KbCatalogueWorkflow>()
        );
        using var database = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(IngestionQueues.Database).AddAllActivities(steps)
        );

        await workflows.ExecuteAsync(() =>
            database.ExecuteAsync(() =>
                environment.Client.ExecuteWorkflowAsync(
                    (KbCatalogueWorkflow w) =>
                        w.RunAsync(
                            new BuildKbCatalogue(
                                new ArtistId(Guid.NewGuid()),
                                null,
                                new[] { "Let Me Lie", "Mellow Mood" }
                            )
                        ),
                    new WorkflowOptions("test/" + Guid.NewGuid(), IngestionQueues.Workflows)
                )
            )
        );

        steps.Forgotten.Should().Equal("Let Me Lie", "Mellow Mood");
        steps.OnlySizes.Should().AllBeEquivalentTo(2);
    }

    [Test]
    public async Task TheChunkCapCoversTheReviewPassToo()
    {
        var steps = new Steps();
        using var workflows = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(IngestionQueues.Workflows).AddWorkflow<KbCatalogueWorkflow>()
        );
        using var database = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(IngestionQueues.Database).AddAllActivities(steps)
        );

        var result = await workflows.ExecuteAsync(() =>
            database.ExecuteAsync(() =>
                environment.Client.ExecuteWorkflowAsync(
                    (KbCatalogueWorkflow w) =>
                        w.RunAsync(new BuildKbCatalogue(new ArtistId(Guid.NewGuid()), 2, null)),
                    new WorkflowOptions("test/" + Guid.NewGuid(), IngestionQueues.Workflows)
                )
            )
        );

        // Two chunks spent the budget: no review pass call at all.
        steps.SkipSizes.Should().Equal(0, 2);
        result.Chunks.Should().Be(2);
    }
}
