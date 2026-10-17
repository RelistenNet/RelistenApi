using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>Selections and failure receipts remain replayable as the live catalog changes.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationRecordingPages
{
    private const string Version = "recording-page-fixture-v1";
    private static long nextGlobalSource;
    private NormalizationDomainFixture fixture = null!;
    private NormalizationRecordingPageStore pages = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        pages = new(fixture.Db);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task ArtistSelectionIsBoundedOrderedAndCarriesAnExclusiveContinuation()
    {
        var artist = await Artist(4);
        var first = await pages.GetOrCreateAsync(Guid.NewGuid(), artist.Uuid, null, 2, Version);
        first.Items.Select(item => item.SourceUuid).Should().Equal(artist.Sources.Take(2));
        first.Items.Select(item => item.Position).Should().Equal(0, 1);
        first.HasMoreAtSelection.Should().BeTrue();
        first.SelectedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(30));
        var second = await pages.GetOrCreateAsync(
            Guid.NewGuid(),
            artist.Uuid,
            first.Items[^1].SourceUuid,
            2,
            Version
        );
        second.Items.Select(item => item.SourceUuid).Should().Equal(artist.Sources.Skip(2));
        second.HasMoreAtSelection.Should().BeFalse();
        first.Items.Select(item => item.AttemptUuid).Should().OnlyHaveUniqueItems();
        foreach (var item in first.Items)
        {
            (
                await new NormalizationRecordingProposalService(fixture.Db).GetAttemptAsync(
                    item.AttemptUuid
                )
            )
                .Should()
                .BeNull();
        }
    }

    [Test]
    public async Task GlobalSelectionUsesTheSameUuidKeysetWithoutAnArtistFilter()
    {
        var artist = await Artist(0);
        var cursor = GlobalUuid(Interlocked.Read(ref nextGlobalSource));
        var firstUuid = GlobalUuid(Interlocked.Increment(ref nextGlobalSource));
        var secondUuid = GlobalUuid(Interlocked.Increment(ref nextGlobalSource));
        await InsertSource(artist.Id, firstUuid);
        await InsertSource(artist.Id, secondUuid);

        var first = await pages.GetOrCreateAsync(Guid.NewGuid(), null, cursor, 1, Version);
        first.Items.Single().SourceUuid.Should().Be(firstUuid);
        first.HasMoreAtSelection.Should().BeTrue();
        var second = await pages.GetOrCreateAsync(Guid.NewGuid(), null, firstUuid, 1, Version);
        second.Items.Single().SourceUuid.Should().Be(secondUuid);
        second.HasMoreAtSelection.Should().BeFalse();
    }

    [Test]
    public async Task ConcurrentPageCreationReturnsOneSelectionAndStablePlannedAttempts()
    {
        var artist = await Artist(3);
        var pageUuid = Guid.NewGuid();
        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 6)
                .Select(_ => pages.GetOrCreateAsync(pageUuid, artist.Uuid, null, 2, Version))
        );
        foreach (var result in results)
        {
            result.Should().BeEquivalentTo(results[0]);
        }
        (await pages.GetAsync(pageUuid)).Should().BeEquivalentTo(results[0]);
        var otherPage = await pages.GetOrCreateAsync(Guid.NewGuid(), artist.Uuid, null, 2, Version);
        otherPage
            .Items.Select(item => item.SourceUuid)
            .Should()
            .Equal(results[0].Items.Select(item => item.SourceUuid));
        otherPage
            .Items.Select(item => item.AttemptUuid)
            .Should()
            .NotIntersectWith(results[0].Items.Select(item => item.AttemptUuid));
    }

    [TestCase("artist")]
    [TestCase("cursor")]
    [TestCase("limit")]
    [TestCase("version")]
    public async Task ReusingPageUuidWithChangedRequestConflictsWithoutChangingItsPlan(string field)
    {
        var artist = await Artist(3);
        var original = await pages.GetOrCreateAsync(Guid.NewGuid(), artist.Uuid, null, 2, Version);
        Func<Task> changed = () =>
            pages.GetOrCreateAsync(
                original.PageUuid,
                field == "artist" ? null : artist.Uuid,
                field == "cursor" ? artist.Sources[0] : null,
                field == "limit" ? 1 : 2,
                field == "version" ? "changed-version" : Version
            );

        await changed.Should().ThrowAsync<NormalizationConflictException>();

        (await pages.GetAsync(original.PageUuid)).Should().BeEquivalentTo(original);
    }

    [Test]
    public async Task LateArrivalsOwnershipMovesAndDeletionCannotRewriteAnExistingPage()
    {
        var artist = await Artist(3);
        var other = await Artist(0);
        var original = await pages.GetOrCreateAsync(Guid.NewGuid(), artist.Uuid, null, 2, Version);
        await InsertSource(artist.Id, Guid.NewGuid());
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "DELETE FROM sources WHERE uuid=@deleted; UPDATE sources SET artist_id=@other WHERE uuid=@moved; DELETE FROM artists WHERE id=@artist",
                new
                {
                    deleted = original.Items[0].SourceUuid,
                    moved = original.Items[1].SourceUuid,
                    other = other.Id,
                    artist = artist.Id,
                }
            )
        );

        var replay = await pages.GetOrCreateAsync(
            original.PageUuid,
            original.ArtistUuid,
            original.AfterSourceUuid,
            original.Limit,
            original.ProcessingVersion
        );

        replay.Should().BeEquivalentTo(original);
        (await pages.GetAsync(original.PageUuid)).Should().BeEquivalentTo(original);
        Func<Task> newSelection = () =>
            pages.GetOrCreateAsync(Guid.NewGuid(), artist.Uuid, null, 2, Version);
        await newSelection
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("Catalog artist was not found.*");
    }

    [Test]
    public async Task KnownEmptyArtistAndUnknownArtistHaveDifferentOutcomes()
    {
        var artist = await Artist(0);
        var empty = await pages.GetOrCreateAsync(Guid.NewGuid(), artist.Uuid, null, 10, Version);
        empty.Items.Should().BeEmpty();
        empty.HasMoreAtSelection.Should().BeFalse();
        (await pages.GetAsync(empty.PageUuid)).Should().BeEquivalentTo(empty);
        var unknownUuid = Guid.NewGuid();
        Func<Task> unknown = () =>
            pages.GetOrCreateAsync(unknownUuid, Guid.NewGuid(), null, 10, Version);
        await unknown.Should().ThrowAsync<ArgumentException>();
        (await pages.GetAsync(unknownUuid)).Should().BeNull();
        (await pages.GetAsync(Guid.NewGuid())).Should().BeNull();
    }

    [TestCase(0)]
    [TestCase(101)]
    public async Task PageBoundsFailBeforePersistingASelection(int limit)
    {
        var pageUuid = Guid.NewGuid();
        Func<Task> create = () => pages.GetOrCreateAsync(pageUuid, null, null, limit, Version);
        await create.Should().ThrowAsync<ArgumentOutOfRangeException>();
        (await pages.GetAsync(pageUuid)).Should().BeNull();
    }

    [TestCase("page")]
    [TestCase("artist")]
    [TestCase("cursor")]
    public async Task EmptyUuidDoesNotMeanAnOmittedSelectionField(string field)
    {
        Func<Task> create = () =>
            pages.GetOrCreateAsync(
                field == "page" ? Guid.Empty : Guid.NewGuid(),
                field == "artist" ? Guid.Empty : null,
                field == "cursor" ? Guid.Empty : null,
                1,
                Version
            );
        await create.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task FailureReceiptReplaysExactlyAndChangedDetailsConflict()
    {
        var page = await Page(1);
        var invocation = Guid.NewGuid();
        var attempt = page.Items[0].AttemptUuid;
        var first = await pages.RecordFailureAsync(
            invocation,
            page.PageUuid,
            attempt,
            NormalizationRecordingPageFailureCode.DatabaseFailed,
            true
        );
        var replay = await pages.RecordFailureAsync(
            invocation,
            page.PageUuid,
            attempt,
            NormalizationRecordingPageFailureCode.DatabaseFailed,
            true
        );
        replay.Should().BeEquivalentTo(first);
        Func<Task> changedCode = () =>
            pages.RecordFailureAsync(
                invocation,
                page.PageUuid,
                attempt,
                NormalizationRecordingPageFailureCode.ProcessingFailed,
                true
            );
        Func<Task> changedRetry = () =>
            pages.RecordFailureAsync(
                invocation,
                page.PageUuid,
                attempt,
                NormalizationRecordingPageFailureCode.DatabaseFailed,
                false
            );
        Func<Task> changedPage = () =>
            pages.RecordFailureAsync(
                invocation,
                Guid.NewGuid(),
                attempt,
                NormalizationRecordingPageFailureCode.DatabaseFailed,
                true
            );
        await changedCode.Should().ThrowAsync<NormalizationConflictException>();
        await changedRetry.Should().ThrowAsync<NormalizationConflictException>();
        await changedPage.Should().ThrowAsync<NormalizationConflictException>();
        (await pages.GetFailuresAsync(page.PageUuid))
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(first);
        (await new NormalizationRecordingProposalService(fixture.Db).GetAttemptAsync(attempt))
            .Should()
            .BeNull();
    }

    [Test]
    public async Task ConcurrentFailureReplayRetainsOneReceiptAndFailurePagingIsExclusive()
    {
        var page = await Page(2);
        var invocation = Guid.NewGuid();
        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 6)
                .Select(_ =>
                    pages.RecordFailureAsync(
                        invocation,
                        page.PageUuid,
                        page.Items[0].AttemptUuid,
                        NormalizationRecordingPageFailureCode.ProcessingTimeout,
                        true
                    )
                )
        );
        foreach (var result in results)
        {
            result.Should().BeEquivalentTo(results[0]);
        }
        var second = await pages.RecordFailureAsync(
            invocation,
            page.PageUuid,
            page.Items[1].AttemptUuid,
            NormalizationRecordingPageFailureCode.PreparationConflict,
            true
        );
        var laterInvocation = await pages.RecordFailureAsync(
            Guid.NewGuid(),
            page.PageUuid,
            page.Items[0].AttemptUuid,
            NormalizationRecordingPageFailureCode.ProcessingFailed,
            false
        );

        var firstPage = await pages.GetFailuresAsync(page.PageUuid, limit: 1);
        firstPage.Should().ContainSingle().Which.Should().BeEquivalentTo(results[0]);
        var next = await pages.GetFailuresAsync(page.PageUuid, firstPage[0].Sequence, 1);
        next.Should().ContainSingle().Which.Should().BeEquivalentTo(second);
        var final = await pages.GetFailuresAsync(page.PageUuid, second.Sequence);
        final.Should().ContainSingle().Which.Should().BeEquivalentTo(laterInvocation);
        (await pages.GetFailuresAsync(Guid.NewGuid())).Should().BeEmpty();
    }

    [Test]
    public async Task FailuresRequireAnActualPlannedItemAndAKnownCode()
    {
        var page = await Page(1);
        Func<Task> foreignAttempt = () =>
            pages.RecordFailureAsync(
                Guid.NewGuid(),
                page.PageUuid,
                Guid.NewGuid(),
                NormalizationRecordingPageFailureCode.ProcessingFailed,
                false
            );
        Func<Task> badCode = () =>
            pages.RecordFailureAsync(
                Guid.NewGuid(),
                page.PageUuid,
                page.Items[0].AttemptUuid,
                (NormalizationRecordingPageFailureCode)1000,
                false
            );
        await foreignAttempt.Should().ThrowAsync<ArgumentException>();
        await badCode.Should().ThrowAsync<ArgumentException>();
        (await pages.GetFailuresAsync(page.PageUuid)).Should().BeEmpty();
    }

    [Test]
    public async Task PagePlansItemsAndFailureReceiptsAreImmutable()
    {
        var page = await Page(1);
        var failure = await pages.RecordFailureAsync(
            Guid.NewGuid(),
            page.PageUuid,
            page.Items[0].AttemptUuid,
            NormalizationRecordingPageFailureCode.ProcessingFailed,
            false
        );
        var statements = new[]
        {
            "UPDATE normalization.recording_processing_pages SET processing_version='changed' WHERE page_uuid=@PageUuid",
            "UPDATE normalization.recording_processing_page_items SET source_uuid=@replacement WHERE page_uuid=@PageUuid",
            "UPDATE normalization.recording_processing_page_failures SET retryable=true WHERE page_uuid=@PageUuid",
        };
        foreach (var sql in statements)
        {
            Func<Task> mutate = () =>
                fixture.Db.WithWriteConnection(c =>
                    c.ExecuteAsync(sql, new { page.PageUuid, replacement = Guid.NewGuid() })
                );
            await mutate.Should().ThrowAsync<Exception>();
        }
        (await pages.GetAsync(page.PageUuid)).Should().BeEquivalentTo(page);
        (await pages.GetFailuresAsync(page.PageUuid))
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(failure);
    }

    [TestCase(0)]
    [TestCase(101)]
    public async Task FailurePagesEnforceTheirBound(int limit)
    {
        Func<Task> read = () => pages.GetFailuresAsync(Guid.NewGuid(), limit: limit);
        await read.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    private async Task<NormalizationRecordingPage> Page(int sources)
    {
        var artist = await Artist(sources);
        return await pages.GetOrCreateAsync(Guid.NewGuid(), artist.Uuid, null, sources, Version);
    }

    private async Task<ArtistGroup> Artist(int count)
    {
        var artist = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<ArtistIdentity>(
                "INSERT INTO artists(name,slug,uuid,updated_at) VALUES('Page test',@slug,@uuid,now()) RETURNING id AS \"Id\",uuid AS \"Uuid\"",
                new { slug = Guid.NewGuid().ToString("D"), uuid = Guid.NewGuid() }
            )
        );
        var sourceUuids = Enumerable
            .Range(0, count)
            .Select(_ => Guid.NewGuid())
            .OrderBy(uuid => uuid.ToString("D"), StringComparer.Ordinal)
            .ToArray();
        foreach (var source in sourceUuids)
        {
            await InsertSource(artist.Id, source);
        }
        return new(artist.Id, artist.Uuid, sourceUuids);
    }

    private Task InsertSource(int artistId, Guid uuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                INSERT INTO sources(artist_id,upstream_identifier,display_date,uuid,updated_at,is_soundboard,is_remaster,has_jamcharts)
                VALUES(@artistId,@key,'2000-01-01',@uuid,now(),false,false,false)
                """,
                new
                {
                    artistId,
                    key = uuid.ToString("D"),
                    uuid,
                }
            )
        );

    private static Guid GlobalUuid(long value) =>
        Guid.Parse("ffffffff-ffff-ffff-ffff-" + value.ToString("x12"));

    private sealed record ArtistIdentity(int Id, Guid Uuid);

    private sealed record ArtistGroup(int Id, Guid Uuid, Guid[] Sources);
}
