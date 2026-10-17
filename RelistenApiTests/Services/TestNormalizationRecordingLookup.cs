using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

/// <summary>Lookup outcomes and durable delivery remain separate from successful extraction and playback.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationRecordingLookup
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationLegacyInputService legacy = null!;
    private NormalizationCatalogBindingService bindings = null!;
    private NormalizationRecordingProposalService proposals = null!;
    private NormalizationRecordingProcessor processor = null!;
    private NormalizationRecordingLookupStore store = null!;
    private NormalizationRecordingLookupService lookups = null!;
    private NormalizationRecordingLookupDispatcher dispatcher = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        legacy = new(fixture.Db, evidence);
        bindings = new(fixture.Db);
        proposals = new(fixture.Db);
        processor = new(
            legacy,
            evidence,
            bindings,
            new(),
            new(new(), new()),
            fixture.Work,
            proposals,
            new(fixture.Db)
        );
        store = new(fixture.Db);
        lookups = new(store, legacy, evidence, bindings, new(), fixture.Work);
        dispatcher = new(store, processor);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task MissingLegacyLookupSettlesWithoutEvidenceAndArrivalSchedulesANewAttempt()
    {
        var uuid = Guid.NewGuid();
        var attemptUuid = Guid.NewGuid();
        var missing = await lookups.LookupLegacyAsync(attemptUuid, uuid);

        missing.Result!.Status.Should().Be(NormalizationCatalogBindingStatus.Missing);
        missing
            .Preparation!.AbsenceProbe!.Dependency.Should()
            .Be(new NormalizationSourceUuidDependency(uuid, 0));
        (await evidence.GetCurrentInputAsync(LegacyResource(uuid))).Should().BeNull();
        var owner = (await fixture.Work.GetSubjectAsync(Owner(missing)))!;
        (await Pending(owner.SubjectUuid)).Should().BeFalse();

        var source = await fixture.Source();
        await Arrive(uuid, source.ArtistId);
        (await Pending(owner.SubjectUuid)).Should().BeTrue();
        var replay = await lookups.LookupLegacyAsync(attemptUuid, uuid);
        replay.Result!.Replayed.Should().BeTrue();
        replay.Result.Status.Should().Be(NormalizationCatalogBindingStatus.Missing);
        (await evidence.GetCurrentInputAsync(LegacyResource(uuid))).Should().BeNull();

        var retry = await lookups.RetryAsync(Guid.NewGuid(), missing.Registration.LookupUuid);
        retry.Result!.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        retry.Result.HandoffUuid.Should().NotBeNull();
        retry.Registration.Should().Be(missing.Registration);
        (await Pending(owner.SubjectUuid)).Should().BeFalse();
    }

    [Test]
    public async Task LegacyDeliveryResumesAfterRequestRegistrationAndAfterPublicationWithoutAnotherRefresh()
    {
        var source = await fixture.Source();
        var bound = await lookups.LookupLegacyAsync(Guid.NewGuid(), source.SourceUuid);
        bound.Result!.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        var handoff = (await store.GetHandoffAsync(bound.Result.HandoffUuid!.Value))!;
        var head = (await evidence.GetCurrentInputAsync(LegacyResource(source.SourceUuid)))!;
        var playback = await Playback(source.TrackUuid);

        // Resume the first crash boundary: the immutable target request exists before preparation.
        await proposals.GetOrCreateAttemptAsync(
            handoff.ProcessingAttemptUuid,
            handoff.SourceUuid,
            handoff.ProcessingVersion!,
            initiatingHandoffUuid: handoff.HandoffUuid
        );
        var delivered = await dispatcher.DispatchAsync(handoff.HandoffUuid);
        delivered
            .ProcessingResult!.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);
        (await store.GetHandoffAsync(handoff.HandoffUuid))!
            .CompletedResult!.AttemptUuid.Should()
            .Be(handoff.ProcessingAttemptUuid);
        (await proposals.GetAsync(source.SourceUuid))!
            .AttemptUuid.Should()
            .Be(handoff.ProcessingAttemptUuid);
        var replay = await dispatcher.DispatchAsync(handoff.HandoffUuid);
        replay.ProcessingResult!.Replayed.Should().BeTrue();
        var repeatedReceipt = await store.AcknowledgeHandoffAsync(
            handoff.HandoffUuid,
            delivered.ProcessingResult with
            {
                Replayed = true,
            }
        );
        repeatedReceipt.CompletedResult!.AttemptUuid.Should().Be(handoff.ProcessingAttemptUuid);
        var changedReceipt = () =>
            store.AcknowledgeHandoffAsync(
                handoff.HandoffUuid,
                delivered.ProcessingResult with
                {
                    Reason = "another outcome",
                }
            );
        await changedReceipt.Should().ThrowAsync<NormalizationConflictException>();
        (await store.GetPendingHandoffsAsync(1000))
            .Should()
            .NotContain(item => item.HandoffUuid == handoff.HandoffUuid);
        (await evidence.GetCurrentInputAsync(LegacyResource(source.SourceUuid)))!
            .Generation.Should()
            .Be(head.Generation);
        (await evidence.GetFetchHistoryAsync(LegacyResource(source.SourceUuid)))
            .Should()
            .ContainSingle();
        (await Playback(source.TrackUuid)).Should().Be(playback);

        // A second handoff stops after processing commits but before the delivery receipt.
        var second = await lookups.LookupLegacyAsync(Guid.NewGuid(), source.SourceUuid);
        var interrupted = (await store.GetHandoffAsync(second.Result!.HandoffUuid!.Value))!;
        await proposals.GetOrCreateAttemptAsync(
            interrupted.ProcessingAttemptUuid,
            interrupted.SourceUuid,
            interrupted.ProcessingVersion!,
            initiatingHandoffUuid: interrupted.HandoffUuid
        );
        var processed = await processor.ProcessLegacyHandoffAsync(
            interrupted.ProcessingAttemptUuid,
            interrupted.HandoffUuid
        );
        processed.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Published);
        (await store.GetHandoffAsync(interrupted.HandoffUuid))!.CompletedResult.Should().BeNull();
        var secondHead = (await evidence.GetCurrentInputAsync(LegacyResource(source.SourceUuid)))!;
        var recovered = await dispatcher.DispatchAsync(interrupted.HandoffUuid);
        recovered.ProcessingResult!.Replayed.Should().BeTrue();
        (await store.GetHandoffAsync(interrupted.HandoffUuid))!
            .CompletedResult!.AttemptUuid.Should()
            .Be(interrupted.ProcessingAttemptUuid);
        (await proposals.GetAsync(source.SourceUuid))!
            .AttemptUuid.Should()
            .Be(interrupted.ProcessingAttemptUuid);
        (await evidence.GetCurrentInputAsync(LegacyResource(source.SourceUuid)))!
            .Generation.Should()
            .Be(secondHead.Generation);
    }

    [TestCase("archive.org")]
    [TestCase("phish.in")]
    public async Task RetainedNegativeBecomesPendingOnQualifiedArrivalAndBoundHandoffPublishesProposals(
        string provider
    )
    {
        var key =
            provider == "phish.in"
                ? Random.Shared.Next(1000000, int.MaxValue).ToString()
                : "lookup-" + Guid.NewGuid().ToString("N");
        var input = await Retain(provider, key, "missing.mp3");
        var negative = await lookups.LookupRetainedAsync(Guid.NewGuid(), input);
        negative.Result!.Status.Should().Be(NormalizationCatalogBindingStatus.Missing);
        var owner = (await fixture.Work.GetSubjectAsync(Owner(negative)))!;
        (await Pending(owner.SubjectUuid)).Should().BeFalse();
        var source = await fixture.Source();
        await Qualify(source, provider, key);
        (await Pending(owner.SubjectUuid)).Should().BeTrue();

        var bound = await lookups.RetryAsync(Guid.NewGuid(), negative.Registration.LookupUuid);
        bound.Result!.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        if (provider == "archive.org")
        {
            bound
                .Preparation!.Binding!.Files.Should()
                .ContainSingle()
                .Which.Binding.Status.Should()
                .NotBe(NormalizationCatalogBindingStatus.Bound);
        }
        var handoffUuid = bound.Result.HandoffUuid!.Value;
        var delivery = await dispatcher.DispatchAsync(handoffUuid);
        delivery
            .ProcessingResult!.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);
        (await store.GetHandoffAsync(handoffUuid))!.CompletedResult.Should().NotBeNull();
        (await store.GetPendingHandoffsAsync(1000))
            .Should()
            .NotContain(item => item.HandoffUuid == handoffUuid);
        (await proposals.GetAsync(source.SourceUuid))!
            .Preparation.Binding.InputRevisionUuid.Should()
            .Be(input.InputRevisionUuid);
    }

    [Test]
    public async Task NegativeLookupPreservesEarlierExtractionAndPlayback()
    {
        var source = await fixture.Source();
        (await processor.ProcessAsync(Guid.NewGuid(), source.SourceUuid))
            .Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);
        var extraction = (await proposals.GetAsync(source.SourceUuid))!;
        var playback = await Playback(source.TrackUuid);

        var conflict = await lookups.LookupLegacyAsync(
            Guid.NewGuid(),
            source.SourceUuid,
            Guid.NewGuid()
        );

        conflict.Result!.Status.Should().NotBe(NormalizationCatalogBindingStatus.Bound);
        conflict.Result.HandoffUuid.Should().BeNull();
        (await proposals.GetAsync(source.SourceUuid)).Should().BeEquivalentTo(extraction);
        (await Playback(source.TrackUuid)).Should().Be(playback);
        (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("extractions", "recording", source.SourceUuid)
            )
        )!
            .PublishedWorkUuid.Should()
            .Be(extraction.AttemptUuid);
    }

    [Test]
    public async Task ChangedAttemptRequestConflictsAndUnknownRetainedInputCannotAdmitLookupWork()
    {
        var attemptUuid = Guid.NewGuid();
        var first = await lookups.LookupLegacyAsync(attemptUuid, Guid.NewGuid());
        var changed = () => lookups.LookupLegacyAsync(attemptUuid, Guid.NewGuid());
        await changed.Should().ThrowAsync<NormalizationConflictException>();
        var changedScope = () =>
            lookups.LookupLegacyAsync(
                attemptUuid,
                Guid.Parse(first.Request.Resource.ProviderKey),
                Guid.NewGuid()
            );
        await changedScope.Should().ThrowAsync<NormalizationConflictException>();

        var oldSourceUuid = Guid.NewGuid();
        var historical = await store.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            new(NormalizationRecordingLookupMode.LegacyCatalog, LegacyResource(oldSourceUuid)),
            "recording-lookup-old-version"
        );
        historical = await store.SavePreparationAsync(
            historical.AttemptUuid,
            new(null, await bindings.ProbeRecordingUuidAsync(oldSourceUuid), null)
        );
        var oldTicket = await Admit(historical);
        await fixture.Work.TryPublishAsync(
            oldTicket.WorkUuid,
            async (connection, transaction, ct) =>
            {
                await store.WritePublishedAsync(connection, transaction, historical, oldTicket, ct);
            }
        );
        var historicalReplay = await lookups.LookupLegacyAsync(
            historical.AttemptUuid,
            oldSourceUuid
        );
        historicalReplay.Result!.Replayed.Should().BeTrue();
        historicalReplay.Result.Status.Should().Be(NormalizationCatalogBindingStatus.Missing);
        historicalReplay.ProcessingVersion.Should().Be(historical.ProcessingVersion);

        var unknownAttempt = Guid.NewGuid();
        var unknown = () =>
            lookups.LookupRetainedAsync(
                unknownAttempt,
                new(
                    Guid.NewGuid(),
                    new("archive.org", "item_metadata", "absent-" + Guid.NewGuid().ToString("N"))
                )
            );
        await unknown.Should().ThrowAsync<NormalizationConflictException>();
        (
            await fixture.Db.WithWriteConnection(connection =>
                connection.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM normalization.work_runs WHERE uuid=@unknownAttempt)",
                    new { unknownAttempt }
                )
            )
        )
            .Should()
            .BeFalse();
    }

    [Test]
    public async Task RetainedAttemptPinsItsOriginalInputAndRetryUsesTheNewRevision()
    {
        var key = "lookup-revision-" + Guid.NewGuid().ToString("N");
        var originalInput = await Retain("archive.org", key, "first.mp3");
        var attemptUuid = Guid.NewGuid();
        var original = await lookups.LookupRetainedAsync(attemptUuid, originalInput);
        var changedInput = await Retain("archive.org", key, "second.mp3");
        changedInput.InputRevisionUuid.Should().NotBe(originalInput.InputRevisionUuid);

        var changed = () => lookups.LookupRetainedAsync(attemptUuid, changedInput);
        await changed.Should().ThrowAsync<NormalizationConflictException>();
        var replay = await lookups.LookupRetainedAsync(attemptUuid, originalInput);
        replay.Result!.Replayed.Should().BeTrue();
        replay.Preparation!.Binding!.InputRevisionUuid.Should().Be(originalInput.InputRevisionUuid);

        var retry = await lookups.RetryAsync(Guid.NewGuid(), original.Registration.LookupUuid);
        retry.Request.Input.Should().Be(changedInput);
        retry.Preparation!.Binding!.InputRevisionUuid.Should().Be(changedInput.InputRevisionUuid);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ArrivalRejectsTheSavedAbsenceBeforeAdmissionOrPublication(bool admitted)
    {
        var uuid = Guid.NewGuid();
        var attempt = await store.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            new(NormalizationRecordingLookupMode.LegacyCatalog, LegacyResource(uuid)),
            NormalizationRecordingLookupService.Version
        );
        var probe = await bindings.ProbeRecordingUuidAsync(uuid);
        attempt = await store.SavePreparationAsync(attempt.AttemptUuid, new(null, probe, null));
        if (admitted)
        {
            await Admit(attempt);
        }
        var source = await fixture.Source();
        await Arrive(uuid, source.ArtistId);

        var result = await lookups.LookupLegacyAsync(attempt.AttemptUuid, uuid);

        result.Result!.Disposition.Should().Be(NormalizationRecordingLookupDisposition.Stale);
        result.Result.HandoffUuid.Should().BeNull();
        result.Preparation!.AbsenceProbe.Should().Be(probe);
        (await lookups.GetAsync(attempt.Registration.LookupUuid)).Should().BeNull();
        (await store.GetHandoffAsync(attempt.AttemptUuid)).Should().BeNull();
        var retry = await lookups.RetryAsync(Guid.NewGuid(), attempt.Registration.LookupUuid);
        retry.Result!.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
    }

    [Test]
    public async Task StaleRetryKeepsThePriorPublishedLookupAndItsOwnerPending()
    {
        var uuid = Guid.NewGuid();
        var original = await lookups.LookupLegacyAsync(Guid.NewGuid(), uuid);
        var owner = (await fixture.Work.GetSubjectAsync(Owner(original)))!;
        var retry = await store.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            original.Request,
            NormalizationRecordingLookupService.Version
        );
        retry = await store.SavePreparationAsync(
            retry.AttemptUuid,
            new(null, await bindings.ProbeRecordingUuidAsync(uuid), owner.DesiredGeneration)
        );
        retry.Registration.Should().Be(original.Registration);
        var source = await fixture.Source();
        await Arrive(uuid, source.ArtistId);

        var stale = await lookups.LookupLegacyAsync(retry.AttemptUuid, uuid);

        stale.Result!.Disposition.Should().Be(NormalizationRecordingLookupDisposition.Stale);
        var current = (await lookups.GetAsync(original.Registration.LookupUuid))!;
        current.AttemptUuid.Should().Be(original.AttemptUuid);
        current.Result!.Status.Should().Be(NormalizationCatalogBindingStatus.Missing);
        (await fixture.Work.GetSubjectAsync(Owner(original)))!
            .PublishedWorkUuid.Should()
            .Be(original.AttemptUuid);
        (await Pending(owner.SubjectUuid)).Should().BeTrue();
    }

    [Test]
    public async Task LegacyLookupPeersSettleWithoutRefreshingEachOtherAndRecaptureChangedCatalogFacts()
    {
        var source = await fixture.Source();
        var artistUuid = await fixture.Db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<Guid>("SELECT uuid FROM artists WHERE id=@ArtistId", source)
        );
        var unscoped = await lookups.LookupLegacyAsync(Guid.NewGuid(), source.SourceUuid);
        var scoped = await lookups.LookupLegacyAsync(Guid.NewGuid(), source.SourceUuid, artistUuid);
        scoped.Registration.LookupUuid.Should().NotBe(unscoped.Registration.LookupUuid);
        var unscopedOwner = (await fixture.Work.GetSubjectAsync(Owner(unscoped)))!;
        var scopedOwner = (await fixture.Work.GetSubjectAsync(Owner(scoped)))!;
        var head = (await evidence.GetCurrentInputAsync(LegacyResource(source.SourceUuid)))!;
        (await Pending(unscopedOwner.SubjectUuid)).Should().BeTrue();

        var settled = await lookups.RetryAsync(Guid.NewGuid(), unscoped.Registration.LookupUuid);

        settled.Result!.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        (await evidence.GetCurrentInputAsync(LegacyResource(source.SourceUuid)))!
            .Generation.Should()
            .Be(head.Generation);
        (await Pending(unscopedOwner.SubjectUuid)).Should().BeFalse();
        (await Pending(scopedOwner.SubjectUuid)).Should().BeFalse();
        var firstDelivery = await dispatcher.DispatchAsync(settled.Result.HandoffUuid!.Value);
        firstDelivery
            .ProcessingResult!.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);

        // Reusing the capture still prepares a new extraction against its current contributor CAS.
        var repeated = await lookups.RetryAsync(Guid.NewGuid(), unscoped.Registration.LookupUuid);
        var repeatedDelivery = await dispatcher.DispatchAsync(repeated.Result!.HandoffUuid!.Value);
        repeatedDelivery
            .ProcessingResult!.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);
        await lookups.RetryAsync(Guid.NewGuid(), scoped.Registration.LookupUuid);
        (await evidence.GetCurrentInputAsync(LegacyResource(source.SourceUuid)))!
            .Generation.Should()
            .Be(head.Generation);
        (await Pending(unscopedOwner.SubjectUuid)).Should().BeFalse();
        (await Pending(scopedOwner.SubjectUuid)).Should().BeFalse();

        await fixture.Db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                "UPDATE sources SET description=@description WHERE id=@SourceId",
                new { source.SourceId, description = "Changed lookup facts " + Guid.NewGuid() }
            )
        );
        var changed = await lookups.RetryAsync(Guid.NewGuid(), unscoped.Registration.LookupUuid);
        var refreshedHead = (
            await evidence.GetCurrentInputAsync(LegacyResource(source.SourceUuid))
        )!;
        refreshedHead.Generation.Should().BeGreaterThan(head.Generation);
        refreshedHead.Revision.Uuid.Should().NotBe(head.Revision.Uuid);
        changed.Preparation!.Binding!.InputRevisionUuid.Should().Be(refreshedHead.Revision.Uuid);

        await lookups.RetryAsync(Guid.NewGuid(), scoped.Registration.LookupUuid);
        var afterPeerRefresh = (
            await evidence.GetCurrentInputAsync(LegacyResource(source.SourceUuid))
        )!;
        var resettled = await lookups.RetryAsync(Guid.NewGuid(), unscoped.Registration.LookupUuid);
        (await evidence.GetCurrentInputAsync(LegacyResource(source.SourceUuid)))!
            .Generation.Should()
            .Be(afterPeerRefresh.Generation);
        (await Pending(unscopedOwner.SubjectUuid)).Should().BeFalse();
        (await Pending(scopedOwner.SubjectUuid)).Should().BeFalse();
        await lookups.RetryAsync(Guid.NewGuid(), scoped.Registration.LookupUuid);
        var finalDelivery = await dispatcher.DispatchAsync(resettled.Result!.HandoffUuid!.Value);
        finalDelivery
            .ProcessingResult!.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);
        (await evidence.GetCurrentInputAsync(LegacyResource(source.SourceUuid)))!
            .Generation.Should()
            .Be(afterPeerRefresh.Generation);
        (await Pending(unscopedOwner.SubjectUuid)).Should().BeFalse();
        (await Pending(scopedOwner.SubjectUuid)).Should().BeFalse();
    }

    [Test]
    public async Task PublicationFailureRollsBackOutcomeHandoffAndWatermarkThenResumesOriginalPreparation()
    {
        var source = await fixture.Source();
        var attempt = await PrepareLegacy(source.SourceUuid);
        var ticket = await Admit(attempt);
        var failed = () =>
            fixture.Work.TryPublishAsync(
                ticket.WorkUuid,
                async (connection, transaction, ct) =>
                {
                    await store.WritePublishedAsync(connection, transaction, attempt, ticket, ct);
                    throw new InvalidOperationException("publication interrupted");
                }
            );

        await failed.Should().ThrowAsync<InvalidOperationException>();

        (await store.GetAttemptAsync(attempt.AttemptUuid))!.Result.Should().BeNull();
        (await store.GetHandoffAsync(attempt.AttemptUuid)).Should().BeNull();
        (await lookups.GetAsync(attempt.Registration.LookupUuid)).Should().BeNull();
        (await Pending(ticket.SubjectUuid)).Should().BeTrue();
        var fetches = (await evidence.GetFetchHistoryAsync(LegacyResource(source.SourceUuid)))
            .Select(fetch => fetch.FetchUuid)
            .ToArray();
        var recovered = await lookups.LookupLegacyAsync(attempt.AttemptUuid, source.SourceUuid);
        recovered.Result!.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        recovered.Preparation.Should().BeEquivalentTo(attempt.Preparation);
        (await store.GetHandoffAsync(attempt.AttemptUuid)).Should().NotBeNull();
        (await Pending(ticket.SubjectUuid)).Should().BeFalse();
        (await evidence.GetFetchHistoryAsync(LegacyResource(source.SourceUuid)))
            .Select(fetch => fetch.FetchUuid)
            .Should()
            .Equal(fetches);
    }

    [Test]
    public async Task CanceledDeliveryLeavesHandoffPendingAndOriginalAttemptCanResume()
    {
        var source = await fixture.Source();
        var attempt = await lookups.LookupLegacyAsync(Guid.NewGuid(), source.SourceUuid);
        var handoffUuid = attempt.Result!.HandoffUuid!.Value;
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var dispatch = () => dispatcher.DispatchAsync(handoffUuid, canceled.Token);
        await dispatch.Should().ThrowAsync<OperationCanceledException>();
        (await store.GetHandoffAsync(handoffUuid))!.CompletedResult.Should().BeNull();

        var recovered = await dispatcher.DispatchAsync(handoffUuid);
        recovered
            .ProcessingResult!.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);
    }

    private async Task<NormalizationRecordingLookupAttempt> PrepareLegacy(Guid sourceUuid)
    {
        var attempt = await store.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            new(NormalizationRecordingLookupMode.LegacyCatalog, LegacyResource(sourceUuid)),
            NormalizationRecordingLookupService.Version
        );
        var snapshot = (await legacy.RefreshRecordingAsync(sourceUuid))!;
        var decoded = new NormalizationProviderDecoder()
            .Decode(snapshot.Capture.Revision)
            .Recordings.Single();
        var binding = await bindings.BindRefreshedRecordingAsync(snapshot, decoded);
        var extractionOwner = NormalizationRecordingProcessor.Owner(sourceUuid);
        var extractionState = await fixture.Work.GetSubjectAsync(extractionOwner);
        var contribution = await fixture.Work.GetInputContributionAsync(
            extractionOwner,
            NormalizationRecordingProcessor.Contributor(sourceUuid)
        );
        var extraction = new NormalizationRecordingPreparation(
            snapshot.Capture.Fetch.FetchUuid,
            snapshot.CatalogInputs,
            binding,
            extractionState?.DesiredGeneration,
            contribution?.Revision ?? 0
        );
        return await store.SavePreparationAsync(
            attempt.AttemptUuid,
            new(
                binding,
                null,
                null,
                snapshot.Capture.Fetch.FetchUuid,
                snapshot.CatalogInputs,
                extraction,
                Guid.NewGuid(),
                NormalizationRecordingProcessor.Version
            )
        );
    }

    private Task<NormalizationWorkTicket> Admit(NormalizationRecordingLookupAttempt attempt)
    {
        var preparation = attempt.Preparation!;
        var binding = preparation.Binding;
        return fixture.Work.BeginWorkAsync(
            attempt.AttemptUuid,
            Owner(attempt),
            attempt.ProcessingVersion,
            binding?.Inputs.Select(input => input.Resource).ToArray() ?? [],
            [],
            preparation.ExpectedConfigurationGeneration,
            catalogInputs: binding?.CatalogInputs,
            sourceIdentities: binding?.SourceIdentities,
            mappingRegistry: binding?.MappingRegistry,
            expectedInputRevisions: binding?.Inputs,
            sourceUuids: binding?.SourceUuids ?? [preparation.AbsenceProbe!.Dependency]
        );
    }

    private async Task<NormalizationBindingInputReference> Retain(
        string provider,
        string key,
        params string[] filenames
    )
    {
        var resource = new NormalizationResourceKey(
            provider,
            provider == "phish.in" ? "show" : "item_metadata",
            key
        );
        var payload =
            provider == "phish.in"
                ? JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        id = int.Parse(key),
                        date = "1990-01-01",
                        tracks = Array.Empty<object>(),
                    }
                )
                : JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        metadata = new { identifier = key },
                        files = filenames.Select(name => new { name, format = "VBR MP3" }),
                    }
                );
        var current = await evidence.GetCurrentInputAsync(resource);
        var captured = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                payload,
                "application/json",
                provider == "phish.in" ? "phishin-show-object-v1" : "archive-item-response-v1",
                current?.Generation ?? 0,
                DateTimeOffset.UtcNow
            )
        );
        return new(captured.Revision.Uuid, resource);
    }

    private async Task Qualify(CatalogSource source, string provider, string key)
    {
        await fixture.Db.WithWriteConnection(async connection =>
        {
            var upstream = await connection.QuerySingleOrDefaultAsync<int?>(
                "SELECT id FROM upstream_sources WHERE name=@provider ORDER BY id LIMIT 1",
                new { provider }
            );
            upstream ??= await connection.QuerySingleAsync<int>(
                "INSERT INTO upstream_sources(id,name,url) SELECT COALESCE(max(id),0)+1,@provider,@url FROM upstream_sources RETURNING id",
                new { provider, url = "https://" + provider }
            );
            await connection.ExecuteAsync(
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

    private Task Arrive(Guid uuid, int artistId) =>
        fixture.Db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                "INSERT INTO sources(artist_id,upstream_identifier,display_date,uuid,updated_at,is_soundboard,is_remaster,has_jamcharts) VALUES(@artistId,@identifier,'2026-10-17',@uuid,now(),false,false,false)",
                new
                {
                    artistId,
                    identifier = "arrived-" + uuid,
                    uuid,
                }
            )
        );

    private Task<string> Playback(Guid uuid) =>
        fixture.Db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<string>(
                "SELECT jsonb_build_object('source',s.uuid,'track',tr.uuid,'set',ss.uuid,'media',tr.mp3_url)::text FROM source_tracks tr JOIN sources s ON s.id=tr.source_id JOIN source_sets ss ON ss.id=tr.source_set_id WHERE tr.uuid=@uuid",
                new { uuid }
            )
        );

    private async Task<bool> Pending(Guid subjectUuid)
    {
        Guid? after = null;
        while (true)
        {
            var page = await fixture.Work.GetPendingSubjectsAsync(after, 1000);
            if (page.Any(subject => subject.SubjectUuid == subjectUuid))
            {
                return true;
            }
            if (page.Count < 1000)
            {
                return false;
            }
            after = page[^1].SubjectUuid;
        }
    }

    private static NormalizationResourceKey LegacyResource(Guid sourceUuid) =>
        new("legacy_catalog", "recording", sourceUuid.ToString("D"));

    private static NormalizationSubjectKey Owner(NormalizationRecordingLookupAttempt attempt) =>
        new("lookups", "recording", attempt.Registration.LookupUuid);
}
