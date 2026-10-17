using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>Missing legacy recordings need a real absence observation without fabricated input evidence.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationSourceUuidLookup
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationCatalogBindingService bindings = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        bindings = new(fixture.Db);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task AbsentProbeCreatesNoClockAndCannotAuthorizeWorkAfterArrival()
    {
        var uuid = Guid.NewGuid();
        var absent = await bindings.ProbeRecordingUuidAsync(uuid);

        absent.Exists.Should().BeFalse();
        absent.Dependency.Should().Be(new NormalizationSourceUuidDependency(uuid, 0));
        (
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM normalization.catalog_source_uuid_revisions WHERE source_uuid=@uuid)",
                    new { uuid }
                )
            )
        )
            .Should()
            .BeFalse();

        var existing = await fixture.Source();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                INSERT INTO sources(artist_id,upstream_identifier,display_date,uuid,updated_at,
                    is_soundboard,is_remaster,has_jamcharts)
                VALUES(@artist,@identifier,'2026-10-17',@uuid,now(),false,false,false)
                """,
                new
                {
                    artist = existing.ArtistId,
                    identifier = "arrived-" + uuid,
                    uuid,
                }
            )
        );

        var present = await bindings.ProbeRecordingUuidAsync(uuid);
        present.Exists.Should().BeTrue();
        present.Dependency.SourceUuid.Should().Be(uuid);
        present.Dependency.Generation.Should().BeGreaterThan(absent.Dependency.Generation);
        var subject = new NormalizationSubjectKey("people", "lookup_fixture", Guid.NewGuid());
        var stale = () =>
            fixture.Work.BeginWorkAsync(
                Guid.NewGuid(),
                subject,
                "probe-v1",
                [],
                [],
                sourceUuids: [absent.Dependency]
            );
        await stale.Should().ThrowAsync<NormalizationConflictException>();
        (await fixture.Work.GetSubjectAsync(subject)).Should().BeNull();
    }

    [Test]
    public async Task DeletedSourceProbeRetainsHistoryAndStalesAnAdmittedPresence()
    {
        var source = await fixture.Source();
        var present = await bindings.ProbeRecordingUuidAsync(source.SourceUuid);
        present.Exists.Should().BeTrue();
        var ticket = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("people", "lookup_fixture", Guid.NewGuid()),
            "probe-v1",
            [],
            [],
            sourceUuids: [present.Dependency]
        );

        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync("DELETE FROM sources WHERE id=@id", new { id = source.SourceId })
        );

        var absent = await bindings.ProbeRecordingUuidAsync(source.SourceUuid);
        absent.Exists.Should().BeFalse();
        absent.Dependency.SourceUuid.Should().Be(source.SourceUuid);
        absent.Dependency.Generation.Should().BeGreaterThan(present.Dependency.Generation);
        var result = await fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            (_, _, _) => throw new AssertionException("Deleted source presence cannot publish.")
        );
        result.Status.Should().Be(NormalizationPublicationStatus.Stale);
    }
}
