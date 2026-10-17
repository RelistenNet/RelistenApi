using System.Text;
using System.Text.Json;
using System.Transactions;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

/// <summary>Catalog association uses existing identities and current ownership, independently of normalized acceptance.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationCatalogBinding
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
    public async Task LegacyBindingReturnsActualUuidsAndCurrentPerformanceWithoutChangingPlayback()
    {
        var source = await fixture.Source();
        var (artistUuid, setUuid) = await Scope(source);
        var venueUuid = Guid.NewGuid();
        var showUuid = Guid.NewGuid();
        await fixture.Db.WithWriteConnection(async c =>
        {
            var venue = await c.QuerySingleAsync<long>(
                "INSERT INTO venues(artist_id,name,location,uuid,updated_at) VALUES(@artist,'Venue','City',@uuid,now()) RETURNING id",
                new { artist = source.ArtistId, uuid = venueUuid }
            );
            var show = await c.QuerySingleAsync<long>(
                "INSERT INTO shows(artist_id,date,display_date,uuid,updated_at) VALUES(@artist,'2000-01-01','2000-01-01 Late',@uuid,now()) RETURNING id",
                new { artist = source.ArtistId, uuid = showUuid }
            );
            await c.ExecuteAsync(
                "UPDATE sources SET show_id=@show,venue_id=@venue WHERE id=@source",
                new
                {
                    show,
                    venue,
                    source = source.SourceId,
                }
            );
        });
        var observation = await Legacy(source);

        var result = await bindings.BindRecordingAsync(observation, artistUuid);

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        result
            .Recording.Value.Should()
            .BeEquivalentTo(
                new NormalizationCatalogRecordingReference(
                    artistUuid,
                    source.SourceUuid,
                    showUuid,
                    venueUuid,
                    []
                )
            );
        var file = result.Files.Single().Binding.Value!;
        file.TrackUuid.Should().Be(source.TrackUuid);
        file.SourceSetUuid.Should().Be(setUuid);
        file.MediaUrl.Should().Be("https://example.test/file.mp3");
        result.CatalogInputs.Should().ContainSingle(input => input.ArtistUuid == artistUuid);
        result.CatalogInputs[0].Generation.Should().BeGreaterThanOrEqualTo(0);
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<Guid>(
                    "SELECT uuid FROM source_tracks WHERE uuid=@uuid AND source_id=@source",
                    new { uuid = source.TrackUuid, source = source.SourceId }
                )
            )
        )
            .Should()
            .Be(source.TrackUuid);
    }

    [Test]
    public async Task MissingShowAndVenueDoNotWithholdRecordingCredits()
    {
        var source = await fixture.Source();
        var result = await bindings.BindRecordingAsync(await Legacy(source));

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        result.Recording.Value!.ShowUuid.Should().BeNull();
        result.Recording.Value.SourceVenueUuid.Should().BeNull();
        result
            .Recording.Value.ContextIssues.Should()
            .BeEquivalentTo(["missing_show", "missing_source_venue"]);
        result.Files.Single().Binding.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
    }

    [Test]
    public async Task MissingLegacyRecordingDoesNotSubstituteAnotherOnTheSameDate()
    {
        var source = await fixture.Source();
        var observation = await Legacy(source);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync("DELETE FROM sources WHERE id=@id", new { id = source.SourceId })
        );

        var result = await bindings.BindRecordingAsync(observation);

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Missing);
        result.Recording.Value.Should().BeNull();
        result.CatalogInputs.Should().BeEmpty();
        result
            .Files.Should()
            .OnlyContain(file =>
                file.Binding.Status == NormalizationCatalogBindingStatus.Missing
                && file.Binding.Value == null
            );
    }

    [Test]
    public async Task OrphaningAndChangedMediaUrlsRemainExplicitFileOutcomes()
    {
        var source = await fixture.Source();
        var observation = await Legacy(source);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE source_tracks SET mp3_url='https://example.test/replaced.mp3' WHERE uuid=@uuid",
                new { uuid = source.TrackUuid }
            )
        );
        var changed = await bindings.BindRecordingAsync(observation);
        changed.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        changed
            .Files.Single()
            .Binding.Status.Should()
            .Be(NormalizationCatalogBindingStatus.ProviderConflict);
        changed.Files.Single().Binding.Reason.Should().Be("media_url_changed");

        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE source_tracks SET is_orphaned=true WHERE uuid=@uuid",
                new { uuid = source.TrackUuid }
            )
        );
        var orphaned = await bindings.BindRecordingAsync(observation);
        orphaned
            .Files.Single()
            .Binding.Status.Should()
            .Be(NormalizationCatalogBindingStatus.Missing);
        orphaned.Files.Single().Binding.Reason.Should().Be("orphaned_file");
    }

    [Test]
    public async Task MovingTheStoredFileToAnotherRecordingCannotRebindTheOldOwner()
    {
        var source = await fixture.Source();
        var other = await fixture.Source();
        var observation = await Legacy(source);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE source_tracks SET source_id=@other WHERE uuid=@uuid",
                new { other = other.SourceId, uuid = source.TrackUuid }
            )
        );

        var result = await bindings.BindRecordingAsync(observation);

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        result
            .Files.Single()
            .Binding.Status.Should()
            .Be(NormalizationCatalogBindingStatus.ProviderConflict);
        result.Files.Single().Binding.Reason.Should().Be("file_ownership_conflict");
    }

    [TestCase("source_id")]
    [TestCase("artist_id")]
    public async Task MissingFileOwnershipCannotSupplyAPlayableBinding(string column)
    {
        var source = await fixture.Source();
        var observation = await Legacy(source);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                $"UPDATE source_tracks SET {column}=NULL WHERE uuid=@uuid",
                new { uuid = source.TrackUuid }
            )
        );

        var result = await bindings.BindRecordingAsync(observation);

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        result
            .Files.Single()
            .Binding.Status.Should()
            .Be(NormalizationCatalogBindingStatus.ProviderConflict);
        result.Files.Single().Binding.Value.Should().BeNull();
    }

    [Test]
    public async Task ArchiveUsesQualifiedItemIdentityAndExactMediaEvenWhenTitlesAndOrderChange()
    {
        var source = await fixture.Source();
        var item = "archive-" + Guid.NewGuid().ToString("N");
        const string filename = "folder/file name.mp3";
        await ArchiveSource(source, item, filename);
        var observation = Archive(item, filename);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE source_tracks SET title='Entirely different title',track_position=99 WHERE uuid=@uuid",
                new { uuid = source.TrackUuid }
            )
        );

        var result = await bindings.BindRecordingAsync(observation);

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        result.Recording.Value!.SourceUuid.Should().Be(source.SourceUuid);
        result
            .SourceIdentities.Should()
            .ContainSingle(identity => identity.UpstreamIdentifier == item);
        result.Files.Single().Binding.Value!.TrackUuid.Should().Be(source.TrackUuid);
        result
            .Files.Single()
            .Binding.Value!.MediaUrl.Should()
            .Be($"https://archive.org/download/{item}/{filename}");
    }

    [Test]
    public async Task GlobalArchiveAmbiguityIsDetectedBeforeTheExpectedArtistFilter()
    {
        var first = await fixture.Source();
        var second = await fixture.Source();
        var item = "archive-" + Guid.NewGuid().ToString("N");
        await ArchiveSource(first, item, "one.mp3");
        await ArchiveSource(second, item, "one.mp3");
        var (artistUuid, _) = await Scope(first);

        var result = await bindings.BindRecordingAsync(Archive(item, "one.mp3"), artistUuid);

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Ambiguous);
        result.Recording.Value.Should().BeNull();
        result.CatalogInputs.Should().BeEmpty();
    }

    [Test]
    public async Task AnUnqualifiedSameIdentifierDoesNotEstablishArchiveOwnership()
    {
        var source = await fixture.Source();
        var item = "archive-" + Guid.NewGuid().ToString("N");
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET upstream_identifier=@item WHERE id=@id",
                new { item, id = source.SourceId }
            )
        );

        var result = await bindings.BindRecordingAsync(Archive(item, "one.mp3"));

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Missing);
        result.Recording.Value.Should().BeNull();
    }

    [Test]
    public async Task ExpectedArtistMismatchIsAConflictAfterUniqueProviderResolution()
    {
        var source = await fixture.Source();
        var item = "archive-" + Guid.NewGuid().ToString("N");
        await ArchiveSource(source, item, "one.mp3");

        var result = await bindings.BindRecordingAsync(Archive(item, "one.mp3"), Guid.NewGuid());

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.ProviderConflict);
        result.Recording.Reason.Should().Be("artist_mismatch");
    }

    [Test]
    public async Task RetainedArchiveAssignmentCannotSilentlyRetargetToANewSource()
    {
        var source = await fixture.Source();
        var replacement = await fixture.Source();
        var item = "archive-" + Guid.NewGuid().ToString("N");
        await ArchiveSource(source, item, "one.mp3");
        var (artistUuid, _) = await Scope(source);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "INSERT INTO normalization.archive_item_resolutions(item_identifier,selected_artist_uuid,canonical_source_uuid,status) VALUES(@item,@artistUuid,@source,'resolved')",
                new
                {
                    item,
                    artistUuid,
                    source = source.SourceUuid,
                }
            )
        );
        var before = await bindings.BindRecordingAsync(Archive(item, "one.mp3"));
        before.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        before.Recording.Reason.Should().Be("retained_archive_assignment");

        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync("DELETE FROM sources WHERE id=@id", new { id = source.SourceId })
        );
        await ArchiveSource(replacement, item, "one.mp3");
        var after = await bindings.BindRecordingAsync(Archive(item, "one.mp3"));
        after.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.ProviderConflict);
        after.Recording.Reason.Should().Be("retained_recording_changed");
        after.Recording.Value.Should().BeNull();
    }

    [Test]
    public async Task IntakePublishedReferenceCanBindWithoutClaimingAnotherRecording()
    {
        var source = await fixture.Source();
        var item = "archive-" + Guid.NewGuid().ToString("N");
        await ArchiveSource(source, item, "one.mp3");
        var (artistUuid, _) = await Scope(source);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                INSERT INTO normalization.archive_item_observations(observation_id,item_identifier,projection_version,content_hash,provider_url,payload)
                VALUES(@observation,@item,1,@item,'https://archive.org','{}'::jsonb);
                INSERT INTO normalization.archive_item_intake(item_identifier,published_observation_id,published_artist_uuid,published_artist_id,published_source_uuid,published_display_date,published_at)
                VALUES(@item,@observation,@artistUuid,@artistId,@source,'2026-10-17',now())
                """,
                new
                {
                    item,
                    observation = Guid.NewGuid(),
                    artistUuid,
                    artistId = source.ArtistId,
                    source = source.SourceUuid,
                }
            )
        );

        var result = await bindings.BindRecordingAsync(Archive(item, "one.mp3"));

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        result.Recording.Value!.SourceUuid.Should().Be(source.SourceUuid);
        result.Recording.Reason.Should().Be("retained_archive_assignment");
    }

    [Test]
    public async Task ConflictingRetainedDecisionCannotPublishFreshItemClaims()
    {
        var source = await fixture.Source();
        var item = "archive-" + Guid.NewGuid().ToString("N");
        await ArchiveSource(source, item, "one.mp3");
        var (artistUuid, _) = await Scope(source);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "INSERT INTO normalization.archive_item_resolutions(item_identifier,selected_artist_uuid,canonical_source_uuid,status) VALUES(@item,@artistUuid,@source,'conflict')",
                new
                {
                    item,
                    artistUuid,
                    source = source.SourceUuid,
                }
            )
        );

        var result = await bindings.BindRecordingAsync(Archive(item, "one.mp3"));

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.ProviderConflict);
        result.Recording.Reason.Should().Be("retained_assignment_conflict");
        result.Files.Single().Binding.Value.Should().BeNull();
    }

    [Test]
    public async Task MissingRetainedSourceRemainsMissingWithoutAReplacementGuess()
    {
        var source = await fixture.Source();
        var item = "archive-" + Guid.NewGuid().ToString("N");
        await ArchiveSource(source, item, "one.mp3");
        var (artistUuid, _) = await Scope(source);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "INSERT INTO normalization.archive_item_resolutions(item_identifier,selected_artist_uuid,canonical_source_uuid,status) VALUES(@item,@artistUuid,@source,'resolved'); DELETE FROM sources WHERE id=@id",
                new
                {
                    item,
                    artistUuid,
                    source = source.SourceUuid,
                    id = source.SourceId,
                }
            )
        );

        var result = await bindings.BindRecordingAsync(Archive(item, "one.mp3"));

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Missing);
        result.Recording.Reason.Should().Be("missing_retained_recording");
    }

    [Test]
    public async Task DuplicateProviderFilenamesCannotChooseOneOfTheAccounts()
    {
        var source = await fixture.Source();
        var item = "archive-" + Guid.NewGuid().ToString("N");
        await ArchiveSource(source, item, "one.mp3");

        var result = await bindings.BindRecordingAsync(Archive(item, "one.mp3", "one.mp3"));

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        result
            .Files.Should()
            .HaveCount(2)
            .And.OnlyContain(file =>
                file.Binding.Status == NormalizationCatalogBindingStatus.Ambiguous
                && file.Binding.Value == null
            );
    }

    [Test]
    public async Task InconsistentSourceSetOwnershipCannotBecomeAnActiveFileBinding()
    {
        var source = await fixture.Source();
        var other = await fixture.Source();
        var observation = await Legacy(source);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE source_tracks SET source_set_id=(SELECT source_set_id FROM source_tracks WHERE uuid=@other) WHERE uuid=@uuid",
                new { other = other.TrackUuid, uuid = source.TrackUuid }
            )
        );

        var result = await bindings.BindRecordingAsync(observation);

        result
            .Files.Single()
            .Binding.Status.Should()
            .Be(NormalizationCatalogBindingStatus.ProviderConflict);
        result.Files.Single().Binding.Reason.Should().Be("file_ownership_conflict");
    }

    [Test]
    public async Task MissingArtistRevisionFailsInsteadOfReturningAnUnfencedBoundSnapshot()
    {
        var source = await fixture.Source();
        var observation = await Legacy(source);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "DELETE FROM normalization.catalog_artist_revisions WHERE artist_uuid=(SELECT uuid FROM artists WHERE id=@id)",
                new { id = source.ArtistId }
            )
        );

        var bind = () => bindings.BindRecordingAsync(observation);

        await bind.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task CancellationAndAmbientTransactionsCannotProducePartialBindings()
    {
        var source = await fixture.Source();
        var observation = await Legacy(source);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var bind = () =>
            bindings.BindRecordingAsync(observation, cancellationToken: canceled.Token);
        await bind.Should().ThrowAsync<OperationCanceledException>();

        using var transaction = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        var ambient = () => bindings.BindRecordingAsync(observation);
        await ambient.Should().ThrowAsync<InvalidOperationException>();
    }

    private async Task<ProviderRecordingObservation> Legacy(
        NormalizationDomainFixture.CatalogSource source
    )
    {
        var seed = await new NormalizationLegacyInputService(
            fixture.Db,
            new NormalizationEvidenceService(fixture.Db)
        ).SeedRecordingAsync(source.SourceUuid);
        return new NormalizationProviderDecoder().Decode(seed.Input!.Revision).Recordings.Single();
    }

    private async Task<(Guid ArtistUuid, Guid SetUuid)> Scope(
        NormalizationDomainFixture.CatalogSource source
    ) =>
        await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<(Guid, Guid)>(
                "SELECT a.uuid,ss.uuid FROM artists a JOIN sources s ON s.artist_id=a.id JOIN source_sets ss ON ss.source_id=s.id WHERE s.id=@id",
                new { id = source.SourceId }
            )
        );

    private async Task ArchiveSource(
        NormalizationDomainFixture.CatalogSource source,
        string item,
        string filename
    )
    {
        await fixture.Db.WithWriteConnection(async c =>
        {
            await c.ExecuteAsync(
                "INSERT INTO upstream_sources(id,name,url) VALUES(1,'archive.org','https://archive.org') ON CONFLICT(id) DO NOTHING"
            );
            var provider = await c.QuerySingleAsync<int>(
                "SELECT id FROM upstream_sources WHERE name='archive.org' ORDER BY id LIMIT 1"
            );
            await c.ExecuteAsync(
                "UPDATE sources SET upstream_identifier=@item WHERE id=@id; UPDATE source_tracks SET mp3_url=@url WHERE uuid=@track; INSERT INTO links(source_id,upstream_source_id,for_source,for_reviews,for_ratings,url,label) VALUES(@id,@provider,true,false,false,@page,'Archive')",
                new
                {
                    item,
                    id = source.SourceId,
                    url = $"https://archive.org/download/{item}/{filename}",
                    track = source.TrackUuid,
                    provider,
                    page = $"https://archive.org/details/{item}",
                }
            );
        });
    }

    private static ProviderRecordingObservation Archive(string item, params string[] filenames)
    {
        var input = new NormalizationInputRevision
        {
            Uuid = Guid.NewGuid(),
            Resource = new("archive.org", "item_metadata", item),
            CaptureVersion = "archive-item-response-v1",
            MediaType = "application/json",
            Payload = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(
                    new
                    {
                        metadata = new { identifier = item },
                        files = filenames.Select(name => new { name, format = "VBR MP3" }),
                    }
                )
            ),
        };
        return new NormalizationProviderDecoder().Decode(input).Recordings.Single();
    }
}
