using System.Diagnostics;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Relisten;
using Relisten.Normalization;
using Relisten.Normalization.Songs;
using Relisten.Normalization.Titles;

namespace RelistenApiTests.Normalization;

/// The norm catalogue on real data: the lab database that local-dev/norm-lab.sh loads from the
/// production replica (a slice of artists across 1959–2026). Skipped unless
/// NORM_LAB_DATABASE_URL is set, e.g.
///   NORM_LAB_DATABASE_URL=postgresql://relisten:local_dev_password@127.0.0.1:15432/relisten_norm_lab
///   NORM_LAB_ARTISTS=phish,grateful-dead            (default: every artist in the lab)
/// The LLM run also needs OPENAI_API_KEY and NORM_LAB_MODEL (NORM_LAB_MAX_CHUNKS caps the calls
/// per artist, default 3); it writes to the lab itself so the results can be inspected.
[TestFixture, NonParallelizable, Category("NormLab")]
public class NormLab
{
    private string labUrl = null!;
    private string? previousWrite;
    private string? previousRead;

    [OneTimeSetUp]
    public void Start()
    {
        labUrl = Environment.GetEnvironmentVariable("NORM_LAB_DATABASE_URL") ?? "";
        if (labUrl.Length == 0)
        {
            Assert.Ignore("NORM_LAB_DATABASE_URL not set; load the lab with local-dev/norm-lab.sh");
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
        var wanted = (Environment.GetEnvironmentVariable("NORM_LAB_ARTISTS") ?? "").Split(
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
        var scratch = "relisten_norm_lab_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = new NpgsqlConnection(Admin()))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync(
                $"CREATE DATABASE {scratch} TEMPLATE {new NpgsqlConnectionStringBuilder(Admin(labUrl)).Database}"
            );
        }

        try
        {
            var db = Db(labUrl.Replace("/relisten_norm_lab", "/" + scratch));
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
                var match = new CatalogueMatch(
                    new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db)
                );
                var first = await match.RunAsync(uuid, CancellationToken.None);
                var elapsed = watch.Elapsed;

                var evaluation = await SongEvaluation.EvaluateAsync(connection, slug);
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
                    "SELECT r.uuid FROM norm.runs r WHERE r.author_ref = @Author AND EXISTS (SELECT 1 FROM norm.changes c WHERE c.run_uuid = r.uuid) ORDER BY r.started_at DESC LIMIT 1",
                    new { TrackMatcher.Author }
                );
                await using var tx = await connection.BeginTransactionAsync();
                var store = await NormStore.BeginAsync(
                    connection,
                    tx,
                    AuthorTypes.Moderator,
                    "lab"
                );
                var (reverted, skipped) = await store.RevertRunAsync(run);
                skipped.Should().Be(0);
                reverted.Should().BeGreaterThan(0);
                (
                    await connection.ExecuteScalarAsync<long>(
                        "SELECT count(*) FROM norm.track_songs WHERE run_uuid = @run",
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
        var model = Environment.GetEnvironmentVariable("NORM_LAB_MODEL");
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(key))
        {
            Assert.Ignore("NORM_LAB_MODEL and OPENAI_API_KEY are needed");
        }

        var maxChunks = int.TryParse(
            Environment.GetEnvironmentVariable("NORM_LAB_MAX_CHUNKS"),
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
        var llm = new CatalogueLlm(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db),
            new OpenAiCatalogueModel(configuration)
        );
        await using var connection = await Open(db);

        foreach (var (slug, uuid) in await Artists(connection))
        {
            var (chunks, input, output, songs) = (0, 0, 0, 0);
            var curve = new List<int>();
            var unsure = new List<ReviewItem>();
            var watch = Stopwatch.StartNew();
            var reviewOnly = Environment.GetEnvironmentVariable("NORM_LAB_REVIEW_ONLY") == "1";
            // NORM_LAB_MATCH_ONLY=1: no LLM, only the deterministic match (after an expert's edits).
            var matchOnly = Environment.GetEnvironmentVariable("NORM_LAB_MATCH_ONLY") == "1";
            // NORM_LAB_ONLY_KEYS_FILE: lines of "artist<TAB>key"; ask only about those keys.
            var onlyFile = Environment.GetEnvironmentVariable("NORM_LAB_ONLY_KEYS_FILE");
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
                // NORM_LAB_MAX_CHUNKS is the artist's whole budget, as in the workflow: the review
                // pass gets what the first pass left.
                for (var passChunks = 0; chunks < maxChunks; passChunks++)
                {
                    var chunk = await llm.RunChunkAsync(
                        uuid,
                        skip,
                        CancellationToken.None,
                        review,
                        only
                    );

                    if (chunk.Done)
                    {
                        break;
                    }

                    chunks++;
                    skip.AddRange(chunk.Asked);
                    curve.Add(chunk.TracksWritten);
                    // As in the workflow: an item stays until a later chunk asks about it again.
                    unsure.RemoveAll(u => chunk.Asked.Contains(u.Key));
                    unsure.AddRange(chunk.Review);

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

            await new CatalogueMatch(
                new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db)
            ).RunAsync(uuid, CancellationToken.None);
            var evaluation = await SongEvaluation.EvaluateAsync(connection, slug);
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

    /// Each artist's catalogue as it stands, with its alias count and the size of the change
    /// log. Reads only.
    [Test, Explicit("Prints the lab's numbers; run deliberately")]
    public async Task Evaluate()
    {
        await using var connection = await Open(Db(labUrl));
        foreach (var (slug, uuid) in await Artists(connection))
        {
            if (
                await connection.ExecuteScalarAsync<long>(
                    "SELECT count(*) FROM norm.songs WHERE artist_uuid = @uuid",
                    new { uuid }
                ) == 0
            )
            {
                continue;
            }

            var aliases = await connection.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM norm.song_aliases WHERE artist_uuid = @uuid",
                new { uuid }
            );
            TestContext.Out.WriteLine(
                $"{await SongEvaluation.EvaluateAsync(connection, slug)}, {aliases} aliases"
            );
        }

        TestContext.Out.WriteLine(
            "norm.changes: "
                + await connection.ExecuteScalarAsync<string>(
                    "SELECT count(*) || ' rows, ' || pg_size_pretty(pg_total_relation_size('norm.changes')) FROM norm.changes"
                )
        );
    }

    /// The song rollout gate: each artist's catalogue scored against a labelled sample of its
    /// tracks (NORM_LAB_GOLD, a tab-separated file kept outside the repo; SongGate.Read gives
    /// its shape). Lists every wrong and hidden track, and labelled names no song carries.
    /// Reads only, so NORM_LAB_DATABASE_URL may point at a read-only production connection.
    [Test, Explicit("Prints the song gate; run deliberately")]
    public async Task Gate()
    {
        var path = Environment.GetEnvironmentVariable("NORM_LAB_GOLD");
        if (string.IsNullOrEmpty(path))
        {
            Assert.Ignore("NORM_LAB_GOLD is needed");
        }

        var gold = SongGate.Read(path);
        await using var connection = await Open(Db(labUrl));
        foreach (var (slug, _) in await Artists(connection))
        {
            if (gold.All(g => g.Artist != slug))
            {
                continue;
            }

            var result = await SongGate.ScoreAsync(connection, slug, gold);
            TestContext.Out.WriteLine(result.ToString());
            foreach (
                var t in result.Tracks.Where(t =>
                    t.Verdict is GateVerdict.Wrong or GateVerdict.Hidden
                )
            )
            {
                TestContext.Out.WriteLine(
                    $"    {t.Verdict.ToString().ToLowerInvariant()}: \"{t.Gold.Title}\" is {string.Join(" > ", t.Gold.Parts.Select(p => string.Join(" | ", p)))}, got {t.Got} ({t.Gold.TrackUuid})"
                );
            }

            foreach (
                var t in result.Tracks.Where(t =>
                    t.Verdict is GateVerdict.Partial or GateVerdict.Open
                )
            )
            {
                TestContext.Out.WriteLine(
                    $"    {t.Verdict.ToString().ToLowerInvariant()}: \"{t.Gold.Title}\", got {t.Got}"
                );
            }

            if (result.NotInCatalogue.Count > 0)
            {
                TestContext.Out.WriteLine(
                    "    labelled but in no song or alias: "
                        + string.Join("; ", result.NotInCatalogue)
                );
            }
        }
    }

    /// What an LLM catalogue run would ask, before it runs (docs/llm.md rule 6): unresolved
    /// keys, groups and chunks for the first pass, and the recurring keys a review pass would
    /// see. No model call, no writes.
    [Test, Explicit("Prints a cost estimate; run deliberately")]
    public async Task Estimate()
    {
        var db = Db(labUrl);
        await using var connection = await Open(db);
        foreach (var (slug, uuid) in await Artists(connection))
        {
            var state = await CatalogueState.LoadAsync(connection, null, uuid);
            var keys = CatalogueLlm.UnresolvedKeys(state);
            var groups = TitleGrouper.Group(keys.Values.Select(k => k.Usage));
            var (chunks, left) = (0, groups.ToList());
            while (left.Count > 0)
            {
                var chunk = TitleGrouper.NextChunk(
                    left,
                    CatalogueLlm.ChunkGroups,
                    CatalogueLlm.ChunkKeys
                );
                left = left.Skip(chunk.Count).ToList();
                chunks++;
            }

            TestContext.Out.WriteLine(
                $"{slug}: {state.UnresolvedTracks.Count()} unresolved tracks, {keys.Count} keys in {groups.Count} groups → {chunks} first-pass chunks; "
                    + $"{keys.Values.Count(k => k.Usage.Shows >= CatalogueLlm.ReviewMinShows)} keys at ≥ {CatalogueLlm.ReviewMinShows} shows, "
                    + $"{keys.Values.Count(k => k.Usage.Shows < 2)} one-show keys; {state.Songs.Count} known songs"
            );
        }
    }

    /// The deterministic venue pass over the lab's artists, then where the venues stand:
    /// legacy rows mapped, venues created, rows left for the LLM, shows with a venue in norm.
    [Test, Explicit("Writes venues to the lab; run deliberately")]
    public async Task VenueMatch()
    {
        var db = Db(labUrl);
        await using var connection = await Open(db);
        var artists = (await Artists(connection)).Select(a => a.Uuid).ToList();
        var result = await new Relisten.Normalization.Venues.VenueMatch(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db)
        ).RunAsync(artists, CancellationToken.None);
        TestContext.Out.WriteLine(result.ToString());
        TestContext.Out.WriteLine(
            await connection.ExecuteScalarAsync<string>(
                """
                SELECT format('venues %s, legacy rows mapped %s of %s, shows with a venue %s of %s',
                    (SELECT count(*) FROM norm.venues WHERE merged_into IS NULL),
                    (SELECT count(*) FROM norm.venue_evidence), (SELECT count(*) FROM venues),
                    (SELECT count(*) FROM norm.show_venues), (SELECT count(*) FROM shows WHERE venue_id IS NOT NULL))
                """
            )
        );
    }

