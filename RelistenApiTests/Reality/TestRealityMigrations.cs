using Dapper;
using FluentAssertions;

namespace RelistenApiTests.Reality;

[Category("LocalIngestion")]
public class TestRealityMigrations
{
    [SetUp]
    public void RequireDatabase() => RealityTestDatabase.RequireLocalDatabase();

    [Test]
    public async Task MigrationsCreateTheRatifiedSchemas()
    {
        await using var db = await RealityTestDatabase.OpenMigratedAsync();

        var tables = (
            await db.QueryAsync<string>(
                """
                SELECT table_schema || '.' || table_name FROM information_schema.tables
                WHERE table_schema IN ('media', 'kb', 'reality') ORDER BY 1
                """
            )
        ).ToList();

        tables
            .Should()
            .Contain([
                "media.algo_params",
                "media.files",
                "media.features",
                "media.anchors",
                "media.import_snapshots",
                "media.import_snapshot_tracks",
                "media.current_tracks",
                "kb.entities",
                "kb.predicates",
                "kb.claims",
                "kb.bot_runs",
                "kb.current_source_events",
                "kb.current_event_shows",
                "reality.versions",
                "reality.builds",
                "reality.version_rows",
                "reality.events_current",
                "reality.segments",
                "reality.pieces",
                "reality.gaps",
                "reality.unmapped",
                "reality.version_maps",
                "reality.performance_lineage",
                "reality.anchor_landings",
            ]);
        // WS-5 owns the rest of kb.
        tables
            .Should()
            .NotContain(["kb.claim_reviews", "kb.redirects", "kb.current_series_events"]);

        var predicates = await db.QueryAsync<string>(
            "SELECT subject_type || '.' || name FROM kb.predicates ORDER BY 1"
        );
        predicates.Should().Equal("event.legacy_show", "source.event");
    }

    [Test]
    public async Task ClaimsAreAppendOnly()
    {
        await using var db = await RealityTestDatabase.OpenMigratedAsync();
        await using var tx = await db.BeginTransactionAsync();
        var claim = Guid.CreateVersion7();
        await db.ExecuteAsync(
            """
            INSERT INTO kb.claims (uuid, subject_type, subject_uuid, predicate, value_schema_version, value, author_type, author_ref)
            VALUES (@claim, 'event', @subject, 'legacy_show', 1, '{"show": "00000000-0000-0000-0000-000000000001"}', 'importer', 'test')
            """,
            new { claim, subject = Guid.CreateVersion7() },
            tx
        );

        var update = () =>
            db.ExecuteAsync(
                "UPDATE kb.claims SET author_ref = 'x' WHERE uuid = @claim",
                new { claim },
                tx
            );
        await update.Should().ThrowAsync<Npgsql.PostgresException>().WithMessage("*append-only*");
    }
}
