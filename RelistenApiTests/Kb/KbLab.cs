using System.Diagnostics;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Relisten;
using Relisten.Kb;
using Relisten.Kb.Catalogue;
using Relisten.Kb.Titles;

namespace RelistenApiTests.Kb;

/// The kb catalogue on real data: the lab database that local-dev/kb-lab.sh loads from the
/// production replica (a slice of artists across 1959–2026). Skipped unless
/// KB_LAB_DATABASE_URL is set, e.g.
///   KB_LAB_DATABASE_URL=postgresql://relisten:local_dev_password@127.0.0.1:15432/relisten_kb_lab
///   KB_LAB_ARTISTS=phish,grateful-dead            (default: every artist in the lab)
/// The LLM run also needs OPENAI_API_KEY and KB_LAB_MODEL (KB_LAB_MAX_CHUNKS caps the calls
/// per artist, default 3); it writes to the lab itself so the results can be inspected.
[TestFixture, NonParallelizable, Category("KbLab")]
public class KbLab
{
    private string labUrl = null!;
    private string? previousWrite;
    private string? previousRead;

    [OneTimeSetUp]
    public void Start()
    {
        labUrl = Environment.GetEnvironmentVariable("KB_LAB_DATABASE_URL") ?? "";
        if (labUrl.Length == 0)
        {
            Assert.Ignore("KB_LAB_DATABASE_URL not set; load the lab with local-dev/kb-lab.sh");
        }

        previousWrite = DbService.ConnStr;
        previousRead = DbService.ReadOnlyConnStr;
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

    private static DbService Db(string url) =>
        new(
            url,
            null,
            null,
            Host.CreateApplicationBuilder().Environment,
            NullLogger<DbService>.Instance
        );

    private static async Task<NpgsqlConnection> Open(DbService db)
    {
        var connection = db.CreateConnection(longTimeout: true, readOnly: false);
        await connection.OpenAsync();
        return connection;
    }

    private async Task<List<(string Slug, Guid Uuid)>> Artists(NpgsqlConnection connection)
    {
        var wanted = (Environment.GetEnvironmentVariable("KB_LAB_ARTISTS") ?? "").Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        return (
            await connection.QueryAsync<(string, Guid)>(
                "SELECT slug, uuid FROM artists ORDER BY slug"
            )
        )
            .Where(a => wanted.Length == 0 || wanted.Contains(a.Item1))
            .ToList();
    }

    [Test]
    public async Task EveryTitleParses()
    {
        await using var connection = await Open(Db(labUrl));
        foreach (var (slug, _) in await Artists(connection))
        {
            var titles = (
                await connection.QueryAsync<(string Title, int Position, int Tracks)>(
                    """
                    SELECT t.title, t.track_position, count(*)::int FROM source_tracks t JOIN artists a ON a.id = t.artist_id
                    WHERE a.slug = @slug GROUP BY 1, 2
                    """,
                    new { slug }
                )
            ).ToList();
            var withText = titles
                .Where(t => TitleParser.Parse(t.Title, t.Position).HasText)
                .Sum(t => t.Tracks);
            var total = titles.Sum(t => t.Tracks);
            TestContext.Out.WriteLine(
                $"{slug}: {titles.Count} distinct titles, song text on {(double)withText / Math.Max(total, 1):P1} of {total} tracks"
            );
        }
    }

    /// The matcher, triggers, revert and report on real titles, with a stand-in catalogue built
    /// from upstream setlist names (test scaffolding, not the pipeline), on a scratch copy.
    [Test]
    public async Task TheModelHoldsOnRealTitles()
    {
        var scratch = "relisten_kb_lab_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = new NpgsqlConnection(Admin()))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync(
                $"CREATE DATABASE {scratch} TEMPLATE {new NpgsqlConnectionStringBuilder(Admin(labUrl)).Database}"
            );
        }