    /// The deterministic plan for venue names containing NORM_LAB_VENUE (nothing written):
    /// each row's locality, and whether it was created, placed or left in a family.
    [Test, Explicit("Prints a venue plan; run deliberately")]
    public async Task VenuePlan()
    {
        var filter = Environment.GetEnvironmentVariable("NORM_LAB_VENUE") ?? "";
        var db = Db(labUrl);
        await using var connection = await Open(db);
        var artists = (await Artists(connection)).Select(a => a.Uuid).ToList();
        var watch = Stopwatch.StartNew();
        var state = await Relisten.Normalization.Venues.VenueState.LoadAsync(
            connection,
            null,
            artists
        );
        TestContext.Out.WriteLine($"load {watch.Elapsed.TotalSeconds:F1}s");
        watch.Restart();
        var families = Relisten.Normalization.Venues.VenueMatcher.Families(
            state.Unmapped.Where(r => r.Key != null).ToList(),
            state.SameShowRows
        );
        TestContext.Out.WriteLine($"families {watch.Elapsed.TotalSeconds:F1}s ({families.Count})");
        watch.Restart();
        var plan = Relisten.Normalization.Venues.VenueMatcher.Match(state);
        TestContext.Out.WriteLine($"match {watch.Elapsed.TotalSeconds:F1}s");
        bool Wanted(Relisten.Normalization.Venues.LegacyVenue r) =>
            r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
        var placed = plan.Evidence.ToDictionary(e => e.LegacyVenueUuid, e => e.VenueId);
        var sizes = plan.Left.Select(f => f.Select(r => r.Key).Distinct().Count()).ToList();
        TestContext.Out.WriteLine(
            $"left families {sizes.Count}: names 1 {sizes.Count(n => n == 1)}, 2-5 {sizes.Count(n => n is >= 2 and <= 5)}, 6-20 {sizes.Count(n => n is > 5 and <= 20)}, >20 {sizes.Count(n => n > 20)}, largest {(sizes.Count > 0 ? sizes.Max() : 0)}"
        );
        foreach (var big in plan.Left.Where(f => f.Select(r => r.Key).Distinct().Count() > 40))
        {
            TestContext.Out.WriteLine(
                $"big family {big.Select(r => r.Key).Distinct().Count()} @ {string.Join(",", big.Select(r => r.Locality?.Key).Distinct().Take(3))}: {string.Join(" / ", big.Select(r => r.Key).Distinct().Take(40))}"
            );
        }

        foreach (var row in state.Unmapped.Where(Wanted))
        {
            var family = plan.Left.FirstOrDefault(f => f.Any(r => r.Uuid == row.Uuid));
            TestContext.Out.WriteLine(
                $"{row.Name} | {row.Location} → {row.Locality?.Key} : "
                    + (
                        placed.TryGetValue(row.Uuid, out var v) ? $"venue {v}"
                        : family != null
                            ? $"left, family of {family.Count}: {string.Join(" / ", family.Select(r => r.Key).Distinct().Take(8))}"
                        : "unplaced"
                    )
            );
        }
    }

