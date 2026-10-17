using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

/// <summary>Missing and conflicting catalog associations retain the exact lookups that made them unresolved.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationRecordingBindingFences
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationCatalogBindingService bindings = null!;
    private NormalizationEvidenceService evidence = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        bindings = new(fixture.Db);
        evidence = new(fixture.Db);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [TestCase(false)]
    [TestCase(true)]
    public async Task MissingLegacyUuidArrivalInvalidatesItsOriginalAbsence(bool beforeAdmission)
    {
        var uuid = Guid.NewGuid();
        var observation = await Observe(new("legacy_catalog", "recording", uuid.ToString("D")));
        var snapshot = await bindings.BindRecordingAsync(observation);
        snapshot.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Missing);
        snapshot.CatalogInputs.Should().BeEmpty();
        snapshot.SourceUuids.Should().Equal(new NormalizationSourceUuidDependency(uuid, 0));
        var ticket = beforeAdmission ? null : await Admit(snapshot);

        var arrived = await fixture.Source();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET uuid=@uuid WHERE id=@SourceId",
                new { uuid, arrived.SourceId }
            )
        );

        await AssertChanged(snapshot, ticket);
        var rebound = await bindings.BindRecordingAsync(observation);
        rebound.Recording.Value!.SourceUuid.Should().Be(uuid);
        rebound.SourceUuids.Should().ContainSingle().Which.Generation.Should().BeGreaterThan(0);
        if (ticket != null)
        {
            // Exact admission/terminal replay retains the original zero rather than re-reading it.
            var replayed = await Admit(snapshot, ticket.WorkUuid);
            replayed.Should().BeEquivalentTo(ticket);
            (await fixture.Work.TryPublishAsync(ticket.WorkUuid, NeverWrite))
                .Replayed.Should()
                .BeTrue();
        }
    }

    [TestCase("archive.org", false)]
    [TestCase("archive.org", true)]
    [TestCase("phish.in", false)]
    [TestCase("phish.in", true)]
    public async Task MissingProviderIdentifierArrivalInvalidatesTheAbsentGlobalLookup(
        string provider,
        bool beforeAdmission
    )
    {
        var key =
            provider == "phish.in"
                ? Random.Shared.Next(100000000, int.MaxValue).ToString()
                : "missing-" + Guid.NewGuid().ToString("N");
        var observation = await Observe(
            new(provider, provider == "phish.in" ? "show" : "item_metadata", key)
        );
        var snapshot = await bindings.BindRecordingAsync(observation, Guid.NewGuid());
        snapshot.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Missing);
        snapshot
            .CatalogInputs.Should()
            .BeEmpty("an expected artist argument is not a catalog observation");
        snapshot.SourceIdentities.Should().Equal(new NormalizationSourceIdentityDependency(key, 0));
        var ticket = beforeAdmission ? null : await Admit(snapshot);

        await Qualify(await fixture.Source(), provider, key);

        await AssertChanged(snapshot, ticket);
        (await bindings.BindRecordingAsync(observation))
            .Recording.Status.Should()
            .Be(NormalizationCatalogBindingStatus.Bound);
    }

    [Test]
    public async Task AResurrectedRetainedArchiveUuidWithAnotherIdentifierCannotEscapeTheFence()
    {
        var original = await fixture.Source();
        var item = "retained-" + Guid.NewGuid().ToString("N");
        await Resolution(item, original.SourceUuid, await Artist(original));
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync("DELETE FROM sources WHERE id=@SourceId", original)
        );
        var observation = await Observe(new("archive.org", "item_metadata", item));
        var snapshot = await bindings.BindRecordingAsync(observation);
        snapshot.Recording.Reason.Should().Be("missing_retained_recording");
        snapshot
            .SourceUuids.Should()
            .ContainSingle()
            .Which.SourceUuid.Should()
            .Be(original.SourceUuid);
        var ticket = await Admit(snapshot);
        var replacement = await fixture.Source();

        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET uuid=@uuid WHERE id=@SourceId",
                new { uuid = original.SourceUuid, replacement.SourceId }
            )
        );

        var changed = await bindings.BindRecordingAsync(observation);
        changed.Recording.Reason.Should().Be("retained_provider_identity_changed");
        changed
            .SourceIdentities.Should()
            .Equal(
                snapshot.SourceIdentities,
                "the arrived source used another provider identifier"
            );
        changed
            .SourceUuids!.Single()
            .Generation.Should()
            .BeGreaterThan(snapshot.SourceUuids!.Single().Generation);
        await AssertChanged(snapshot, ticket);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ConflictingRetentionPreservesEveryValidUuidLookup(bool multipleReferences)
    {
        var source = await fixture.Source();
        var item = "conflicting-" + Guid.NewGuid().ToString("N");
        var otherUuid = Guid.NewGuid();
        await Resolution(
            item,
            source.SourceUuid,
            await Artist(source),
            multipleReferences ? "resolved" : "conflict"
        );
        if (multipleReferences)
        {
            await PublishedIntake(item, otherUuid, await Artist(source));
        }
        var snapshot = await bindings.BindRecordingAsync(
            await Observe(new("archive.org", "item_metadata", item))
        );

        snapshot.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.ProviderConflict);
        snapshot.Recording.Reason.Should().Be("retained_assignment_conflict");
        snapshot
            .SourceIdentities.Should()
            .ContainSingle(identity => identity.UpstreamIdentifier == item);
        snapshot
            .SourceUuids!.Select(input => input.SourceUuid)
            .Should()
            .BeEquivalentTo(
                multipleReferences
                    ? new[] { source.SourceUuid, otherUuid }
                    : new[] { source.SourceUuid }
            );
        snapshot.CatalogInputs.Should().BeEmpty();
        await Admit(snapshot);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task EmptyRetainedUuidsAreExplicitConflictsWithoutAnEmptyClock(bool emptyArtist)
    {
        var source = await fixture.Source();
        var item = "malformed-" + Guid.NewGuid().ToString("N");
        await Resolution(
            item,
            emptyArtist ? source.SourceUuid : Guid.Empty,
            emptyArtist ? Guid.Empty : await Artist(source)
        );
        var observation = await Observe(new("archive.org", "item_metadata", item));
        var snapshot = await bindings.BindRecordingAsync(observation);

        snapshot.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.ProviderConflict);
        snapshot.Recording.Reason.Should().Be("invalid_retained_assignment");
        (snapshot.SourceUuids ?? []).Should().NotContain(input => input.SourceUuid == Guid.Empty);
        snapshot
            .SourceIdentities.Should()
            .ContainSingle(identity => identity.UpstreamIdentifier == item);
        var ticket = await Admit(snapshot);
        var artistUuid = await Artist(source);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE normalization.archive_item_resolutions SET canonical_source_uuid=@sourceUuid,selected_artist_uuid=@artistUuid WHERE item_identifier=@item",
                new
                {
                    sourceUuid = source.SourceUuid,
                    artistUuid,
                    item,
                }
            )
        );
        await AssertChanged(snapshot, ticket);
    }

    [Test]
    public async Task AmbiguousProviderLookupRetainsItsFenceBeforeTheExpectedArtistFilter()
    {
        var first = await fixture.Source();
        var second = await fixture.Source();
        var item = "ambiguous-" + Guid.NewGuid().ToString("N");
        await Qualify(first, "archive.org", item);
        await Qualify(second, "archive.org", item);
        var observation = await Observe(new("archive.org", "item_metadata", item));
        var snapshot = await bindings.BindRecordingAsync(observation, await Artist(first));
        snapshot.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Ambiguous);
        snapshot.CatalogInputs.Should().BeEmpty();
        snapshot
            .SourceIdentities.Should()
            .ContainSingle(identity => identity.UpstreamIdentifier == item);
        var ticket = await Admit(snapshot);

        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync("UPDATE links SET for_source=false WHERE source_id=@SourceId", second)
        );

        await AssertChanged(snapshot, ticket);
        (await bindings.BindRecordingAsync(observation, await Artist(first)))
            .Recording.Value!.SourceUuid.Should()
            .Be(first.SourceUuid);
    }

    [TestCase("archive.org")]
    [TestCase("phish.in")]
    public async Task ExpectedArtistConflictsKeepTheProviderLookupFence(string provider)
    {
        var source = await fixture.Source();
        var other = await fixture.Source();
        var key =
            provider == "phish.in"
                ? Random.Shared.Next(100000000, int.MaxValue).ToString()
                : "artist-conflict-" + Guid.NewGuid().ToString("N");
        await Qualify(source, provider, key);
        var observation = await Observe(
            new(provider, provider == "phish.in" ? "show" : "item_metadata", key)
        );
        var snapshot = await bindings.BindRecordingAsync(observation, await Artist(other));
        snapshot.Recording.Reason.Should().Be("artist_mismatch");
        snapshot
            .SourceIdentities.Should()
            .ContainSingle(identity => identity.UpstreamIdentifier == key);
        var ticket = await Admit(snapshot);

        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET artist_id=@ArtistId WHERE id=@source",
                new { other.ArtistId, source = source.SourceId }
            )
        );

        await AssertChanged(snapshot, ticket);
        (await bindings.BindRecordingAsync(observation, await Artist(other)))
            .Recording.Status.Should()
            .Be(NormalizationCatalogBindingStatus.Bound);
    }

    [Test]
    public async Task LegacyArtistMismatchInvalidatesWhenTheStoredArtistUuidChanges()
    {
        var source = await fixture.Source();
        var expectedArtist = Guid.NewGuid();
        var observation = await Observe(
            new("legacy_catalog", "recording", source.SourceUuid.ToString("D"))
        );
        var snapshot = await bindings.BindRecordingAsync(observation, expectedArtist);
        snapshot.Recording.Reason.Should().Be("artist_mismatch");
        snapshot.CatalogInputs.Should().BeEmpty();
        snapshot
            .SourceUuids.Should()
            .ContainSingle()
            .Which.SourceUuid.Should()
            .Be(source.SourceUuid);
        var ticket = await Admit(snapshot);

        // The source's numeric artist association stays unchanged. The checked UUID lookup
        // must still become stale when that artist acquires the expected public identity.
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE artists SET uuid=@expectedArtist WHERE id=@ArtistId",
                new { expectedArtist, source.ArtistId }
            )
        );

        await AssertChanged(snapshot, ticket);
        var rebound = await bindings.BindRecordingAsync(observation, expectedArtist);
        rebound.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        rebound.Recording.Value!.ArtistUuid.Should().Be(expectedArtist);
    }

    private async Task<ProviderRecordingObservation> Observe(NormalizationResourceKey resource)
    {
        var capture = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                JsonSerializer.SerializeToUtf8Bytes(new { identifier = resource.ProviderKey }),
                "application/json",
                "binding-fence-fixture-v1",
                0,
                DateTimeOffset.UtcNow
            )
        );
        return new ProviderRecordingObservation
        {
            Origin = new(
                resource,
                new(capture.Revision.Uuid, ""),
                ProviderObservationScope.ResourceItem
            ),
        };
    }

    private Task<NormalizationWorkTicket> Admit(
        NormalizationRecordingBindingSnapshot snapshot,
        Guid? workUuid = null
    ) =>
        fixture.Work.BeginWorkAsync(
            workUuid ?? Guid.NewGuid(),
            new("extractions", "recording", snapshot.InputRevisionUuid),
            "recording-binding-fences",
            [snapshot.Resource],
            [],
            catalogInputs: snapshot.CatalogInputs,
            sourceIdentities: snapshot.SourceIdentities,
            expectedInputRevisions: snapshot.Inputs,
            sourceUuids: snapshot.SourceUuids
        );

    private async Task AssertChanged(
        NormalizationRecordingBindingSnapshot snapshot,
        NormalizationWorkTicket? ticket
    )
    {
        if (ticket == null)
        {
            var admit = () => Admit(snapshot);
            await admit.Should().ThrowAsync<NormalizationConflictException>();
        }
        else
        {
            (await fixture.Work.TryPublishAsync(ticket.WorkUuid, NeverWrite))
                .Status.Should()
                .Be(NormalizationPublicationStatus.Stale);
        }
    }

    private Task<Guid> Artist(CatalogSource source) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>("SELECT uuid FROM artists WHERE id=@ArtistId", source)
        );

    private async Task Qualify(CatalogSource source, string provider, string key)
    {
        await fixture.Db.WithWriteConnection(async c =>
        {
            var upstream = await c.QuerySingleOrDefaultAsync<int?>(
                "SELECT id FROM upstream_sources WHERE name=@provider ORDER BY id LIMIT 1",
                new { provider }
            );
            upstream ??= await c.QuerySingleAsync<int>(
                "INSERT INTO upstream_sources(id,name,url) SELECT COALESCE(max(id),0)+1,@provider,@url FROM upstream_sources RETURNING id",
                new { provider, url = "https://" + provider }
            );
            await c.ExecuteAsync(
                "UPDATE sources SET upstream_identifier=@key WHERE id=@SourceId; INSERT INTO links(source_id,upstream_source_id,for_source,for_reviews,for_ratings,url,label) VALUES(@SourceId,@upstream,true,false,false,@url,'Provider')",
                new
                {
                    source.SourceId,
                    key,
                    upstream,
                    url = "https://" + provider + "/" + key,
                }
            );
        });
    }

    private Task Resolution(
        string item,
        Guid sourceUuid,
        Guid artistUuid,
        string status = "resolved"
    ) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "INSERT INTO normalization.archive_item_resolutions(item_identifier,canonical_source_uuid,selected_artist_uuid,status) VALUES(@item,@sourceUuid,@artistUuid,@status)",
                new
                {
                    item,
                    sourceUuid,
                    artistUuid,
                    status,
                }
            )
        );

    private Task PublishedIntake(string item, Guid sourceUuid, Guid artistUuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                INSERT INTO normalization.archive_item_observations(observation_id,item_identifier,projection_version,content_hash,provider_url,payload)
                VALUES(@observation,@item,1,@item,'https://archive.org','{}');
                INSERT INTO normalization.archive_item_intake(item_identifier,published_observation_id,published_artist_uuid,published_artist_id,published_display_date,published_source_uuid,published_at)
                VALUES(@item,@observation,@artistUuid,(SELECT id FROM artists WHERE uuid=@artistUuid),'2026-10-17',@sourceUuid,now())
                """,
                new
                {
                    item,
                    sourceUuid,
                    artistUuid,
                    observation = Guid.NewGuid(),
                }
            )
        );

    private static Task NeverWrite(
        Npgsql.NpgsqlConnection _,
        Npgsql.NpgsqlTransaction __,
        CancellationToken ___
    ) => throw new InvalidOperationException("Stale binding must not invoke publication.");
}
