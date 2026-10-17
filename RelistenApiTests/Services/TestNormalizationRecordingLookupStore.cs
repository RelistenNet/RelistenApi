using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

/// <summary>Attempt recovery preserves the winning facts before any work owner is configured.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationRecordingLookupStore
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationRecordingLookupStore store = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        store = new(fixture.Db);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task LogicalRegistrationIsSharedButAttemptsAndArtistScopesRemainDistinct()
    {
        var request = LegacyRequest();
        var attempts = await Task.WhenAll(
            store.GetOrCreateAttemptAsync(Guid.NewGuid(), request, "store-v1"),
            store.GetOrCreateAttemptAsync(Guid.NewGuid(), request, "store-v1")
        );
        attempts[0].Registration.Should().Be(attempts[1].Registration);
        attempts[0].AttemptUuid.Should().NotBe(attempts[1].AttemptUuid);
        var scoped = await store.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            request with
            {
                ExpectedArtistUuid = Guid.NewGuid(),
            },
            "store-v1"
        );
        scoped.Registration.LookupUuid.Should().NotBe(attempts[0].Registration.LookupUuid);
        (await store.GetRegistrationAsync(attempts[0].Registration.LookupUuid))
            .Should()
            .Be(attempts[0].Registration);
        (await store.GetCurrentAsync(attempts[0].Registration.LookupUuid)).Should().BeNull();
    }

    [Test]
    public async Task ExactHeaderReplayPreservesAttemptAndChangedRequestOrVersionConflicts()
    {
        var request = LegacyRequest();
        var attempt = await store.GetOrCreateAttemptAsync(Guid.NewGuid(), request, "store-v1");
        await store.CompleteStaleAsync(attempt, "changed_during_preparation");
        var replay = await store.GetOrCreateAttemptAsync(attempt.AttemptUuid, request, "store-v1");
        replay.Result!.Disposition.Should().Be(NormalizationRecordingLookupDisposition.Stale);
        var otherRequest = () =>
            store.GetOrCreateAttemptAsync(attempt.AttemptUuid, LegacyRequest(), "store-v1");
        await otherRequest.Should().ThrowAsync<NormalizationConflictException>();
        var otherVersion = () =>
            store.GetOrCreateAttemptAsync(attempt.AttemptUuid, request, "store-v2");
        await otherVersion.Should().ThrowAsync<NormalizationConflictException>();
        (await store.GetAttemptAsync(attempt.AttemptUuid)).Should().BeEquivalentTo(replay);
    }

    [Test]
    public async Task ConcurrentPreparationReturnsOneWinnerAndAnUnpreparedLoserCannotTerminalizeIt()
    {
        var request = LegacyRequest();
        var unprepared = await store.GetOrCreateAttemptAsync(Guid.NewGuid(), request, "store-v1");
        var first = Absence(request, 0);
        var second = Absence(request, 1);
        var prepared = await Task.WhenAll(
            store.SavePreparationAsync(unprepared.AttemptUuid, first),
            store.SavePreparationAsync(unprepared.AttemptUuid, second)
        );
        prepared[0].Preparation.Should().BeEquivalentTo(prepared[1].Preparation);
        var lost = await store.CompleteStaleAsync(unprepared, "discarded_loser");
        lost.Result.Should().BeNull();
        lost.Preparation.Should().BeEquivalentTo(prepared[0].Preparation);
        var stale = await store.CompleteStaleAsync(lost, "original_facts_changed");
        var replay = await store.SavePreparationAsync(stale.AttemptUuid, first);
        replay.Should().BeEquivalentTo(stale);
    }

    [Test]
    public async Task TerminalHeaderMismatchCannotHideBehindFirstWinner()
    {
        var request = LegacyRequest();
        var attempt = await store.GetOrCreateAttemptAsync(Guid.NewGuid(), request, "store-v1");
        var terminal = await store.CompleteStaleAsync(attempt, "finished");
        var changed = () =>
            store.CompleteStaleAsync(
                terminal with
                {
                    ProcessingVersion = "another-version",
                },
                "ignored"
            );
        await changed.Should().ThrowAsync<NormalizationConflictException>();
        (await store.GetAttemptAsync(attempt.AttemptUuid)).Should().BeEquivalentTo(terminal);
    }

    [Test]
    public async Task InvalidInputFreePreparationCannotReplaceTheRequestOrCreateAWorkOwner()
    {
        var request = LegacyRequest();
        var attempt = await store.GetOrCreateAttemptAsync(Guid.NewGuid(), request, "store-v1");
        var wrongUuid = Absence(LegacyRequest(), 0);
        var invalid = () => store.SavePreparationAsync(attempt.AttemptUuid, wrongUuid);
        await invalid.Should().ThrowAsync<ArgumentException>();
        (await store.GetAttemptAsync(attempt.AttemptUuid))!.Preparation.Should().BeNull();
        (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("lookups", "recording", attempt.Registration.LookupUuid)
            )
        )
            .Should()
            .BeNull();
        var saved = await store.SavePreparationAsync(attempt.AttemptUuid, Absence(request, 0));
        saved.Preparation.Should().NotBeNull();
    }

    [TestCase("missing_producer_fence")]
    [TestCase("invalid_producer_stamp")]
    [TestCase("conflicting_producer_stamps")]
    [TestCase("downstream_configuration")]
    [TestCase("downstream_contribution")]
    public async Task InvalidLegacyPreparationCannotPoisonWriteOnceRecovery(string change)
    {
        var source = await fixture.Source();
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var snapshot = (
            await new NormalizationLegacyInputService(fixture.Db, evidence).RefreshRecordingAsync(
                source.SourceUuid
            )
        )!;
        var recording = new NormalizationProviderDecoder()
            .Decode(snapshot.Capture.Revision)
            .Recordings.Single();
        var binding = await new NormalizationCatalogBindingService(
            fixture.Db
        ).BindRefreshedRecordingAsync(snapshot, recording);
        var request = new NormalizationRecordingLookupRequest(
            NormalizationRecordingLookupMode.LegacyCatalog,
            binding.Resource
        );
        var attempt = await store.GetOrCreateAttemptAsync(Guid.NewGuid(), request, "store-v1");
        var downstream = new NormalizationRecordingPreparation(
            snapshot.Capture.Fetch.FetchUuid,
            snapshot.CatalogInputs,
            binding,
            null,
            0
        );
        var valid = new NormalizationRecordingLookupPreparation(
            binding,
            null,
            null,
            snapshot.Capture.Fetch.FetchUuid,
            snapshot.CatalogInputs,
            downstream,
            Guid.NewGuid(),
            NormalizationRecordingProcessor.Version
        );
        var invalid = change switch
        {
            "missing_producer_fence" => valid with
            {
                Binding = binding with { CatalogInputs = [] },
            },
            "invalid_producer_stamp" => valid with
            {
                ProducerCatalogInputs = [snapshot.CatalogInputs[0] with { Generation = -1 }],
            },
            "conflicting_producer_stamps" => valid with
            {
                ProducerCatalogInputs =
                [
                    snapshot.CatalogInputs[0],
                    snapshot.CatalogInputs[0] with
                    {
                        Generation = snapshot.CatalogInputs[0].Generation + 1,
                    },
                ],
            },
            "downstream_configuration" => valid with
            {
                LegacyPreparation = downstream with { ExpectedConfigurationGeneration = -1 },
            },
            "downstream_contribution" => valid with
            {
                LegacyPreparation = downstream with { ExpectedContributionRevision = -1 },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };

        if (
            change
            is "missing_producer_fence"
                or "invalid_producer_stamp"
                or "conflicting_producer_stamps"
        )
        {
            // Keep both copies consistent so only the invalid producer read set can reject
            // this save; a generic nested-preparation mismatch would mask the regression.
            invalid = invalid with
            {
                LegacyPreparation = downstream with
                {
                    Binding = invalid.Binding!,
                    ProducerCatalogInputs = invalid.ProducerCatalogInputs!,
                },
            };
        }

        var saveInvalid = () => store.SavePreparationAsync(attempt.AttemptUuid, invalid);
        await saveInvalid.Should().ThrowAsync<ArgumentException>();
        (await store.GetAttemptAsync(attempt.AttemptUuid))!.Preparation.Should().BeNull();
        (await store.SavePreparationAsync(attempt.AttemptUuid, valid))
            .Preparation.Should()
            .BeEquivalentTo(valid);
    }

    [TestCase("capture")]
    [TestCase("configuration")]
    [TestCase("contribution")]
    public async Task InitiatingHandoffCannotBeChangedOrReplacedWithAnotherCapturePreparation(
        string change
    )
    {
        var source = await fixture.Source();
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var legacy = new NormalizationLegacyInputService(fixture.Db, evidence);
        var bindings = new NormalizationCatalogBindingService(fixture.Db);
        var proposals = new NormalizationRecordingProposalService(fixture.Db);
        var lookups = new NormalizationRecordingLookupService(
            store,
            legacy,
            evidence,
            bindings,
            new(),
            fixture.Work
        );
        var published = await lookups.LookupLegacyAsync(Guid.NewGuid(), source.SourceUuid);
        var handoff = (await store.GetHandoffAsync(published.Result!.HandoffUuid!.Value))!;

        // Processing can be invoked directly before delivery. A second admitted capture
        // has its own receipt even when catalog wording and the immutable bytes agree.
        var snapshot = (await legacy.RefreshRecordingAsync(source.SourceUuid))!;
        var recording = new NormalizationProviderDecoder()
            .Decode(snapshot.Capture.Revision)
            .Recordings.Single();
        var binding = await bindings.BindRefreshedRecordingAsync(snapshot, recording);
        var original = handoff.LegacyPreparation!;
        var differentPreparation = change switch
        {
            "capture" => original with
            {
                FetchUuid = snapshot.Capture.Fetch.FetchUuid,
                ProducerCatalogInputs = snapshot.CatalogInputs,
                Binding = binding,
            },
            "configuration" => original with
            {
                ExpectedConfigurationGeneration =
                    (original.ExpectedConfigurationGeneration ?? 0) + 1,
            },
            "contribution" => original with
            {
                ExpectedContributionRevision = original.ExpectedContributionRevision + 1,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        var attempt = await proposals.GetOrCreateAttemptAsync(
            handoff.ProcessingAttemptUuid,
            source.SourceUuid,
            handoff.ProcessingVersion!,
            initiatingHandoffUuid: handoff.HandoffUuid
        );
        var changedInitiator = () =>
            proposals.GetOrCreateAttemptAsync(
                handoff.ProcessingAttemptUuid,
                source.SourceUuid,
                handoff.ProcessingVersion!
            );
        await changedInitiator.Should().ThrowAsync<NormalizationConflictException>();
        var saveDifferent = () =>
            proposals.SavePreparationAsync(
                handoff.ProcessingAttemptUuid,
                differentPreparation with
                {
                    ExpectedContributions = [],
                }
            );
        await saveDifferent.Should().ThrowAsync<NormalizationConflictException>();
        (await proposals.GetAttemptAsync(attempt.AttemptUuid))!.Preparation.Should().BeNull();

        // The failed save does not poison recovery. A genuine pre-preparation terminal
        // result is deliverable because its immutable request still identifies this handoff.
        var terminal = await proposals.CompleteAsync(
            attempt,
            NormalizationRecordingProcessingDisposition.Unresolved,
            "synthetic_unsupported_peer"
        );
        await store.AcknowledgeHandoffAsync(handoff.HandoffUuid, terminal.Result!);
        var unchanged = (await store.GetHandoffAsync(handoff.HandoffUuid))!;
        unchanged.CompletedResult.Should().BeEquivalentTo(terminal.Result);
        unchanged.LegacyPreparation.Should().BeEquivalentTo(handoff.LegacyPreparation);
        (await store.GetPendingHandoffsAsync(mode: NormalizationRecordingLookupMode.LegacyCatalog))
            .Should()
            .NotContain(item => item.HandoffUuid == handoff.HandoffUuid);
    }

    [Test]
    public async Task CancelledPreparationLeavesRequestResumable()
    {
        var request = LegacyRequest();
        var attempt = await store.GetOrCreateAttemptAsync(Guid.NewGuid(), request, "store-v1");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = () =>
            store.SavePreparationAsync(
                attempt.AttemptUuid,
                Absence(request, 0),
                cancellation.Token
            );
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        (await store.GetAttemptAsync(attempt.AttemptUuid))!.Preparation.Should().BeNull();
        (await store.SavePreparationAsync(attempt.AttemptUuid, Absence(request, 0)))
            .Preparation.Should()
            .NotBeNull();
    }

    private static NormalizationRecordingLookupRequest LegacyRequest() =>
        new(
            NormalizationRecordingLookupMode.LegacyCatalog,
            new("legacy_catalog", "recording", Guid.NewGuid().ToString("D"))
        );

    private static NormalizationRecordingLookupPreparation Absence(
        NormalizationRecordingLookupRequest request,
        long generation
    ) => new(null, new(false, new(Guid.Parse(request.Resource.ProviderKey), generation)), null);
}