    /// Official names for the lab's busiest venues the model never named (VenueHistory):
    /// NORM_LAB_MAX_CHUNKS requests of 50 venues with NORM_LAB_MIN_SHOWS shows or more, then
    /// what each asked venue is called now.
    [Test, Explicit("Calls OpenAI; run deliberately")]
    public async Task VenueHistory()
    {
        var model = Environment.GetEnvironmentVariable("NORM_LAB_MODEL");
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(key))
        {
            Assert.Ignore("NORM_LAB_MODEL and OPENAI_API_KEY are needed");
        }

        var calls = int.TryParse(
            Environment.GetEnvironmentVariable("NORM_LAB_MAX_CHUNKS"),
            out var c
        )
            ? c
            : 1;
        var minShows = int.TryParse(
            Environment.GetEnvironmentVariable("NORM_LAB_MIN_SHOWS"),
            out var m
        )
            ? m
            : 5;
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
        var history = new Relisten.Normalization.Venues.VenueHistory(
            new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db),
            new OpenAiCatalogueModel(configuration)
        );
        for (var i = 0; i < calls; i++)
        {
            var result = await history.RunChunkAsync(minShows, CancellationToken.None);
            TestContext.Out.WriteLine(result.ToString());
            if (result.Done)
            {
                break;
            }
        }

        await using var connection = await Open(db);
        foreach (
            var line in await connection.QueryAsync<string>(
                """
                SELECT v.name || ' — ' || v.city || ' — ' || coalesce(r.proposal, '') || ' [' || round(r.confidence::numeric, 2) || ']'
                FROM norm.review_items r JOIN norm.venues v ON v.id = r.venue_id
                WHERE r.key LIKE 'history:%' ORDER BY r.shows DESC NULLS LAST
                """
            )
        )
        {
            TestContext.Out.WriteLine("  " + line);
        }
    }

    /// Which rule links the names of one town (NORM_LAB_TOWN, a locality key): diagnosis
    /// for families that grow too large.
    [Test, Explicit("Prints a town's family links; run deliberately")]
    public async Task VenueTownLinks()
    {
        var town = Environment.GetEnvironmentVariable("NORM_LAB_TOWN") ?? "chicago|il|US";
        var db = Db(labUrl);
        await using var connection = await Open(db);
        var artists = (await Artists(connection)).Select(a => a.Uuid).ToList();
        var state = await Relisten.Normalization.Venues.VenueState.LoadAsync(
            connection,
            null,
            artists
        );
        var rows = state.Unmapped.Where(r => r.Key != null && r.Locality?.Key == town).ToList();
        var keys = rows.Select(r => r.Key!).Distinct().ToList();
        var locality = rows[0].Locality!;
        var townWords = Relisten.Normalization.Venues.VenueNormalizer.PlaceWords(locality);
        var rare = keys.SelectMany(k =>
                Relisten.Normalization.Venues.VenueNormalizer.Words(k).Distinct()
            )
            .GroupBy(w => w)
            .Where(g => g.Count() <= 3)
            .Select(g => g.Key)
            .ToHashSet();
        var counts = new Dictionary<string, int>();
        var samples = new Dictionary<string, List<string>>();
        void Count(string rule, string a, string b)
        {
            counts[rule] = counts.GetValueOrDefault(rule) + 1;
            (samples.TryGetValue(rule, out var l) ? l : samples[rule] = []).Add($"{a} ~ {b}");
        }
        var local = keys.Select(k =>
                Relisten.Normalization.Venues.VenueNormalizer.WithoutPlace(k, locality)
            )
            .Where(k => k != null)
            .Select(k => k!)
            .Distinct()
            .ToList();
        var byWord = local.Count <= Relisten.Normalization.Venues.VenueMatcher.WordLinkTownNames;
        for (var i = 0; i < local.Count; i++)
        for (var j = i + 1; j < local.Count; j++)
        {
            var (a, b) = (local[i], local[j]);
            if (Relisten.Normalization.Venues.VenueNormalizer.SameName(a, b))
                Count("same", a, b);
            else if (Relisten.Normalization.Venues.VenueMatcher.Alike(a, b))
                Count("alike", a, b);
            else if (
                byWord
                && Relisten.Normalization.Venues.VenueMatcher.ShareADistinctiveWord(
                    a,
                    b,
                    townWords,
                    rare
                )
            )
                Count("word", a, b);
        }
        var ids = rows.Select(r => r.Uuid).ToHashSet();
        TestContext.Out.WriteLine(
            $"{town}: {keys.Count} names, same-show links {state.SameShowRows.Count(p => ids.Contains(p.A) && ids.Contains(p.B))}"
        );
        foreach (var (rule, n) in counts)
        {
            TestContext.Out.WriteLine(
                $"  {rule}: {n} — {string.Join(" | ", samples[rule].OrderBy(_ => Guid.NewGuid()).Take(12))}"
            );
        }
    }

    /// The venue catalogue over the lab's artists, as the workflow runs it (match, LLM chunks
    /// up to NORM_LAB_MAX_CHUNKS, match), then the fixture places.
    [Test, Explicit("Calls OpenAI; run deliberately")]
    public async Task VenueCatalogue()
    {
        var model = Environment.GetEnvironmentVariable("NORM_LAB_MODEL");
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(key))
        {
            Assert.Ignore("NORM_LAB_MODEL and OPENAI_API_KEY are needed");
        }

        var maxChunks = int.TryParse(
            Environment.GetEnvironmentVariable("NORM_LAB_MAX_CHUNKS"),
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
        var database = new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db);
        var match = new Relisten.Normalization.Venues.VenueMatch(database);
        var llm = new Relisten.Normalization.Venues.VenueLlm(
            database,
            new OpenAiCatalogueModel(configuration)
        );
        await using var connection = await Open(db);
        var artists = (await Artists(connection)).Select(a => a.Uuid).ToList();
        var watch = Stopwatch.StartNew();

        TestContext.Out.WriteLine(
            "first match: " + await match.RunAsync(artists, CancellationToken.None)
        );
        var skip = new List<string>();
        var (chunks, input, output) = (0, 0, 0);
        while (chunks < maxChunks)
        {
            // As the workflow does: never more calls at once than the cap has left, and a
            // re-ask round only when it fits too.
            var remaining = maxChunks - chunks;
            var parallel = Math.Max(
                1,
                Math.Min(
                    int.TryParse(Environment.GetEnvironmentVariable("NORM_LAB_PARALLEL"), out var p)
                        ? p
                        : 1,
                    remaining
                )
            );
            var chunk = await llm.RunChunkAsync(
                artists,
                skip,
                CancellationToken.None,
                mayRetry: remaining >= 2 * parallel,
                parallel: parallel
            );
            if (chunk.Done)
            {
                break;
            }

            chunks += chunk.ModelCalls;
            skip.AddRange(chunk.Asked);
            (input, output) = (input + chunk.InputTokens, output + chunk.OutputTokens);
            TestContext.Out.WriteLine(
                $"  chunk {chunks}: {chunk.Resolved} placed, {chunk.Unsure} unsure, {chunk.VenuesCreated} new venues, {chunk.RowsMapped} rows ({watch.Elapsed.TotalSeconds:F0}s)"
            );
        }

        TestContext.Out.WriteLine(
            "last match: " + await match.RunAsync(artists, CancellationToken.None)
        );
        TestContext.Out.WriteLine(
            $"{model}: {chunks} chunks, {input} in / {output} out tokens, {watch.Elapsed.TotalSeconds:F0}s"
        );
        TestContext.Out.WriteLine(
            await connection.ExecuteScalarAsync<string>(
                """
                SELECT format('venues %s (%s rooms), legacy rows mapped %s of %s, shows with a venue %s of %s, open venue questions %s',
                    (SELECT count(*) FROM norm.venues WHERE merged_into IS NULL),
                    (SELECT count(*) FROM norm.venues WHERE merged_into IS NULL AND kind = 'room'),
                    (SELECT count(*) FROM norm.venue_evidence), (SELECT count(*) FROM venues),
                    (SELECT count(*) FROM norm.show_venues), (SELECT count(*) FROM shows WHERE venue_id IS NOT NULL),
                    (SELECT count(*) FROM norm.review_items WHERE subject = 'venue' AND resolved_at IS NULL))
                """
            )
        );
        foreach (
            var line in await connection.QueryAsync<string>(
                """
                SELECT v.name || ' [' || v.kind || coalesce(' in ' || p.name, '') || '] ' || coalesce(v.city, '?') || ', ' || coalesce(v.region, v.country, '')
                       || ' — ' || (SELECT string_agg(n.name || coalesce(' ' || n.valid_from || '…' || coalesce(n.valid_to::text, ''), '') || ' (' || n.kind || ')', '; ' ORDER BY n.kind, n.valid_from) FROM norm.venue_names n WHERE n.venue_id = v.id)
                       || ' — ' || (SELECT count(*) FROM norm.show_venues sv WHERE sv.venue_id = v.id) || ' shows'
                FROM norm.venues v LEFT JOIN norm.venues p ON p.id = v.parent_id
                WHERE v.merged_into IS NULL AND (v.name ~* '(madison square|spectrum|wells fargo|wachovia|first union|corestates|fox thea|red rocks|fillmore|winterland|warfield)')
                ORDER BY v.city, v.name
                """
            )
        )
        {
            TestContext.Out.WriteLine("  " + line);
        }
    }

    /// What the venue pass would do before any model call: venues the deterministic step
    /// creates, families left for the LLM, and duplicate names (one name in two or more venues
    /// of one state, among existing and new venues), the number to drive down first.
    [Test, Explicit("Prints the venue workload; run deliberately")]
    public async Task VenueEstimate()
    {
        var db = Db(labUrl);
        await using var connection = await Open(db);
        var artists = (await Artists(connection)).Select(a => a.Uuid).ToList();
        var state = await Relisten.Normalization.Venues.VenueState.LoadAsync(
            connection,
            null,
            artists
        );
        var plan = Relisten.Normalization.Venues.VenueMatcher.Match(state);
        var rows = plan.Left.Sum(f => f.Count);
        TestContext.Out.WriteLine(
            $"rows {state.Unmapped.Count}: placed {plan.Evidence.Count}, new venues {plan.NewVenues.Count}, "
                + $"left {rows} rows in {plan.Left.Count} families ({plan.Left.Sum(f => f.Select(r => (r.Key, r.Locality?.Key)).Distinct().Count())} spellings), unplaced {plan.Unplaced.Count}"
        );
        var venues = state
            .Venues.Values.Select(v =>
                (
                    v.Name,
                    v.City,
                    Region: v.LocalityKey == null
                        ? null
                        : new Relisten.Normalization.Venues.Locality(
                            v.City!,
                            v.Region,
                            v.Country
                        ).RegionKey
                )
            )
            .Concat(
                plan.NewVenues.Select(v =>
                    (
                        v.Name,
                        v.City,
                        Region: new Relisten.Normalization.Venues.Locality(
                            v.City!,
                            v.Region,
                            v.Country
                        ).RegionKey
                    )
                )
            )
            .ToList();
        var duplicates = venues
            .GroupBy(v => (Relisten.Normalization.Venues.VenueNormalizer.Key(v.Name), v.Region))
            .Where(g => g.Count() > 1)
            .ToList();
        TestContext.Out.WriteLine(
            $"duplicate names within a state: {duplicates.Count} ({duplicates.Sum(g => g.Count())} venues): "
                + string.Join(
                    " | ",
                    duplicates
                        .Take(12)
                        .Select(g => string.Join(", ", g.Select(v => $"{v.Name} ({v.City})")))
                )
        );
        foreach (var family in plan.Left.OrderByDescending(f => f.Count).Take(5))
        {
            TestContext.Out.WriteLine(
                $"  family of {family.Count}: {string.Join(" | ", family.GroupBy(r => (r.Key, r.Locality?.Key)).OrderByDescending(g => g.Sum(r => r.Uses)).Take(8).Select(g => $"{g.First().Name} — {g.First().Location}"))}"
            );
        }
    }

    /// One-off, for a lab built before 2026-10-20 ("song names, not titles"): removes the
    /// machine aliases the new rule wouldn't have stored (a song's own name, a title with a
    /// track number or notes around the name), then rebuilds every artist's track rows. A row
    /// the rebuild can't derive stays a ruling.
    [Test, Explicit("Rewrites the lab's catalogue; run deliberately")]
    public async Task PruneAliasesToSongNames()
    {
        var db = Db(labUrl);
        await using var connection = await Open(db);
        foreach (var (slug, uuid) in await Artists(connection))
        {
            var names = (
                await connection.QueryAsync<(Guid Uuid, string Name)>(
                    "SELECT id, name FROM norm.songs WHERE artist_uuid = @uuid AND merged_into IS NULL",
                    new { uuid }
                )
            ).ToDictionary(s => s.Uuid, s => TitleNormalizer.Normalize(s.Name));
            if (names.Count == 0)
            {
                continue;
            }

            var aliases = (
                await connection.QueryAsync<(
                    string Normalized,
                    string Alias,
                    Guid Song,
                    string AuthorType,
                    float? Confidence
                )>(
                    "SELECT normalized, alias, song_id, author_type, confidence FROM norm.song_aliases WHERE artist_uuid = @uuid",
                    new { uuid }
                )
            ).ToList();
            var known = names.Values.Concat(aliases.Select(a => a.Normalized)).ToHashSet();

            await using var tx = await connection.BeginTransactionAsync();
            var store = await NormStore.BeginAsync(
                connection,
                tx,
                AuthorTypes.Algorithm,
                "alias-prune@1",
                "song names, not titles"
            );
            var removed = 0;
            foreach (var alias in aliases.Where(a => !AuthorTypes.IsHuman(a.AuthorType)))
            {
                var parts = TitleParser.Parse(alias.Alias).Parts;
                var cleaned =
                    parts.Count == 1 ? TrackNumberFold.Fold(parts[0].Key, known.Contains) : "";
                if (cleaned == alias.Normalized && cleaned != names.GetValueOrDefault(alias.Song))
                {
                    continue;
                }

                // Not re-keyed under its cleaned spelling: that is usually title noise the
                // parser can't see ("01 freeborn man set1"), or loses a qualifier ("pinball
                // wizard tease"). Its tracks keep their rows as rulings.
                await store.DeleteAliasAsync(uuid, alias.Alias);
                removed++;
            }

            await store.FinishAsync();
            await tx.CommitAsync();

            var rebuilt = await new CatalogueMatch(
                new Relisten.Services.Normalization.DbServiceNormalizationDatabase(db)
            ).RunAsync(uuid, CancellationToken.None);
            TestContext.Out.WriteLine(
                $"{slug}: {aliases.Count} aliases, {removed} removed; rebuild touched {rebuilt.TracksWritten} tracks"
            );
        }
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
        var dir = Environment.GetEnvironmentVariable("NORM_LAB_REPORT_DIR");
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
            SELECT s.name, s.kind, s.original_artist, (SELECT count(DISTINCT ts.track_uuid) FROM norm.track_songs ts WHERE ts.song_id = s.id)
            FROM norm.songs s WHERE s.artist_uuid = @artist ORDER BY 4 DESC, 1
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
            FROM norm.track_songs ts JOIN source_tracks t ON t.uuid = ts.track_uuid
            LEFT JOIN norm.songs s ON s.id = ts.song_id
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
            .Select(g => (Id: SongId.New(), Name: g.First().Name.Trim(), Members: g.ToList()))
            .ToList();

        await using var tx = await connection.BeginTransactionAsync();
        var store = await NormStore.BeginAsync(
            connection,
            tx,
            AuthorTypes.Importer,
            "lab-setlist-scaffold@1"
        );
        await store.WriteSongsAsync(
            songs.Select(s => new SongRecord(s.Id, artist, s.Name)).ToList()
        );
        await store.WriteAliasesAsync(
            songs.Select(s => new AliasRecord(artist, s.Name, s.Id)).ToList()
        );
        await store.WriteEvidenceAsync(
            songs.SelectMany(s => s.Members.Select(m => new EvidenceRecord(m.Uuid, s.Id))).ToList()
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
                (SELECT count(*) FROM norm.track_songs ts JOIN source_tracks t ON t.uuid = ts.track_uuid
                 JOIN artists a ON a.id = t.artist_id JOIN norm.songs s ON s.id = ts.song_id
                 WHERE s.artist_uuid <> a.uuid),
                (SELECT count(*) FROM norm.song_aliases al JOIN norm.songs s ON s.id = al.song_id WHERE s.artist_uuid <> al.artist_uuid),
                (SELECT count(*) FROM norm.song_evidence e JOIN setlist_songs ss ON ss.uuid = e.setlist_song_uuid
                 JOIN artists a ON a.id = ss.artist_id JOIN norm.songs s ON s.id = e.song_id WHERE s.artist_uuid <> a.uuid),
                (SELECT count(*) FROM norm.songs s WHERE NOT EXISTS (
                    SELECT 1 FROM norm.changes c WHERE c.table_name = 'songs' AND c.row_key = jsonb_build_object('uuid', s.uuid))),
                (SELECT count(*) FROM norm.track_songs ts WHERE NOT EXISTS (SELECT 1 FROM norm.runs r WHERE r.uuid = ts.run_uuid))
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