        try
        {
            var db = Db(labUrl.Replace("/relisten_kb_lab", "/" + scratch));
            await using var connection = await Open(db);
            foreach (var (slug, uuid) in await Artists(connection))
            {
                if (
                    !await connection.ExecuteScalarAsync<bool>(
                        "SELECT EXISTS (SELECT 1 FROM setlist_songs ss JOIN artists a ON a.id = ss.artist_id WHERE a.slug = @slug)",
                        new { slug }
                    )
                )
                {
                    continue;
                }

                await ScaffoldFromSetlists(connection, slug, uuid);
                var watch = Stopwatch.StartNew();
                var match = new CatalogueMatch(db);
                var first = await match.RunAsync(uuid, CancellationToken.None);
                var elapsed = watch.Elapsed;

                var evaluation = await KbEvaluation.EvaluateAsync(connection, slug);
                TestContext.Out.WriteLine($"{evaluation} [match {elapsed.TotalSeconds:F1}s]");
                foreach (var d in evaluation.SampleDisagreements.Take(8))
                {
                    TestContext.Out.WriteLine("    " + d);
                }

                (await match.RunAsync(uuid, CancellationToken.None))
                    .TracksWritten.Should()
                    .Be(0, "a re-run of {0} changes nothing", slug);
                await AssertInvariants(connection);

                // The match run reverts exactly.
                var run = await connection.ExecuteScalarAsync<Guid>(
                    "SELECT r.uuid FROM kb.runs r WHERE r.author_ref = @Author AND EXISTS (SELECT 1 FROM kb.changes c WHERE c.run_uuid = r.uuid) ORDER BY r.started_at DESC LIMIT 1",
                    new { TrackMatcher.Author }
                );
                await using var tx = await connection.BeginTransactionAsync();
                var store = await KbStore.BeginAsync(connection, tx, AuthorTypes.Moderator, "lab");
                var (reverted, skipped) = await store.RevertRunAsync(run);
                skipped.Should().Be(0);
                reverted.Should().BeGreaterThan(0);
                (
                    await connection.ExecuteScalarAsync<long>(
                        "SELECT count(*) FROM kb.track_songs WHERE run_uuid = @run",
                        new { run },
                        tx
                    )
                )
                    .Should()
                    .Be(0);
                TestContext.Out.WriteLine(
                    $"    revert of the match run: {reverted} changes undone"
                );
                await tx.RollbackAsync();
                first.TracksWritten.Should().BeGreaterThan(0);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(Admin());
            await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {scratch} WITH (FORCE)");
        }
    }

    /// The real pipeline: the LLM catalogue then the deterministic match, on the lab itself.
    [Test, Explicit("Calls OpenAI; run deliberately")]
    public async Task LlmCatalogue()
    {
        var model = Environment.GetEnvironmentVariable("KB_LAB_MODEL");
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(key))
        {
            Assert.Ignore("KB_LAB_MODEL and OPENAI_API_KEY are needed");
        }

