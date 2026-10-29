using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Relisten;
using Relisten.Normalization;
using Relisten.Normalization.Songs;
using Relisten.Normalization.Venues;
using Relisten.Services.Normalization;

namespace RelistenApiTests.Normalization;

/// A venue model that answers New York from the request alone: Madison Square Garden (short
/// name MSG) is a new site, its Theater a room inside it.
/// twin: it also proposes Madison Square Garden a second time, for "MSG". stages: the Garden
/// and the Beacon Theatre each get a room named "Main Stage" ("MSG" goes to the Beacon's).
/// forgetful: its first answer leaves "MSG" out. sloppy: its first answer places "MSG" on a
/// new venue it never declares.
internal sealed class ScriptedVenueModel(
    bool twin = false,
    bool stages = false,
    bool forgetful = false,
    bool sloppy = false
) : ICatalogueModel
{
    public int Calls { get; private set; }

    public string ModelId => "scripted-venues-1";

    public Task<ModelReply> CompleteAsync(
        string instructions,
        string data,
        string schemaName,
        string schemaJson,
        CancellationToken cancellationToken
    )
    {
        var newVenues = new JsonArray();
        var spellings = new JsonArray();
        foreach (var locality in JsonNode.Parse(data)!["localities"]!.AsArray())
        {
            var l = (string)locality!["id"]!;
            newVenues.Add(Venue("n1", l, "Madison Square Garden", "site", null, ("MSG", "short")));
            newVenues.Add(
                Venue(
                    "n2",
                    l,
                    "Hulu Theater",
                    "room",
                    "n1",
                    ("The Theater at Madison Square Garden", "official")
                )
            );
            if (stages)
            {
                newVenues.Add(
                    Venue("n4", l, "Beacon Theatre", "site", null, ("The Beacon", "short"))
                );
                newVenues.Add(
                    Venue("n5", l, "Main Stage", "room", "n1", ("Garden Stage", "short"))
                );
                newVenues.Add(
                    Venue("n6", l, "Main Stage", "room", "n4", ("Beacon Stage", "short"))
                );
            }

            if (twin)
            {
                newVenues.Add(
                    Venue("n3", l, "Madison Square Garden", "site", null, ("The Garden", "short"))
                );
            }

            foreach (var s in locality["spellings"]!.AsArray())
            {
                var text = (string)s!["text"]!;
                if (forgetful && Calls == 0 && text == "MSG")
                {
                    continue;
                }

                spellings.Add(
                    new JsonObject
                    {
                        ["id"] = (string)s["id"]!,
                        ["spelling_text"] = text,
                        ["venue"] =
                            sloppy && Calls == 0 && text == "MSG" ? "n9"
                            : stages && text.Contains("Theater") ? "n5"
                            : stages && text == "MSG" ? "n6"
                            : text.Contains("Theater") ? "n2"
                            : twin && text == "MSG" ? "n3"
                            : "n1",
                        ["not_a_venue"] = false,
                        ["confidence"] = 0.95,
                        ["rationale"] = "scripted",
                    }
                );
            }
        }

        var reply = new JsonObject
        {
            ["new_venues"] = newVenues,
            ["spellings"] = spellings,
            ["known_names"] = new JsonArray(),
        };
        Calls++;
        return Task.FromResult(new ModelReply(reply.ToJsonString(), 100, 50));
    }

    private static JsonObject Venue(
        string id,
        string locality,
        string name,
        string kind,
        string? parent,
        (string Name, string Kind) other
    ) =>
        new()
        {
            ["id"] = id,
            ["locality"] = locality,
            ["name"] = name,
            ["kind"] = kind,
            ["parent"] = parent,
            ["names"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = other.Name,
                    ["kind"] = other.Kind,
                    ["from"] = other.Kind == "official" ? "1991" : null,
                    ["to"] = other.Kind == "official" ? "2018-09" : null,
                }
            ),
        };
}

/// A history model that knows Red Rocks (sure, or not when unsure is set).
internal sealed class ScriptedHistoryModel(bool unsure = false) : ICatalogueModel
{
    public int Calls { get; private set; }
    public string ModelId => "scripted-history-1";

    public Task<ModelReply> CompleteAsync(
        string instructions,
        string data,
        string schemaName,
        string schemaJson,
        CancellationToken cancellationToken
    )
    {
        Calls++;
        var venues = new JsonArray();
        foreach (var v in JsonNode.Parse(data)!["venues"]!.AsArray())
        {
            venues.Add(
                new JsonObject
                {
                    ["id"] = (string)v!["id"]!,
                    ["names"] = new JsonArray(
                        new JsonObject
                        {
                            ["name"] = "Red Rocks Park & Amphitheatre",
                            ["kind"] = "official",
                            ["from"] = "1941",
                            ["to"] = null,
                        },
                        new JsonObject
                        {
                            ["name"] = "Red Rocks",
                            ["kind"] = "short",
                            ["from"] = null,
                            ["to"] = null,
                        }
                    ),
                    ["confidence"] = unsure ? 0.6 : 0.95,
                    ["rationale"] = "scripted",
                }
            );
        }

        return Task.FromResult(
            new ModelReply(new JsonObject { ["venues"] = venues }.ToJsonString(), 80, 40)
        );
    }
}

