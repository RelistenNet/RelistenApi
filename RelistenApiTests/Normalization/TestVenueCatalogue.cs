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
internal sealed class ScriptedVenueModel(bool twin = false, bool stages = false) : ICatalogueModel
{
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
                spellings.Add(
                    new JsonObject
                    {
                        ["id"] = (string)s["id"]!,
                        ["spelling_text"] = text,
                        ["venue"] =
                            stages && text.Contains("Theater") ? "n5"
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
                    UPDATE norm.venues SET parent_uuid = NULL, merged_into = NULL
                    WHERE parent_uuid IS NOT NULL OR merged_into IS NOT NULL;
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
        var chunk = await llm.RunChunkAsync(artists, [], CancellationToken.None);
        chunk.VenuesCreated.Should().Be(2);
        (await llm.RunChunkAsync(artists, chunk.Asked, CancellationToken.None))
            .Done.Should()
            .BeTrue();
        var last = await match.RunAsync(artists, CancellationToken.None);

        (
            await Strings(
                """
                SELECT v.name || ' (' || v.kind || coalesce(', in ' || p.name, '') || ') ' || v.city
                FROM norm.venues v LEFT JOIN norm.venues p ON p.uuid = v.parent_uuid
                WHERE v.uuid IN (SELECT e.venue_uuid FROM norm.venue_evidence e JOIN venues lv ON lv.uuid = e.legacy_venue_uuid
                                 JOIN artists a ON a.id = lv.artist_id WHERE a.uuid = ANY(@artists))
                   OR v.uuid IN (SELECT parent_uuid FROM norm.venues)
                ORDER BY 1
                """
            )
        ).Should().Equal("Hulu Theater (room, in Madison Square Garden) New York", "Madison Square Garden (site) New York", "Red Rocks Amphitheatre (site) Morrison");

        // Every show has its venue in norm, and the Theater's shows read the name of their date.
        (
            await Strings(
                """
                SELECT sh.display_date || ' ' || norm.venue_name_on(sv.venue_uuid, sh.date) || ' (' || sv.basis || ')'
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
                SELECT n.name || ' (' || n.kind || ')' FROM norm.venues v JOIN norm.venue_names n ON n.venue_uuid = v.uuid
                WHERE v.name = 'Madison Square Garden' ORDER BY 1
                """
            )
        ).Should().Equal("MSG (short)", "Madison Square Garden (spelling)", "The Garden (short)");
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
                        Guid.CreateVersion7(),
                        "Cat's Cradle",
                        City: "Carrboro",
                        Region: "NC",
                        Country: "US",
                        Confidence: 0.9f
                    ),
                    new VenueRecord(
                        Guid.CreateVersion7(),
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

        // "Two places": answered, and not asked again.
        var key = (
            await Strings(
                "SELECT key FROM norm.review_items WHERE key LIKE 'same:%' AND resolved_at IS NULL"
            )
        ).Single();
        await As(
            AuthorTypes.User,
            "expert",
            store => store.CloseVenueReviewItemsAsync([key], "two places")
        );
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
                "SELECT r.name || ' in ' || p.name FROM norm.venues r JOIN norm.venues p ON p.uuid = r.parent_uuid ORDER BY 1"
            )
        )
            .Should()
            .Equal("Main Stage in Beacon Theatre", "Main Stage in Madison Square Garden");
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
            var brooklyn = Guid.CreateVersion7();
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
}