        var maxChunks = int.TryParse(
            Environment.GetEnvironmentVariable("KB_LAB_MAX_CHUNKS"),
            out var m
        )
            ? m
            : 3;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["OpenAI:Model"] = model,
                    ["OpenAI:ApiKey"] = key,
                }
            )
            .Build();
        var db = Db(labUrl);
        var llm = new CatalogueLlm(db, new OpenAiCatalogueModel(configuration));
        await using var connection = await Open(db);

        foreach (var (slug, uuid) in await Artists(connection))
        {
            var (chunks, input, output, songs) = (0, 0, 0, 0);
            var curve = new List<int>();
            var unsure = new List<ReviewItem>();
            var watch = Stopwatch.StartNew();
            var reviewOnly = Environment.GetEnvironmentVariable("KB_LAB_REVIEW_ONLY") == "1";
            // KB_LAB_MATCH_ONLY=1: no LLM, only the deterministic match (after an expert's edits).
            var matchOnly = Environment.GetEnvironmentVariable("KB_LAB_MATCH_ONLY") == "1";
            // KB_LAB_ONLY_KEYS_FILE: lines of "artist<TAB>key"; ask only about those keys.
            var onlyFile = Environment.GetEnvironmentVariable("KB_LAB_ONLY_KEYS_FILE");
            var only = string.IsNullOrEmpty(onlyFile)
                ? null
                : File.ReadLines(onlyFile)
                    .Select(l => l.Split('\t'))
                    .Where(f => f.Length == 2 && f[0] == slug)
                    .Select(f => f[1])
                    .ToHashSet();
            if (only != null)
            {
                var (forgotAliases, forgotTracks) = await llm.ForgetAsync(
                    uuid,
                    only,
                    CancellationToken.None
                );
                TestContext.Out.WriteLine(
                    $"  {slug} re-ask: forgot {forgotAliases} aliases and {forgotTracks} tracks for {only.Count} spellings"
                );
            }

            // As the workflow does: a first pass over everything, then a review pass over the
            // recurring titles still unresolved, with neighbouring titles as context.
            foreach (
                var review in matchOnly ? []
                : reviewOnly ? new[] { true }
                : new[] { false, true }
            )
            {
                var skip = new List<string>();
                for (var passChunks = 0; review || passChunks < maxChunks; passChunks++)
                {
                    // Retried like the workflow's step: up to three attempts.
                    CatalogueChunkResult chunk;
                    for (var attempt = 1; ; attempt++)
                    {
                        try
                        {
                            chunk = await llm.RunChunkAsync(
                                uuid,
                                skip,
                                CancellationToken.None,
                                review,
                                only
                            );
                            break;
                        }
                        catch (CatalogueAnswerException e) when (attempt < 3)
                        {
                            TestContext.Out.WriteLine($"  {slug}: retrying a step ({e.Message})");
                        }
                    }

                    if (chunk.Done)
                    {
                        break;
                    }

                    chunks++;
                    skip.AddRange(chunk.Asked);
                    curve.Add(chunk.TracksWritten);
                    if (review)
                    {
                        unsure.AddRange(chunk.Review);
                    }

                    (input, output, songs) = (
                        input + chunk.InputTokens,
                        output + chunk.OutputTokens,
                        songs + chunk.SongsCreated
                    );
                    TestContext.Out.WriteLine(
                        $"  {slug} {(review ? "review" : "chunk")} {chunks}: {chunk.KeysResolved} keys resolved, {chunk.Unresolved.Count} unsure, {chunk.SongsCreated} new songs, {chunk.TracksWritten} tracks"
                    );
                }
            }

            TestContext.Out.WriteLine(
                $"  {slug} returns curve (tracks per chunk): {string.Join(" ", curve)}"
            );
            TestContext.Out.WriteLine(
                $"  {slug} still unsure after review ({unsure.Count}): "
                    + string.Join(
                        "; ",
                        unsure
                            .OrderByDescending(u => u.Shows)
                            .Take(25)
                            .Select(u =>
                                $"{u.Title} ({u.Shows} shows, {u.Tapers} tapers): {u.Proposal ?? "no answer"} {u.Confidence:F2}"
                            )
                    )
            );

            await new CatalogueMatch(db).RunAsync(uuid, CancellationToken.None);
            var evaluation = await KbEvaluation.EvaluateAsync(connection, slug);
            TestContext.Out.WriteLine(
                $"{evaluation} [{model}: {chunks} chunks, {input} in / {output} out tokens, {songs} songs created, {watch.Elapsed.TotalSeconds:F0}s]"
            );
            foreach (var d in evaluation.SampleDisagreements.Take(10))
            {
                TestContext.Out.WriteLine("    " + d);
            }

            await WriteReport(
                connection,
                slug,
                uuid,
                evaluation,
                $"{model}: {chunks} chunks, {input} in / {output} out tokens",
                unsure
            );
        }

        await AssertInvariants(connection);
    }

    /// For artists without an upstream setlist there is no automatic answer key: the report
    /// lists every song the run created and a random sample of title → song decisions to judge.
    private static async Task WriteReport(
        NpgsqlConnection connection,
        string slug,
        Guid artist,
        ArtistEvaluation evaluation,
        string run,
        IReadOnlyList<ReviewItem>? unsure = null
    )
    {
        var dir = Environment.GetEnvironmentVariable("KB_LAB_REPORT_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        var songs = await connection.QueryAsync<(
            string Name,
            string? Kind,
            string? Cover,
            long Tracks
        )>(
            """
            SELECT s.name, s.kind, s.original_artist, (SELECT count(DISTINCT ts.track_uuid) FROM kb.track_songs ts WHERE ts.song_uuid = s.uuid)
            FROM kb.songs s WHERE s.artist_uuid = @artist ORDER BY 4 DESC, 1
            """,
            new { artist }
        );
        var sample = await connection.QueryAsync<(
            string Title,
            string? Song,
            string? Text,
            float? Confidence,
            string Author
        )>(
            """
            SELECT t.title, s.name, ts.text, ts.confidence, ts.author_ref
            FROM kb.track_songs ts JOIN source_tracks t ON t.uuid = ts.track_uuid
            LEFT JOIN kb.songs s ON s.uuid = ts.song_uuid
            WHERE t.artist_id = (SELECT id FROM artists WHERE uuid = @artist)
            ORDER BY md5(ts.track_uuid::text || ts.part) LIMIT 40
            """,
            new { artist }
        );

        var lines = new List<string>
        {
            $"# {slug}",
            "",
            evaluation.ToString(),
            "",
            $"Run: {run}",
            "",
            "## Sample of title → song decisions",
            "",
            "| Title | Part | Song | Confidence | By |",
            "|---|---|---|---|---|",
        };
        lines.AddRange(
            sample.Select(r =>
                $"| {r.Title.Replace("|", "/")} | {r.Text?.Replace("|", "/")} | {r.Song ?? "(not a song)"} | {r.Confidence:F2} | {r.Author} |"
            )
        );
        if (unsure is { Count: > 0 })
        {
            lines.AddRange([
                "",
                "## Review list (recurring titles below the bar)",
                "",
                "| Title | Shows | Tapers | Model's proposal | Confidence |",
                "|---|---|---|---|---|",
            ]);
            lines.AddRange(
                unsure
                    .OrderByDescending(u => u.Shows)
                    .Select(u =>
                        $"| {u.Title.Replace("|", "/")} | {u.Shows} | {u.Tapers} | {u.Proposal ?? "(no answer)"} | {u.Confidence:F2} |"
                    )
            );
        }

        lines.AddRange([
            "",
            $"## Songs ({songs.Count()})",
            "",
            "| Song | Kind | Cover of | Tracks |",
            "|---|---|---|---|",
        ]);
        lines.AddRange(
            songs.Select(s => $"| {s.Name.Replace("|", "/")} | {s.Kind} | {s.Cover} | {s.Tracks} |")
        );

        Directory.CreateDirectory(dir!);
        await File.WriteAllLinesAsync(Path.Combine(dir!, $"{slug}.md"), lines);
    }

    private static async Task ScaffoldFromSetlists(
        NpgsqlConnection connection,
        string slug,
        Guid artist
    )
    {
        var names = (
            await connection.QueryAsync<(Guid Uuid, string Name)>(
                "SELECT ss.uuid, ss.name FROM setlist_songs ss JOIN artists a ON a.id = ss.artist_id WHERE a.slug = @slug",
                new { slug }
            )
        )
            .Select(n => (n.Uuid, n.Name, Key: TitleNormalizer.Normalize(n.Name)))
            .Where(n => n.Key.Length > 0)
            .ToList();
        var songs = names
            .GroupBy(n => n.Key)
            .Select(g =>
                (Uuid: Guid.CreateVersion7(), Name: g.First().Name.Trim(), Members: g.ToList())
            )
            .ToList();

        await using var tx = await connection.BeginTransactionAsync();
        var store = await KbStore.BeginAsync(
            connection,
            tx,
            AuthorTypes.Importer,
            "lab-setlist-scaffold@1"
        );
        await store.WriteSongsAsync(
            songs.Select(s => new SongRecord(s.Uuid, artist, s.Name)).ToList()
        );
        await store.WriteAliasesAsync(
            songs.Select(s => new AliasRecord(artist, s.Name, s.Uuid)).ToList()
        );
        await store.WriteEvidenceAsync(
            songs
                .SelectMany(s => s.Members.Select(m => new EvidenceRecord(m.Uuid, s.Uuid)))
                .ToList()
        );
        await store.FinishAsync();
        await tx.CommitAsync();
    }

    /// What must hold for any catalogue, however it was built.
    private static async Task AssertInvariants(NpgsqlConnection connection)
    {
        var violations = await connection.QuerySingleAsync<(
            long Tracks,
            long Aliases,
            long Evidence,
            long Unlogged,
            long NoRun
        )>(
            """
            SELECT
                (SELECT count(*) FROM kb.track_songs ts JOIN source_tracks t ON t.uuid = ts.track_uuid
                 JOIN artists a ON a.id = t.artist_id JOIN kb.songs s ON s.uuid = ts.song_uuid
                 WHERE s.artist_uuid <> a.uuid),
                (SELECT count(*) FROM kb.song_aliases al JOIN kb.songs s ON s.uuid = al.song_uuid WHERE s.artist_uuid <> al.artist_uuid),
                (SELECT count(*) FROM kb.song_evidence e JOIN setlist_songs ss ON ss.uuid = e.setlist_song_uuid
                 JOIN artists a ON a.id = ss.artist_id JOIN kb.songs s ON s.uuid = e.song_uuid WHERE s.artist_uuid <> a.uuid),
                (SELECT count(*) FROM kb.songs s WHERE NOT EXISTS (
                    SELECT 1 FROM kb.changes c WHERE c.table_name = 'songs' AND c.row_key = jsonb_build_object('uuid', s.uuid))),
                (SELECT count(*) FROM kb.track_songs ts WHERE NOT EXISTS (SELECT 1 FROM kb.runs r WHERE r.uuid = ts.run_uuid))
            """
        );
        violations
            .Should()
            .Be(
                (0L, 0L, 0L, 0L, 0L),
                "tracks, aliases and evidence stay within an artist, and every row is logged and has a run"
            );
    }

    private static string Admin(string? url = null)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = 15432,
            Username = "relisten",
            Password = "local_dev_password",
            Database = "postgres",
        };
        if (url != null)
        {
            builder.Database = new Uri(url).AbsolutePath.Trim('/');
        }

        return builder.ConnectionString;
    }
}