/// The scripted venue model, checking on another connection that no one holds the venue
/// lock while it is called.
internal sealed class LockProbeModel(string connectionString) : ICatalogueModel
{
    private readonly ScriptedVenueModel inner = new();
    public List<bool> LockFree { get; } = [];
    public string ModelId => inner.ModelId;

    public async Task<ModelReply> CompleteAsync(
        string instructions,
        string data,
        string schemaName,
        string schemaJson,
        CancellationToken cancellationToken
    )
    {
        await using var probe = new Npgsql.NpgsqlConnection(connectionString);
        await probe.OpenAsync(cancellationToken);
        var free = await probe.ExecuteScalarAsync<bool>(
            "SELECT pg_try_advisory_lock(hashtext('norm-venues'))"
        );
        if (free)
        {
            await probe.ExecuteAsync("SELECT pg_advisory_unlock(hashtext('norm-venues'))");
        }

        LockFree.Add(free);
        return await inner.CompleteAsync(
            instructions,
            data,
            schemaName,
            schemaJson,
            cancellationToken
        );
    }
}

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public class TestVenueCatalogue
{
    private DbService db = null!;
    private readonly List<Guid> artists = [];

    [OneTimeSetUp]
    public async Task Start()
    {
        MigratedTestDatabase.RequireLocalDatabase();
        await (await MigratedTestDatabase.OpenAsync()).DisposeAsync();
        db = new DbService(
            LocalTestDatabase.DatabaseUrl,
            null,
            null,
            Host.CreateApplicationBuilder().Environment,
            NullLogger<DbService>.Instance
        );
    }

    /// Two artists, each with its own legacy rows for the same places.
    [SetUp]
    public async Task Seed()
    {
        artists.Clear();
        var rows = new[]
        {
            (0, "Madison Square Garden", "New York, NY", "1994-12-31"),
            (0, "MSG", "NY, NY", "1995-12-31"),
            (1, "Madison Square Garden", "New York City, NY", "1997-12-31"),
            (1, "The Theater at Madison Square Garden", "New York, NY", "2003-02-24"),
            (0, "Red Rocks Amphitheatre", "Morrison, CO", "1996-08-11"),
            (1, "Red Rocks Amphitheatre", "Morrison, CO", "2001-08-04"),
            (1, "Red Rocks Amphitheater", "Morrison, Colorado", "2004-06-25"),
        };
        foreach (var _ in new[] { 0, 1 })
        {
            var uuid = Guid.NewGuid();
            artists.Add(uuid);
        }

        // Venues are cross-artist: each test starts from none.
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                DO $$
                DECLARE r uuid := gen_random_uuid();
                BEGIN
                    INSERT INTO norm.runs(uuid, author_type, author_ref) VALUES (r, 'moderator', 'test-reset@1');
                    PERFORM set_config('norm.author_type', 'moderator', true),
                            set_config('norm.author_ref', 'test-reset@1', true),
                            set_config('norm.run_uuid', r::text, true);
                    DELETE FROM norm.review_items WHERE subject = 'venue';
                    DELETE FROM norm.show_venues;
                    DELETE FROM norm.venue_evidence;
                    DELETE FROM norm.venue_external_ids;
                    DELETE FROM norm.venue_names;
                    UPDATE norm.venues SET parent_id = NULL, merged_into = NULL
                    WHERE parent_id IS NOT NULL OR merged_into IS NOT NULL;
                    DELETE FROM norm.venues;
                END $$
                """
            )
        );

        await db.WithWriteConnection(async c =>
        {
            var ids = new List<int>();
            foreach (var uuid in artists)
            {
                ids.Add(
                    await c.QuerySingleAsync<int>(
                        "INSERT INTO artists(name, slug, uuid, updated_at) VALUES ('Venue Band', @slug, @uuid, now()) RETURNING id",
                        new { slug = "venues-" + uuid, uuid }
                    )
                );
            }

            foreach (var (artist, name, location, date) in rows)
            {
                var venue = await c.QuerySingleAsync<long>(
                    "INSERT INTO venues(artist_id, name, location, uuid, updated_at) VALUES (@artist, @name, @location, @uuid, now()) RETURNING id",
                    new
                    {
                        artist = ids[artist],
                        name,
                        location,
                        uuid = Guid.NewGuid(),
                    }
                );
                await c.ExecuteAsync(
                    "INSERT INTO shows(artist_id, venue_id, date, display_date, uuid, updated_at) VALUES (@artist, @venue, @date::date, @date, @uuid, now())",
                    new
                    {
                        artist = ids[artist],
                        venue,
                        date,
                        uuid = Guid.NewGuid(),
                    }
                );
            }

            return 0;
        });
    }

    private Task<IEnumerable<string>> Strings(string sql) =>
        db.WithWriteConnection(c => c.QueryAsync<string>(sql, new { artists = artists.ToArray() }));

    [Test]
    public async Task OnePlacePerVenueAcrossArtistsWithItsRoomsAndNames()
    {
        var database = new DbServiceNormalizationDatabase(db);
        var match = new VenueMatch(database);
        var llm = new VenueLlm(database, new ScriptedVenueModel());

        // An earlier run's open question about "MSG", answered by this run's chunk.
        await db.WithWriteConnection(async c =>
        {
            var connection = (Npgsql.NpgsqlConnection)c;
            await using var tx = await connection.BeginTransactionAsync();
            var bot = await Relisten.Normalization.NormStore.BeginAsync(
                connection,
                tx,
                "llm",
                "venue-llm@1"
            );
            await bot.WriteReviewItemsAsync([
                new Relisten.Normalization.ReviewRecord(
                    null,
                    "MSG — New York, NY, US",
                    "msg@new york|ny|US",
                    Subject: Relisten.Normalization.ReviewSubjects.Venue
                ),
            ]);
            await tx.CommitAsync();
            return 0;
        });

        var first = await match.RunAsync(artists, CancellationToken.None);
        // Red Rocks has one spelling in Morrison; New York has three, for the model.
        first.VenuesCreated.Should().Be(1);
        first.RowsLeft.Should().Be(4);
        var since = await db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<DateTime>("SELECT clock_timestamp()")
        );
        var chunk = await llm.RunChunkAsync(artists, [], CancellationToken.None);
        chunk.VenuesCreated.Should().Be(2);
        (
            await db.WithWriteConnection(c =>
                c.QueryAsync<string>(
                    "SELECT job || ' ' || (run_uuid IS NOT NULL)::text || ' ' || jsonb_array_length(outcome->'accepted') FROM norm.model_calls WHERE called_at >= @since AND job = 'venues'",
                    new { since }
                )
            )
        )
            .Should()
            .Equal($"venues true {chunk.Resolved}");
        (await llm.RunChunkAsync(artists, chunk.Asked, CancellationToken.None))
            .Done.Should()
            .BeTrue();
        var last = await match.RunAsync(artists, CancellationToken.None);

        (
            await Strings(
                """
                SELECT v.name || ' (' || v.kind || coalesce(', in ' || p.name, '') || ') ' || v.city
                FROM norm.venues v LEFT JOIN norm.venues p ON p.id = v.parent_id
                WHERE v.id IN (SELECT e.venue_id FROM norm.venue_evidence e JOIN venues lv ON lv.uuid = e.legacy_venue_uuid
                                 JOIN artists a ON a.id = lv.artist_id WHERE a.uuid = ANY(@artists))
                   OR v.id IN (SELECT parent_id FROM norm.venues)
                ORDER BY 1
                """
            )
        ).Should().Equal("Hulu Theater (room, in Madison Square Garden) New York", "Madison Square Garden (site) New York", "Red Rocks Amphitheatre (site) Morrison");

        // Every show has its venue in norm, and the Theater's shows read the name of their date.
        (
            await Strings(
                """
                SELECT sh.display_date || ' ' || norm.venue_name_on(sv.venue_id, sh.date) || ' (' || sv.basis || ')'
                FROM shows sh JOIN artists a ON a.id = sh.artist_id
                LEFT JOIN norm.show_venues sv ON sv.show_uuid = sh.uuid
                WHERE a.uuid = ANY(@artists) ORDER BY sh.date
                """
            )
        )
            .Should()
            .Equal(
                "1994-12-31 Madison Square Garden (evidence)",
                "1995-12-31 Madison Square Garden (evidence)",
                "1996-08-11 Red Rocks Amphitheatre (evidence)",
                "1997-12-31 Madison Square Garden (evidence)",
                "2001-08-04 Red Rocks Amphitheatre (evidence)",
                "2003-02-24 The Theater at Madison Square Garden (evidence)",
                "2004-06-25 Red Rocks Amphitheatre (evidence)"
            );
        last.RowsLeft.Should().Be(0);
        (
            await Strings(
                "SELECT (resolved_at IS NOT NULL)::text FROM norm.review_items WHERE key = 'msg@new york|ny|US'"
            )
        )
            .Should()
            .Equal("true");

        // A show whose legacy venue is cleared loses its derived venue.
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE shows SET venue_id = NULL WHERE display_date = '2004-06-25' AND artist_id IN (SELECT id FROM artists WHERE uuid = ANY(@artists))",
                new { artists = artists.ToArray() }
            )
        );
        await match.RunAsync(artists, CancellationToken.None);
        (
            await Strings(
                "SELECT count(*)::text FROM norm.show_venues sv JOIN shows sh ON sh.uuid = sv.show_uuid WHERE sh.display_date = '2004-06-25'"
            )
        )
            .Should()
            .Equal("0");
    }

    [Test]
    public async Task AProposedTwinOfAVenueIsThatVenue()
    {
        var database = new DbServiceNormalizationDatabase(db);
        await new VenueMatch(database).RunAsync(artists, CancellationToken.None);
        var chunk = await new VenueLlm(database, new ScriptedVenueModel(twin: true)).RunChunkAsync(
            artists,
            [],
            CancellationToken.None
        );

        chunk.VenuesCreated.Should().Be(2);
        (
            await Strings(
                """
                SELECT n.name || ' (' || n.kind || ')' FROM norm.venues v JOIN norm.venue_names n ON n.venue_id = v.id
                WHERE v.name = 'Madison Square Garden' ORDER BY 1
                """
            )
        ).Should().Equal("MSG (short)", "Madison Square Garden (official)", "The Garden (short)");
    }

    [Test]
    public async Task OneNameInTwoTownsOfAStateIsAQuestionUntilAPersonAnswersIt()
    {
        var database = new DbServiceNormalizationDatabase(db);
        async Task As(
            string authorType,
            string authorRef,
            Func<Relisten.Normalization.NormStore, Task> write
        )
        {
            await db.WithWriteConnection(async c =>
            {
                var connection = (Npgsql.NpgsqlConnection)c;
                await using var tx = await connection.BeginTransactionAsync();
                await write(
                    await Relisten.Normalization.NormStore.BeginAsync(
                        connection,
                        tx,
                        authorType,
                        authorRef
                    )
                );
                await tx.CommitAsync();
                return 0;
            });
        }

        await As(
            AuthorTypes.Llm,
            "venue-llm@5",
            store =>
                store.WriteVenuesAsync([
                    new VenueRecord(
                        VenueId.New(),
                        "Cat's Cradle",
                        City: "Carrboro",
                        Region: "NC",
                        Country: "US",
                        Confidence: 0.9f
                    ),
                    new VenueRecord(
                        VenueId.New(),
                        "Cat's Cradle",
                        City: "Chapel Hill",
                        Region: "NC",
                        Country: "US",
                        Confidence: 0.9f
                    ),
                ])
        );
        var match = new VenueMatch(database);
        await match.RunAsync(artists, CancellationToken.None);
        await match.RunAsync(artists, CancellationToken.None);
        const string open =
            "SELECT title FROM norm.review_items WHERE key LIKE 'same:%' AND resolved_at IS NULL";
        (await Strings(open)).Should().ContainSingle().Which.Should().StartWith("Cat's Cradle — ");

        // A typed question about both venues.
        (
            await Strings(
                "SELECT question || ' ' || jsonb_array_length(refs->'venues') FROM norm.review_items WHERE key LIKE 'same:%' AND resolved_at IS NULL"
            )
        )
            .Should()
            .Equal("namesake 2");

        // "Two places": answered, and not asked again.
        var item = Guid.Parse(
            (
                await Strings(
                    "SELECT uuid::text FROM norm.review_items WHERE key LIKE 'same:%' AND resolved_at IS NULL"
                )
            ).Single()
        );
        await As(
            AuthorTypes.User,
            "expert",
            store =>
                store.AnswerReviewItemAsync(
                    item,
                    new System.Text.Json.Nodes.JsonObject { ["action"] = "two_places" }
                )
        );
        (
            await Strings(
                "SELECT resolution || ' ' || (answer->>'action') FROM norm.review_items WHERE uuid = '"
                    + item
                    + "'"
            )
        )
            .Should()
            .Equal("two_places two_places");
        await match.RunAsync(artists, CancellationToken.None);
        (await Strings(open)).Should().BeEmpty();
    }

    [Test]
    public async Task RoomsOfOneNameInDifferentSitesStayApart()
    {
        var database = new DbServiceNormalizationDatabase(db);
        await new VenueMatch(database).RunAsync(artists, CancellationToken.None);
        await new VenueLlm(database, new ScriptedVenueModel(stages: true)).RunChunkAsync(
            artists,
            [],
            CancellationToken.None
        );

        (
            await Strings(
                "SELECT r.name || ' in ' || p.name FROM norm.venues r JOIN norm.venues p ON p.id = r.parent_id ORDER BY 1"
            )
        )
            .Should()
            .Equal("Main Stage in Beacon Theatre", "Main Stage in Madison Square Garden");
    }

    private Task<Guid> Row(string name, string location) =>
        db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                """
                SELECT v.uuid FROM venues v JOIN artists a ON a.id = v.artist_id
                WHERE a.uuid = ANY(@artists) AND v.name = @name AND v.location = @location
                """,
                new
                {
                    artists = artists.ToArray(),
                    name,
                    location,
                }
            )
        );

    private async Task<VenueId?> PlacedOn(Guid row) =>
        await db.WithWriteConnection(c =>
            c.QuerySingleOrDefaultAsync<string?>(
                "SELECT venue_id FROM norm.venue_evidence WHERE legacy_venue_uuid = @row",
                new { row }
            )
        )
            is { } id
            ? VenueId.Parse(id)
            : null;

    [Test]
    public async Task ARematchPlacesAnOlderMatchersRowsAgainAndRetiresWhatTheyLeft()
    {
        var database = new DbServiceNormalizationDatabase(db);
        var match = new VenueMatch(database);
        await match.RunAsync(artists, CancellationToken.None);
        var row = await Row("Red Rocks Amphitheater", "Morrison, Colorado");
        var redRocks = (await PlacedOn(row))!.Value;

        // An older matcher put the row on a venue of its own in the wrong town, and pinned the
        // row's provider id there (the matcher trusts an id before anything else).
        var denver = VenueId.New();
        await db.WithWriteConnection(async c =>
        {
            var connection = (Npgsql.NpgsqlConnection)c;
            await c.ExecuteAsync(
                "UPDATE venues SET upstream_identifier = 'setlistfm:6bd6ca6e' WHERE uuid = @row",
                new { row }
            );
            await using var tx = await connection.BeginTransactionAsync();
            var old = await NormStore.BeginAsync(
                connection,
                tx,
                AuthorTypes.Algorithm,
                "venue-matcher@2.12.0"
            );
            await old.WriteVenuesAsync([
                new VenueRecord(
                    denver,
                    "Red Rocks Amphitheater",
                    City: "Denver",
                    Region: "CO",
                    Country: "US",
                    Confidence: 0.95f
                ),
            ]);
            await old.WriteVenueNamesAsync([
                new VenueNameRecord(denver, "Red Rocks Amphitheater", Confidence: 0.95f),
            ]);
            await old.WriteVenueExternalIdsAsync([
                new VenueExternalIdRecord("setlistfm", "6bd6ca6e", denver, 0.95f),
            ]);
            await old.WriteVenueEvidenceAsync([new VenueEvidenceRecord(row, denver, 0.95f)]);
            await old.FinishAsync();
            await tx.CommitAsync();
            return 0;
        });
        await match.RunAsync(artists, CancellationToken.None);

        var forget = await match.PlanForgetAsync(
            artists,
            [],
            rematch: true,
            CancellationToken.None
        );
        (await match.ForgetAsync(forget.Rows, reask: false, CancellationToken.None)).Should().Be(1);
        // A retried forget changes nothing; the plan it ran from still names the row.
        (await match.ForgetAsync(forget.Rows, reask: false, CancellationToken.None))
            .Should()
            .Be(0);
        await match.RunAsync(artists, CancellationToken.None);
        var retired = await match.RetireAsync(forget.Venues, CancellationToken.None);

        // Only the older matcher's row was forgotten; it is back on the venue it had (its id
        // went with the forget), its show with it, and the venue it left is gone. The current
        // matcher's rows stayed put.
        forget.Rows.Should().Equal(row);
        forget.Held.Should().BeEmpty();
        forget.Venues.Should().Equal(denver);
        (await PlacedOn(row)).Should().Be(redRocks);
        retired.Should().Be(1);
        (await Strings("SELECT city FROM norm.venues WHERE name LIKE 'Red Rocks%'"))
            .Should()
            .Equal("Morrison");
        (
            await Strings(
                """
                SELECT sv.venue_id::text FROM norm.show_venues sv JOIN shows s ON s.uuid = sv.show_uuid
                WHERE s.date = '2004-06-25'
                """
            )
        ).Should().Equal(redRocks.ToString());
    }

    [Test]
    public async Task ARematchOfEveryRowKeepsEveryVenue()
    {
        var database = new DbServiceNormalizationDatabase(db);
        var match = new VenueMatch(database);
        await match.RunAsync(artists, CancellationToken.None);
        const string placements = """
            SELECT e.legacy_venue_uuid::text || '>' || e.venue_id::text FROM norm.venue_evidence e
            JOIN venues v ON v.uuid = e.legacy_venue_uuid JOIN artists a ON a.id = v.artist_id
            WHERE a.uuid = ANY(@artists) ORDER BY 1
            """;
        var before = (await Strings(placements)).ToList();
        var venues = (await Strings("SELECT id FROM norm.venues ORDER BY 1")).ToList();
        before.Should().NotBeEmpty();

        // Every placement as an older matcher's, so a rematch forgets them all: each venue
        // loses every row at once and must still be found again by its names.
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                DO $$
                DECLARE r uuid := gen_random_uuid();
                BEGIN
                    INSERT INTO norm.runs(uuid, author_type, author_ref) VALUES (r, 'algorithm', 'venue-matcher@2.12.0');
                    PERFORM set_config('norm.author_type', 'algorithm', true),
                            set_config('norm.author_ref', 'venue-matcher@2.12.0', true),
                            set_config('norm.run_uuid', r::text, true);
                    UPDATE norm.venue_evidence SET author_ref = 'venue-matcher@2.12.0', run_uuid = r;
                END $$
                """
            )
        );
        var forget = await match.PlanForgetAsync(
            artists,
            [],
            rematch: true,
            CancellationToken.None
        );
        forget.Rows.Should().HaveCount(before.Count);
        await match.ForgetAsync(forget.Rows, reask: false, CancellationToken.None);
        await match.RunAsync(artists, CancellationToken.None);
        (await match.RetireAsync(forget.Venues, CancellationToken.None)).Should().Be(0);

        (await Strings(placements)).Should().Equal(before);
        (await Strings("SELECT id FROM norm.venues ORDER BY 1")).Should().Equal(venues);
        (await Strings("SELECT id FROM norm.venues WHERE merged_into IS NOT NULL"))
            .Should()
            .BeEmpty();
    }

    [Test]
    public async Task AReaskedRowIsLeftToTheModelByEveryMatch()
    {
        var database = new DbServiceNormalizationDatabase(db);
        var match = new VenueMatch(database);
        await match.RunAsync(artists, CancellationToken.None);
        var row = await Row("Red Rocks Amphitheater", "Morrison, Colorado");
        var redRocks = await PlacedOn(row);

        var forget = await match.PlanForgetAsync(
            artists,
            ["red rocks amphitheater@morrison, colorado", "Nowhere Hall"],
            rematch: false,
            CancellationToken.None
        );
        forget.Rows.Should().Equal(row);
        forget.Held.Should().Equal(row);
        forget.Venues.Should().Equal(redRocks!.Value);
        // Every artist's rows (no list) plan the same.
        (
            await match.PlanForgetAsync(
                null,
                ["red rocks amphitheater@morrison, colorado"],
                rematch: false,
                CancellationToken.None
            )
        )
            .Rows.Should()
            .Equal(row);
        (await match.ForgetAsync(forget.Rows, reask: true, CancellationToken.None)).Should().Be(1);

        // The hold is kept in norm, not by the run: a later match (one after an import, which
        // doesn't wait for the lock, included) leaves the row and its show to the model.
        await match.RunAsync(artists, CancellationToken.None);
        await match.RunAsync(artists, CancellationToken.None, wait: false);
        (await PlacedOn(row)).Should().BeNull();
        (
            await Strings(
                "SELECT show_uuid::text FROM norm.show_venues sv JOIN shows s ON s.uuid = sv.show_uuid WHERE s.date = '2004-06-25'"
            )
        )
            .Should()
            .BeEmpty();
        (await match.RetireAsync(forget.Venues, CancellationToken.None)).Should().Be(0);
    }

    [Test]
    public async Task ANamesakeInAnotherTownIsNoTwin()
    {
        var database = new DbServiceNormalizationDatabase(db);
        await db.WithWriteConnection(async c =>
        {
            var connection = (Npgsql.NpgsqlConnection)c;
            await using var tx = await connection.BeginTransactionAsync();
            var store = await NormStore.BeginAsync(connection, tx, AuthorTypes.Llm, "venue-llm@5");
            var brooklyn = VenueId.New();
            await store.WriteVenuesAsync([
                new VenueRecord(
                    brooklyn,
                    "Madison Square Garden",
                    City: "Brooklyn",
                    Region: "NY",
                    Country: "US",
                    Confidence: 0.9f
                ),
            ]);
            await store.WriteVenueNamesAsync([
                new VenueNameRecord(brooklyn, "Madison Square Garden", Confidence: 0.9f),
            ]);
            await tx.CommitAsync();
            return 0;
        });

        await new VenueMatch(database).RunAsync(artists, CancellationToken.None);
        await new VenueLlm(database, new ScriptedVenueModel()).RunChunkAsync(
            artists,
            [],
            CancellationToken.None
        );

        (
            await Strings(
                "SELECT city FROM norm.venues WHERE name = 'Madison Square Garden' ORDER BY 1"
            )
        )
            .Should()
            .Equal("Brooklyn", "New York");
    }

    [Test]
    public async Task OneNameTwiceInATownIsAQuestion()
    {
        var database = new DbServiceNormalizationDatabase(db);
        await db.WithWriteConnection(async c =>
        {
            var connection = (Npgsql.NpgsqlConnection)c;
            await using var tx = await connection.BeginTransactionAsync();
            var store = await NormStore.BeginAsync(connection, tx, AuthorTypes.Llm, "venue-llm@6");
            var (auditorium, opera, room) = (VenueId.New(), VenueId.New(), VenueId.New());
            VenueRecord At(
                VenueId id,
                string name,
                string kind = VenueKinds.Site,
                VenueId? parent = null
            ) => new(id, name, kind, parent, "St. Louis", "MO", "US", Confidence: 0.9f);
            await store.WriteVenuesAsync([
                At(auditorium, "Kiel Auditorium"),
                At(opera, "Kiel Opera House"),
                At(room, "Kiel Opera House", VenueKinds.Room, auditorium),
            ]);
            await tx.CommitAsync();
            return 0;
        });

        await new VenueMatch(database).RunAsync(artists, CancellationToken.None);

        (
            await Strings(
                "SELECT title FROM norm.review_items WHERE key LIKE 'same:%' AND resolved_at IS NULL"
            )
        )
            .Should()
            .Equal(
                "Kiel Opera House — St. Louis: site (0 rows) / room of Kiel Auditorium (0 rows)"
            );
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task ASpellingTheModelLeftOutIsAskedAgain(bool forgetful, bool sloppy)
    {
        var database = new DbServiceNormalizationDatabase(db);
        var model = new ScriptedVenueModel(forgetful: forgetful, sloppy: sloppy);
        await new VenueMatch(database).RunAsync(artists, CancellationToken.None);
        var chunk = await new VenueLlm(database, model).RunChunkAsync(
            artists,
            [],
            CancellationToken.None
        );

        model.Calls.Should().Be(2);
        chunk.Unsure.Should().Be(0);
        (
            await Strings(
                "SELECT lv.name FROM norm.venue_evidence e JOIN venues lv ON lv.uuid = e.legacy_venue_uuid JOIN norm.venues v ON v.id = e.venue_id WHERE lv.name = 'MSG' AND v.name = 'Madison Square Garden'"
            )
        )
            .Should()
            .ContainSingle();
    }

    [Test]
    public async Task NoReaskWhenTheBudgetHasNoRoom()
    {
        var database = new DbServiceNormalizationDatabase(db);
        var model = new ScriptedVenueModel(sloppy: true);
        await new VenueMatch(database).RunAsync(artists, CancellationToken.None);
        var chunk = await new VenueLlm(database, model).RunChunkAsync(
            artists,
            [],
            CancellationToken.None,
            mayRetry: false
        );

        model.Calls.Should().Be(1);
        chunk.ModelCalls.Should().Be(1);
        chunk.Unsure.Should().Be(1);
    }

    [Test]
    public async Task OnlyArtistsWhoseVenuesAreSwitchedOnAreKeptCurrent()
    {
        var database = new DbServiceNormalizationDatabase(db);
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "INSERT INTO features (artist_id, norm_venues) SELECT id, true FROM artists WHERE uuid = @on",
                new { on = artists[0] }
            )
        );
        var flags = new NormFlags(database);

        (await flags.VenuesOnAsync(artists[0])).Should().BeTrue();
        (await flags.VenuesOnAsync(artists[1])).Should().BeFalse();
        (await flags.VenueArtistsAsync()).Should().Contain(artists[0]).And.NotContain(artists[1]);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AVenueTheModelNeverSawLearnsItsNamesOnce(bool unsure)
    {
        var database = new DbServiceNormalizationDatabase(db);
        await new VenueMatch(database).RunAsync(artists, CancellationToken.None);
        var model = new ScriptedHistoryModel(unsure);
        var history = new VenueHistory(database, model);

        // A run for other artists doesn't ask about these artists' venues.
        (await history.RunChunkAsync(1, CancellationToken.None, [Guid.NewGuid()]))
            .Done.Should()
            .BeTrue();
        var first = await history.RunChunkAsync(1, CancellationToken.None, artists);
        (await history.RunChunkAsync(1, CancellationToken.None)).Done.Should().BeTrue();

        first.Asked.Should().Be(1); // Red Rocks: the one venue the match created
        (
            await Strings(
                "SELECT (outcome ? 'named')::text || ' ' || (provider_request_id IS NULL)::text FROM norm.model_calls WHERE job = 'venue_history' ORDER BY called_at DESC LIMIT 1"
            )
        )
            .Should()
            .Equal("true true");
        model.Calls.Should().Be(1);
        (await Strings("SELECT name FROM norm.venues WHERE city = 'Morrison'"))
            .Should()
            .Equal(unsure ? "Red Rocks Amphitheatre" : "Red Rocks Park & Amphitheatre");
        (
            await Strings(
                "SELECT question || ' ' || (resolved_at IS NULL)::text FROM norm.review_items WHERE key LIKE 'history:%'"
            )
        )
            .Should()
            .Equal(unsure ? "history true" : "history false");
    }

    [Test]
    public async Task NoLockIsHeldThroughAModelCall()
    {
        var database = new DbServiceNormalizationDatabase(db);
        var model = new LockProbeModel(LocalTestDatabase.ConnectionString);
        await new VenueMatch(database).RunAsync(artists, CancellationToken.None);
        await new VenueLlm(database, model).RunChunkAsync(artists, [], CancellationToken.None);

        model.LockFree.Should().NotBeEmpty().And.OnlyContain(free => free);
    }

    [Test]
    public async Task ABusyPostImportMatchDefers()
    {
        var match = new VenueMatch(new DbServiceNormalizationDatabase(db));
        await using var holder = new Npgsql.NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await holder.OpenAsync();
        await using (var tx = await holder.BeginTransactionAsync())
        {
            await holder.ExecuteAsync(
                "SELECT pg_advisory_xact_lock(hashtext('norm-venues'))",
                transaction: tx
            );
            (await match.RunAsync(artists, CancellationToken.None, wait: false))
                .Deferred.Should()
                .BeTrue();
        }

        var later = await match.RunAsync(artists, CancellationToken.None, wait: false);
        later.Deferred.Should().BeFalse();
        later.RowsMapped.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task ReciprocalMergesCannotMakeACycle()
    {
        var (a, b) = (VenueId.New(), VenueId.New());
        await db.WithWriteConnection(async c =>
        {
            var connection = (Npgsql.NpgsqlConnection)c;
            await using var tx = await connection.BeginTransactionAsync();
            var store = await NormStore.BeginAsync(connection, tx, AuthorTypes.Moderator, "m1");
            await store.WriteVenuesAsync([
                new VenueRecord(a, "Venue A", City: "Albany", Region: "NY", Country: "US"),
                new VenueRecord(b, "Venue B", City: "Albany", Region: "NY", Country: "US"),
            ]);
            await tx.CommitAsync();
            return 0;
        });

        await using var first = new Npgsql.NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await using var second = new Npgsql.NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await first.OpenAsync();
        await second.OpenAsync();
        await using var tx1 = await first.BeginTransactionAsync();
        await (await NormStore.BeginAsync(first, tx1, AuthorTypes.Moderator, "m1")).MergeVenueAsync(
            a,
            b
        );

        // B into A waits for A into B, then finds A merged: refused, no cycle.
        var reverse = Task.Run(async () =>
        {
            await using var tx2 = await second.BeginTransactionAsync();
            await (
                await NormStore.BeginAsync(second, tx2, AuthorTypes.Moderator, "m2")
            ).MergeVenueAsync(b, a);
            await tx2.CommitAsync();
        });
        await Task.Delay(300);
        reverse.IsCompleted.Should().BeFalse();
        await tx1.CommitAsync();

        await FluentActions
            .Awaiting(() => reverse)
            .Should()
            .ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task ARevertNeverUndoesAnEditCommittedDuringIt()
    {
        var song = SongId.New();
        var run = await db.WithWriteConnection(async c =>
        {
            var connection = (Npgsql.NpgsqlConnection)c;
            await using var tx = await connection.BeginTransactionAsync();
            var store = await NormStore.BeginAsync(
                connection,
                tx,
                AuthorTypes.Llm,
                "catalogue-llm@5"
            );
            await store.WriteSongsAsync([
                new SongRecord(song, artists[0], "Sugaree", Confidence: 0.95f),
            ]);
            await tx.CommitAsync();
            return store.Run;
        });

        await using var editor = new Npgsql.NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await using var reverter = new Npgsql.NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await editor.OpenAsync();
        await reverter.OpenAsync();
        await using var edit = await editor.BeginTransactionAsync();
        await NormStore.BeginAsync(editor, edit, AuthorTypes.Moderator, "m1");
        await editor.ExecuteAsync(
            "UPDATE norm.songs SET name = 'Sugaree (corrected)' WHERE id = @song",
            new { song },
            edit
        );

        var revert = Task.Run(async () =>
        {
            await using var tx = await reverter.BeginTransactionAsync();
            var result = await (
                await NormStore.BeginAsync(reverter, tx, AuthorTypes.Moderator, "m2")
            ).RevertRunAsync(run);
            await tx.CommitAsync();
            return result;
        });
        await Task.Delay(300);
        await edit.CommitAsync();

        (await revert).Should().Be((0, 1));
        (await Strings($"SELECT name FROM norm.songs WHERE id = '{song}'"))
            .Should()
            .Equal("Sugaree (corrected)");
    }
}
