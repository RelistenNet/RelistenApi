using System.Diagnostics;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

/// <summary>Refreshed facts keep their original catalog read set through binding and typed publication.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationLegacyBinding
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationLegacyInputService legacy = null!;
    private NormalizationCatalogBindingService bindings = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        legacy = new(fixture.Db, new NormalizationEvidenceService(fixture.Db));
        bindings = new(fixture.Db);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task RefreshDecodesChangedCreditsAndTitlesAndKeepsExistingPlaybackIdentity()
    {
        var source = await fixture.Source();
        await UpdateTaper(source, "Pat Original");
        var original = await Refresh(source);
        await fixture.Db.WithWriteConnection(async c =>
        {
            await c.ExecuteAsync(
                "UPDATE sources SET taper='Pat Current',transferrer='Robin Transfer' WHERE id=@id",
                new { id = source.SourceId }
            );
            await c.ExecuteAsync(
                "UPDATE source_tracks SET title='Changed song title' WHERE uuid=@uuid",
                new { uuid = source.TrackUuid }
            );
        });

        var current = await Refresh(source);
        var bound = await bindings.BindRefreshedRecordingAsync(
            current.Snapshot,
            current.Observation
        );

        Credits(original).Should().Contain(c => c.Name == "Pat Original");
        Credits(current)
            .Should()
            .Contain(c => c.Name == "Pat Current" && c.Role == RecordingCreditRole.Taper);
        Credits(current)
            .Should()
            .Contain(c => c.Name == "Robin Transfer" && c.Role == RecordingCreditRole.Transferrer);
        current.Observation.Files.Single().Title!.Value.Should().Be("Changed song title");
        current
            .Snapshot.Capture.Revision.Uuid.Should()
            .NotBe(original.Snapshot.Capture.Revision.Uuid);
        bound.Recording.Value!.SourceUuid.Should().Be(source.SourceUuid);
        bound.Files.Single().Binding.Value!.TrackUuid.Should().Be(source.TrackUuid);
        bound.CatalogInputs.Should().BeEquivalentTo(current.Snapshot.CatalogInputs);
    }

    [Test]
    public async Task StaleFactsRequireAWholeRefreshRatherThanAnAssociationRebind()
    {
        var source = await fixture.Source();
        await UpdateTaper(source, "Pat Original");
        var original = await Refresh(source);
        await UpdateTaper(source, "Pat Current");

        var bind = () =>
            bindings.BindRefreshedRecordingAsync(original.Snapshot, original.Observation);
        await bind.Should().ThrowAsync<NormalizationConflictException>();
        // Historical association inspection remains useful, but cannot make these old facts current.
        (await bindings.BindRecordingAsync(original.Observation))
            .Recording.Status.Should()
            .Be(NormalizationCatalogBindingStatus.Bound);
        await bind.Should().ThrowAsync<NormalizationConflictException>();

        var fresh = await Refresh(source);
        Credits(fresh).Should().ContainSingle(c => c.Name == "Pat Current");
        (await bindings.BindRefreshedRecordingAsync(fresh.Snapshot, fresh.Observation))
            .CatalogInputs.Should()
            .BeEquivalentTo(fresh.Snapshot.CatalogInputs);
    }

    [Test]
    public async Task ForeignContextChangesCannotBeHiddenByRefreshingOnlyTheRecordingOwnerClock()
    {
        var source = await fixture.Source();
        var foreign = await fixture.Source();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE source_tracks SET artist_id=@foreign,mp3_url=@url WHERE uuid=@uuid",
                new
                {
                    foreign = foreign.ArtistId,
                    uuid = source.TrackUuid,
                    url = "https://example.test/" + source.TrackUuid + ".mp3",
                }
            )
        );
        var original = await Refresh(source);
        original.Snapshot.CatalogInputs.Should().HaveCount(2);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE artists SET name='Changed foreign artist' WHERE id=@id",
                new { id = foreign.ArtistId }
            )
        );

        var bind = () =>
            bindings.BindRefreshedRecordingAsync(original.Snapshot, original.Observation);
        await bind.Should().ThrowAsync<NormalizationConflictException>();

        var fresh = await Refresh(source);
        var bound = await bindings.BindRefreshedRecordingAsync(fresh.Snapshot, fresh.Observation);
        bound.CatalogInputs.Should().BeEquivalentTo(fresh.Snapshot.CatalogInputs);
        bound
            .Files.Single()
            .Binding.Status.Should()
            .Be(NormalizationCatalogBindingStatus.ProviderConflict);
        bound.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
    }

    [TestCase("unadmitted")]
    [TestCase("unknown_fetch")]
    [TestCase("wrong_revision")]
    [TestCase("empty_clocks")]
    [TestCase("changed_payload")]
    public async Task CheckedBindingRejectsAnUnverifiableRefresh(string fault)
    {
        var context = await Refresh(await fixture.Source());
        var capture = context.Snapshot.Capture;
        var fetch = capture.Fetch;
        if (fault is "unadmitted" or "unknown_fetch" or "wrong_revision")
        {
            capture = capture with
            {
                Fetch = new NormalizationInputFetch
                {
                    FetchUuid = fault == "unknown_fetch" ? Guid.NewGuid() : fetch.FetchUuid,
                    InputRevisionUuid =
                        fault == "wrong_revision" ? Guid.NewGuid() : fetch.InputRevisionUuid,
                    AcceptedAsCurrent = fault != "unadmitted",
                    ExpectedGeneration = fetch.ExpectedGeneration,
                    ResourceGeneration = fetch.ResourceGeneration,
                },
            };
        }
        if (fault == "changed_payload")
        {
            capture.Revision.Payload[0] = (byte)' ';
        }
        var snapshot = context.Snapshot with
        {
            Capture = capture,
            CatalogInputs = fault == "empty_clocks" ? [] : context.Snapshot.CatalogInputs,
        };

        var bind = () => bindings.BindRefreshedRecordingAsync(snapshot, context.Observation);
        await bind.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task ADecodedObservationFromAnotherRevisionCannotBorrowAValidReceipt()
    {
        var source = await fixture.Source();
        var original = await Refresh(source);
        await UpdateTaper(source, "Pat Current");
        var fresh = await Refresh(source);

        var bind = () => bindings.BindRefreshedRecordingAsync(fresh.Snapshot, original.Observation);
        await bind.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task SourceCreditsPublishWithAValidFileAndAnUnresolvedSibling()
    {
        var source = await fixture.Source();
        await UpdateTaper(source, "Pat Recorder");
        var incompleteUuid = Guid.NewGuid();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "INSERT INTO source_tracks(source_id,source_set_id,artist_id,track_position,title,slug,mp3_url,uuid,updated_at) VALUES(@source,NULL,NULL,2,'Incomplete',@slug,'https://example.test/incomplete.mp3',@uuid,now())",
                new
                {
                    source = source.SourceId,
                    slug = incompleteUuid.ToString(),
                    uuid = incompleteUuid,
                }
            )
        );
        var context = await Refresh(source);
        using (var document = JsonDocument.Parse(context.Snapshot.Capture.Revision.Payload))
        {
            document.RootElement.GetProperty("show").ValueKind.Should().Be(JsonValueKind.Null);
            var incomplete = document
                .RootElement.GetProperty("tracks")
                .EnumerateArray()
                .Single(row =>
                    row.GetProperty("track").GetProperty("uuid").GetGuid() == incompleteUuid
                );
            incomplete.GetProperty("set_context").ValueKind.Should().Be(JsonValueKind.Null);
            incomplete.GetProperty("artist_context").ValueKind.Should().Be(JsonValueKind.Null);
        }
        var bound = await bindings.BindRefreshedRecordingAsync(
            context.Snapshot,
            context.Observation
        );
        bound.Recording.Value!.ContextIssues.Should().Contain("missing_show");
        bound
            .Files.Should()
            .ContainSingle(f =>
                f.Binding.Value != null && f.Binding.Value.TrackUuid == source.TrackUuid
            );
        bound
            .Files.Should()
            .ContainSingle(f => f.Binding.Status != NormalizationCatalogBindingStatus.Bound);

        var ticket = await Begin(source, bound);
        (await PublishCredit(context, source, ticket))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        var reader = new NormalizationReadService(
            fixture.Db,
            Options.Create(new NormalizationReadOptions { Enabled = true })
        );
        var page = await reader.ListRecordingCreditsAsync(source.SourceUuid);
        var credit = page!.items.Should().ContainSingle().Subject;
        credit.role.Should().Be("taper");
        credit.original_text.Should().Be("Pat Recorder");
        credit.reference_available.Should().BeTrue();
        credit.evidence_status.Should().Be("current");
    }

    [Test]
    public async Task CatalogChangeAfterCheckedBindingRejectsWorkAdmission()
    {
        var source = await fixture.Source();
        var context = await Refresh(source);
        var bound = await bindings.BindRefreshedRecordingAsync(
            context.Snapshot,
            context.Observation
        );
        await UpdateTaper(source, "Pat Changed");

        var begin = () => Begin(source, bound);
        await begin.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task ReplacedInputHeadRejectsOldBindingAndWholeRefreshCanRecover()
    {
        var source = await fixture.Source();
        await UpdateTaper(source, "Pat Recorder");
        var context = await Refresh(source);
        var bound = await bindings.BindRefreshedRecordingAsync(
            context.Snapshot,
            context.Observation
        );
        var evidence = new NormalizationEvidenceService(fixture.Db);
        // Evidence retention can advance independently of catalog rows. Admission must consume
        // the revision that was actually decoded, rather than silently reading the latest head.
        var replacement = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                bound.Resource,
                "{}"u8.ToArray(),
                "application/json",
                "competing-input-test-v1",
                context.Snapshot.Capture.Fetch.ResourceGeneration,
                DateTimeOffset.UtcNow
            )
        );
        replacement.Fetch.AcceptedAsCurrent.Should().BeTrue();

        var begin = () => Begin(source, bound);
        await begin.Should().ThrowAsync<NormalizationConflictException>();

        var fresh = await Refresh(source);
        var freshBinding = await bindings.BindRefreshedRecordingAsync(
            fresh.Snapshot,
            fresh.Observation
        );
        freshBinding.CatalogInputs.Should().BeEquivalentTo(bound.CatalogInputs);
        var ticket = await Begin(source, freshBinding);
        (await PublishCredit(fresh, source, ticket))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
    }

    [Test]
    public async Task CatalogChangeAfterAdmissionPreventsTypedCreditPublication()
    {
        var source = await fixture.Source();
        await UpdateTaper(source, "Pat Original");
        var context = await Refresh(source);
        var bound = await bindings.BindRefreshedRecordingAsync(
            context.Snapshot,
            context.Observation
        );
        var ticket = await Begin(source, bound);
        await UpdateTaper(source, "Pat Changed");

        (await PublishCredit(context, source, ticket))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
        var reader = new NormalizationReadService(
            fixture.Db,
            Options.Create(new NormalizationReadOptions { Enabled = true })
        );
        (await reader.ListRecordingCreditsAsync(source.SourceUuid))!.items.Should().BeEmpty();
    }

    [Test]
    public async Task CatalogEditBetweenSnapshotAndCaptureCannotBeAcceptedByCheckedBinding()
    {
        var source = await fixture.Source();
        await UpdateTaper(source, "Pat Original");
        var initial = await Refresh(source);
        await using var blocker = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "SELECT 1 FROM normalization.input_resources WHERE provider='legacy_catalog' AND resource_type='recording' AND provider_key=@key FOR UPDATE",
            new { key = source.SourceUuid.ToString("D") },
            transaction
        );
        var refresh = legacy.RefreshRecordingAsync(source.SourceUuid);
        try
        {
            await RequireBlockedBy(blocker.ProcessID);
            await UpdateTaper(source, "Pat Changed");
        }
        finally
        {
            await transaction.CommitAsync();
            await refresh;
        }
        var retained = (await refresh)!;
        retained.Capture.Fetch.AcceptedAsCurrent.Should().BeTrue();
        retained.Capture.Revision.Uuid.Should().Be(initial.Snapshot.Capture.Revision.Uuid);
        var observation = new NormalizationProviderDecoder()
            .Decode(retained.Capture.Revision)
            .Recordings.Single();
        var bind = () => bindings.BindRefreshedRecordingAsync(retained, observation);
        await bind.Should().ThrowAsync<NormalizationConflictException>();
        var fresh = await Refresh(source);
        Credits(fresh).Should().ContainSingle(c => c.Name == "Pat Changed");
        (await bindings.BindRefreshedRecordingAsync(fresh.Snapshot, fresh.Observation))
            .Recording.Status.Should()
            .Be(NormalizationCatalogBindingStatus.Bound);
    }

    private async Task<Context> Refresh(CatalogSource source)
    {
        var snapshot = (await legacy.RefreshRecordingAsync(source.SourceUuid))!;
        return new(
            snapshot,
            new NormalizationProviderDecoder().Decode(snapshot.Capture.Revision).Recordings.Single()
        );
    }

    private static IReadOnlyList<RetainedCreditMention> Credits(Context context) =>
        new RetainedRecordingCreditExtractor()
            .Extract(context.Snapshot.Capture.Revision, context.Observation.Text)
            .Credits;

    private Task UpdateTaper(CatalogSource source, string taper) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET taper=@taper WHERE id=@id",
                new { id = source.SourceId, taper }
            )
        );

    private Task<NormalizationWorkTicket> Begin(
        CatalogSource source,
        NormalizationRecordingBindingSnapshot bound
    ) =>
        fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("people", "recording", source.SourceUuid),
            "checked-legacy-test-v1",
            bound.Inputs.Select(input => input.Resource).ToArray(),
            [],
            catalogInputs: bound.CatalogInputs,
            sourceIdentities: bound.SourceIdentities,
            expectedInputRevisions: bound.Inputs
        );

    private Task<NormalizationPublicationResult> PublishCredit(
        Context context,
        CatalogSource source,
        NormalizationWorkTicket ticket
    )
    {
        var taper = Credits(context).Single(c => c.Role == RecordingCreditRole.Taper);
        return new NormalizedPeopleService(fixture.Db, fixture.Work).PublishAsync(
            ticket.WorkUuid,
            new(
                [],
                [],
                [],
                [
                    new RecordingCredit(
                        Guid.NewGuid(),
                        source.SourceUuid,
                        "taper",
                        taper.Name,
                        null,
                        null,
                        [taper.Evidence]
                    ),
                ]
            )
        );
    }

    private async Task RequireBlockedBy(int pid)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (
                await fixture.Db.WithWriteConnection(c =>
                    c.ExecuteScalarAsync<bool>(
                        "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE @pid=ANY(pg_blocking_pids(pid)))",
                        new { pid }
                    )
                )
            )
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail("The refresh did not reach its input capture lock.");
    }

    private sealed record Context(
        NormalizationLegacyRecordingSnapshot Snapshot,
        ProviderRecordingObservation Observation
    );
}
