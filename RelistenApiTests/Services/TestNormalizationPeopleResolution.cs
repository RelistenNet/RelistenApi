using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

/// <summary>Complete published extraction feeds durable candidate proposals without accepted matching authority.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationPeopleResolution
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizedPeopleService people = null!;
    private NormalizationRecordingProposalService extractions = null!;
    private NormalizationRecordingProcessor extractor = null!;
    private NormalizationPeopleResolutionStore store = null!;
    private NormalizationPeopleResolutionProcessor processor = null!;
    private NormalizationCandidateService candidates = null!;
    private readonly NormalizationPeopleResolutionDecisionService decisions = new(new());

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        people = new(fixture.Db, fixture.Work);
        extractions = new(fixture.Db);
        var evidence = new NormalizationEvidenceService(fixture.Db);
        extractor = new(
            new(fixture.Db, evidence),
            evidence,
            new(fixture.Db),
            new(),
            new(new(), new()),
            fixture.Work,
            extractions,
            new(fixture.Db)
        );
        store = new(fixture.Db);
        candidates = new(fixture.Db);
        processor = new(people, extractions, candidates, decisions, store, fixture.Work);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [TestCase("source:2169954", "Dave Sorochty")]
    [TestCase("source:1329586", "Keith Litzenberger")]
    [TestCase("local:source:4283434", "Scott Bernstein")]
    public async Task RealCrossArtistCreditWordingRetainsCompleteCandidateFactsWithoutInventedKindOrAuthority(
        string evidenceId,
        string name
    )
    {
        await Person(name);
        var path = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Fixtures",
            "normalization",
            "evidence.jsonl"
        );
        using var rows = JsonDocument.Parse("[" + string.Join(",", File.ReadLines(path)) + "]");
        var fields = rows
            .RootElement.EnumerateArray()
            .Single(row => row.GetProperty("id").GetString() == evidenceId)
            .GetProperty("fields");
        string? Text(string key) =>
            fields.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        var source = await EmptySource();
        // Catalog rows are synthetic; provider credit wording is replayed from the retained corpus.
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET taper=@taper,transferrer=@transferrer,lineage=@lineage,source=@source,description=@description WHERE id=@id",
                new
                {
                    taper = Text("taper"),
                    transferrer = Text("transferrer"),
                    lineage = Text("lineage"),
                    source = Text("source"),
                    description = Text("description"),
                    id = source.SourceId,
                }
            )
        );
        var extraction = await Extract(source);
        var before = await AcceptedState();
        var epoch = await PeopleEpoch();

        var result = await processor.ResolveRecordingAsync(Guid.NewGuid(), extraction.AttemptUuid);

        result.Result!.Disposition.Should().Be(NormalizationPeopleResolutionDisposition.Published);
        var batch = (await store.GetAsync(source.SourceUuid))!;
        var claim = batch
            .Preparation.Inputs.SelectMany(input => input.Claims)
            .First(claim => claim.Proposal.Mention.Name == name);
        claim.Candidates!.Facts.Should().NotBeEmpty();
        claim.Comparisons.Should().HaveCount(claim.Candidates.Facts.Count);
        claim
            .Comparisons.Should()
            .OnlyContain(pair =>
                pair.Features.Any(feature =>
                    feature.Name == "person_kind"
                    && feature.Agreement == IdentityFeatureAgreement.Missing
                )
            );
        claim.Decision.AutomaticAcceptanceAuthorized.Should().BeFalse();
        (await AcceptedState()).Should().Be(before);
        (await PeopleEpoch()).Should().Be(epoch);
        (await processor.ResolveRecordingAsync(result.AttemptUuid, extraction.AttemptUuid))
            .Result!.Replayed.Should()
            .BeTrue();
    }

    [Test]
    public async Task NamesakesRemainCompetingCandidatesAndNoIdentityIsChosen()
    {
        var name = "Namesake " + Guid.NewGuid().ToString("N");
        var first = await Person(name);
        var second = await Person(name);
        var source = await Source(name);
        var extraction = await Extract(source);
        var result = await processor.ResolveRecordingAsync(Guid.NewGuid(), extraction.AttemptUuid);
        var claim = result.Preparation!.Inputs.Single().Claims.Single();
        claim.Candidates!.Facts.Select(fact => fact.Hit.Uuid).Should().Contain([first, second]);
        claim.Decision.Action.Should().Be(PeopleResolutionAction.Review);
        claim.Decision.PersonUuid.Should().BeNull();
        claim
            .Comparisons.Should()
            .NotContain(pair => pair.Disposition == IdentityMatchDisposition.AutomaticMatch);
    }

    [Test]
    public async Task EmptyCandidatePoolAndUnparsedCreditsRemainExplicitWithoutCreatingPeople()
    {
        var source = await Source("Cecily Unrepresented " + Guid.NewGuid().ToString("N"));
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync("UPDATE sources SET transferrer='unknown' WHERE id=@SourceId", source)
        );
        var extraction = await Extract(source);
        var before = await AcceptedState();
        var result = await processor.ResolveRecordingAsync(Guid.NewGuid(), extraction.AttemptUuid);
        var input = result.Preparation!.Inputs.Single();
        input.UnresolvedCredits.Should().NotBeEmpty();
        input.Claims.Should().ContainSingle().Which.Candidates!.Facts.Should().BeEmpty();
        input.Claims.Single().Decision.Action.Should().Be(PeopleResolutionAction.Unresolved);
        (await AcceptedState()).Should().Be(before);
    }

    [Test]
    public async Task UnsupportedCreditNameRemainsAProposalWithoutInventingAnEmptyCandidatePool()
    {
        var source = await Source("''");
        var extraction = await Extract(source);
        var result = await processor.ResolveRecordingAsync(Guid.NewGuid(), extraction.AttemptUuid);
        var claim = result.Preparation!.Inputs.Single().Claims.Single();

        claim.Proposal.Mention.Name.Should().Be("''");
        claim.Candidates.Should().BeNull();
        claim.Comparisons.Should().BeEmpty();
        claim.Decision.Action.Should().Be(PeopleResolutionAction.Unresolved);
        claim.Decision.Reason.Should().Be("unsupported_credit_query");
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().BeEmpty();
    }

    [Test]
    public async Task EmptyExtractionPublishesAnEmptyCompleteProposalWithoutWithdrawingAcceptedData()
    {
        var source = await EmptySource();
        var extraction = await Extract(source);
        var result = await processor.ResolveRecordingAsync(Guid.NewGuid(), extraction.AttemptUuid);
        result.Result!.Disposition.Should().Be(NormalizationPeopleResolutionDisposition.Published);
        result.Preparation!.Inputs.Should().ContainSingle().Which.Claims.Should().BeEmpty();
        result
            .Preparation.CreditContext.PeopleUniverse.Generation.Should()
            .BeGreaterThanOrEqualTo(0);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task NewRivalInvalidatesOriginalCandidatePreparationBeforeAdmissionOrPublication(
        bool beforeAdmission
    )
    {
        var name = "Rival " + Guid.NewGuid().ToString("N");
        var source = await Source(name);
        var extraction = await Extract(source);
        var staged = await Stage(extraction);
        if (!beforeAdmission)
        {
            await Admit(staged);
        }
        await Person(name);

        var result = await processor.ResolveRecordingAsync(
            staged.AttemptUuid,
            extraction.AttemptUuid
        );

        result.Result!.Disposition.Should().Be(NormalizationPeopleResolutionDisposition.Stale);
        (await store.GetAsync(source.SourceUuid)).Should().BeNull();
        var retry = await processor.ResolveRecordingAsync(Guid.NewGuid(), extraction.AttemptUuid);
        retry.Result!.Disposition.Should().Be(NormalizationPeopleResolutionDisposition.Published);
        retry.Preparation!.Inputs.Single().Claims.Single().Candidates!.Facts.Should().NotBeEmpty();
    }

    [Test]
    public async Task LaterExtractionReceiptCannotFreshenEarlierProposalsEvenWhenInputBytesAreUnchanged()
    {
        var source = await Source("Original Receipt " + Guid.NewGuid().ToString("N"));
        var first = await Extract(source);
        var second = await Extract(source);
        second
            .Preparation.Binding.InputRevisionUuid.Should()
            .Be(first.Preparation.Binding.InputRevisionUuid);
        var result = await processor.ResolveRecordingAsync(Guid.NewGuid(), first.AttemptUuid);
        result.Result!.Disposition.Should().Be(NormalizationPeopleResolutionDisposition.Stale);
        result.Preparation!.ExtractionObservation.PublishedWorkUuid.Should().Be(first.AttemptUuid);
        (await processor.ResolveRecordingAsync(Guid.NewGuid(), second.AttemptUuid))
            .Result!.Disposition.Should()
            .Be(NormalizationPeopleResolutionDisposition.Published);
    }

    [Test]
    public async Task CheckpointAndOriginalPreparationSurvivePublicationRollbackAndResume()
    {
        var source = await Source("Checkpoint " + Guid.NewGuid().ToString("N"));
        var extraction = await Extract(source);
        var attempt = await Stage(extraction);
        var ticket = await Admit(attempt);
        var batch = new NormalizationPeopleResolutionBatch(
            attempt.AttemptUuid,
            source.SourceUuid,
            extraction.AttemptUuid,
            attempt.ProcessingVersion,
            attempt.Preparation!,
            ticket
        );
        await store.SaveProposalsAsync(batch);
        var fail = () =>
            fixture.Work.TryPublishAsync(
                attempt.AttemptUuid,
                async (c, t, ct) =>
                {
                    await store.WritePublishedAsync(c, t, batch, ct);
                    throw new InvalidOperationException(
                        "synthetic resolution publication rollback"
                    );
                }
            );
        await fail.Should().ThrowAsync<InvalidOperationException>();
        (await store.GetAsync(source.SourceUuid)).Should().BeNull();
        (await store.GetAttemptBatchAsync(attempt.AttemptUuid)).Should().BeEquivalentTo(batch);
        (await processor.ResolveRecordingAsync(attempt.AttemptUuid, extraction.AttemptUuid))
            .Result!.Disposition.Should()
            .Be(NormalizationPeopleResolutionDisposition.Published);
    }

    [TestCase("omit_claim")]
    [TestCase("invent_match")]
    [TestCase("freshen_extraction")]
    public async Task PreparationCannotOmitClaimsInjectAutomaticSelectionOrRefreshItsExtractionStamp(
        string change
    )
    {
        var source = await Source("Original Claim " + Guid.NewGuid().ToString("N"));
        var extraction = await Extract(source);
        var original = await Preparation(extraction);
        var input = original.Inputs.Single();
        var invalid = change switch
        {
            "omit_claim" => original with { Inputs = [input with { Claims = [] }] },
            "invent_match" => original with
            {
                Inputs =
                [
                    input with
                    {
                        Claims =
                        [
                            input.Claims.Single() with
                            {
                                Decision = new(
                                    PeopleResolutionAction.MatchExisting,
                                    "forged_policy",
                                    Guid.NewGuid()
                                ),
                            },
                        ],
                    },
                ],
            },
            "freshen_extraction" => original with
            {
                ExtractionObservation = original.ExtractionObservation with
                {
                    PublishedWorkUuid = Guid.NewGuid(),
                },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        var attempt = await store.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            extraction.AttemptUuid,
            NormalizationPeopleResolutionProcessor.Version
        );
        var save = () => store.SavePreparationAsync(attempt.AttemptUuid, invalid);
        await save.Should().ThrowAsync<ArgumentException>();
        (await store.GetAttemptAsync(attempt.AttemptUuid))!.Preparation.Should().BeNull();
        await store.SavePreparationAsync(attempt.AttemptUuid, original);
    }

    [Test]
    public async Task TerminalReplayKeepsItsOriginalRequestAndCancelledPreparationRemainsRecoverable()
    {
        var source = await Source("Recovery " + Guid.NewGuid().ToString("N"));
        var extraction = await Extract(source);
        var attempt = await store.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            extraction.AttemptUuid,
            NormalizationPeopleResolutionProcessor.Version
        );
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = () =>
            processor.ResolveRecordingAsync(
                attempt.AttemptUuid,
                extraction.AttemptUuid,
                cancellation.Token
            );
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        (await store.GetAttemptAsync(attempt.AttemptUuid))!.Result.Should().BeNull();
        var result = await processor.ResolveRecordingAsync(
            attempt.AttemptUuid,
            extraction.AttemptUuid
        );
        result.Result!.Disposition.Should().Be(NormalizationPeopleResolutionDisposition.Published);
        var changed = () =>
            store.GetOrCreateAttemptAsync(
                result.AttemptUuid,
                extraction.AttemptUuid,
                "different-version"
            );
        await changed.Should().ThrowAsync<NormalizationConflictException>();
        var other = await Extract(source);
        var different = () =>
            processor.ResolveRecordingAsync(result.AttemptUuid, other.AttemptUuid);
        await different.Should().ThrowAsync<NormalizationConflictException>();
        (await store.GetAttemptBatchAsync(result.AttemptUuid))!
            .ExtractionAttemptUuid.Should()
            .Be(extraction.AttemptUuid);
    }

    [Test]
    public async Task DifferentOriginalSnapshotEpochsBecomeStaleInsteadOfPoisoningRecovery()
    {
        var name = "Snapshot " + Guid.NewGuid().ToString("N");
        var source = await Source(name);
        var extraction = await Extract(source);
        var preparation = await Preparation(extraction);
        await Person("Concurrent " + Guid.NewGuid().ToString("N"));
        preparation = preparation with
        {
            CreditContext = await people.ReadCreditContextAsync(source.SourceUuid),
        };
        var attempt = await store.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            extraction.AttemptUuid,
            NormalizationPeopleResolutionProcessor.Version
        );
        await store.SavePreparationAsync(attempt.AttemptUuid, preparation);
        var result = await processor.ResolveRecordingAsync(
            attempt.AttemptUuid,
            extraction.AttemptUuid
        );
        result.Result!.Disposition.Should().Be(NormalizationPeopleResolutionDisposition.Stale);
        (await processor.ResolveRecordingAsync(attempt.AttemptUuid, extraction.AttemptUuid))
            .Result!.Replayed.Should()
            .BeTrue();
        (await store.GetAsync(source.SourceUuid)).Should().BeNull();
    }

    [Test]
    public async Task UnknownBindingsUnderTheSameContributorAreNotReplaced()
    {
        var source = await Source("Ownership " + Guid.NewGuid().ToString("N"));
        var extraction = await Extract(source);
        await processor.ResolveRecordingAsync(Guid.NewGuid(), extraction.AttemptUuid);
        var previous = (await store.GetAsync(source.SourceUuid))!;
        var owner = NormalizationPeopleResolutionProcessor.Owner(source.SourceUuid);
        var membership = await fixture.Work.GetInputContributionsAsync(owner);
        var contributor = membership.Contributions.Single();
        var extra = await fixture.Capture("Unmanaged extra evidence", "people");
        await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            owner,
            NormalizationPeopleResolutionProcessor.Version,
            new(
                contributor.Contributor,
                contributor.Revision,
                [.. contributor.Resources, extra.Resource],
                contributor.DependencySubjectUuids
            ),
            membership.Owner!.DesiredGeneration
        );
        var before = await fixture.Work.GetInputContributionsAsync(owner);
        var result = await processor.ResolveRecordingAsync(Guid.NewGuid(), extraction.AttemptUuid);
        result.Result!.Disposition.Should().Be(NormalizationPeopleResolutionDisposition.Unresolved);
        (await fixture.Work.GetInputContributionsAsync(owner)).Should().BeEquivalentTo(before);
        (await store.GetAsync(source.SourceUuid)).Should().BeEquivalentTo(previous);
    }

    [Test]
    public async Task TruncatedPoolRetainsRivalsAndCannotBecomeAnAutomaticChoice()
    {
        var name = "Truncated " + Guid.NewGuid().ToString("N");
        await Person(name);
        await Person(name);
        var source = await Source(name);
        var extraction = await Extract(source);
        var preparation = await Preparation(extraction);
        var input = preparation.Inputs.Single();
        var proposal = input.Claims.Single().Proposal;
        var limited = await candidates.FindPeopleFactsAsync(
            new(IdentityMatchingDomain.People, [name], [], NameLimit: 1)
        );
        limited.Selection.NameCandidatesTruncated.Should().BeTrue();
        preparation = preparation with
        {
            Inputs =
            [
                input with
                {
                    Claims =
                    [
                        decisions.Compare(
                            proposal,
                            input.OriginalInput.Binding,
                            limited,
                            preparation.CreditContext
                        ),
                    ],
                },
            ],
        };
        var attempt = await store.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            extraction.AttemptUuid,
            NormalizationPeopleResolutionProcessor.Version
        );
        await store.SavePreparationAsync(attempt.AttemptUuid, preparation);
        var result = await processor.ResolveRecordingAsync(
            attempt.AttemptUuid,
            extraction.AttemptUuid
        );
        var claim = result.Preparation!.Inputs.Single().Claims.Single();
        claim.Candidates!.Complete.Should().BeFalse();
        claim.Decision.Action.Should().Be(PeopleResolutionAction.Unresolved);
        claim.Decision.AutomaticAcceptanceAuthorized.Should().BeFalse();
    }

    [Test]
    public async Task UnboundFileRetainsGlobalPersonComparisonsWithoutWideningCreditScope()
    {
        var name = "Unbound Reporter";
        var person = await Person(name);
        var source = await EmptySource();
        var key = "people-resolution-" + Guid.NewGuid().ToString("N");
        await fixture.Db.WithWriteConnection(async c =>
        {
            var upstream =
                await c.QuerySingleOrDefaultAsync<int?>(
                    "SELECT id FROM upstream_sources WHERE name='archive.org' ORDER BY id LIMIT 1"
                )
                ?? await c.QuerySingleAsync<int>(
                    "INSERT INTO upstream_sources(id,name,url) SELECT COALESCE(max(id),0)+1,'archive.org','https://archive.org' FROM upstream_sources RETURNING id"
                );
            await c.ExecuteAsync(
                """
                UPDATE sources SET upstream_identifier=@key WHERE id=@SourceId;
                UPDATE source_tracks SET mp3_url=@media WHERE uuid=@TrackUuid;
                INSERT INTO links(source_id,upstream_source_id,for_source,for_reviews,for_ratings,url,label)
                    VALUES(@SourceId,@upstream,true,false,false,@url,'Archive');
                """,
                new
                {
                    source.SourceId,
                    source.TrackUuid,
                    key,
                    upstream,
                    media = $"https://archive.org/download/{key}/file.mp3",
                    url = $"https://archive.org/details/{key}",
                }
            );
        });
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var captured = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                new("archive.org", "item_metadata", key),
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        metadata = new { identifier = key },
                        files = new object[]
                        {
                            new
                            {
                                name = "file.mp3",
                                title = "Song",
                                format = "VBR MP3",
                            },
                            new
                            {
                                name = "missing.flac",
                                title = "Song",
                                format = "Flac",
                                taper = name,
                            },
                        },
                    }
                ),
                "application/json",
                "archive-item-response-v1",
                0,
                DateTimeOffset.UtcNow
            )
        );
        var lookups = new NormalizationRecordingLookupStore(fixture.Db);
        var service = new NormalizationRecordingLookupService(
            lookups,
            new(fixture.Db, evidence),
            evidence,
            new(fixture.Db),
            new(),
            fixture.Work
        );
        var lookup = await service.LookupRetainedAsync(
            Guid.NewGuid(),
            new(captured.Revision.Uuid, captured.Revision.Resource)
        );
        var dispatch = await new NormalizationRecordingLookupDispatcher(
            lookups,
            extractor
        ).DispatchAsync(lookup.Result!.HandoffUuid!.Value);
        var result = await processor.ResolveRecordingAsync(
            Guid.NewGuid(),
            dispatch.ProcessingResult.AttemptUuid
        );
        var claim = result.Preparation!.Inputs.Single().Claims.Single();
        claim.Proposal.FileOrigin.Should().NotBeNull();
        claim.ScopeTrackUuid.Should().BeNull();
        claim.ScopeIssue.Should().NotBeNull();
        claim.Candidates!.Facts.Should().Contain(fact => fact.Hit.Uuid == person);
        claim.Comparisons.Should().NotBeEmpty();
        claim.Decision.Action.Should().Be(PeopleResolutionAction.Unresolved);
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().BeEmpty();
        var combined = await Extract(source);
        var later = await processor.ResolveRecordingAsync(Guid.NewGuid(), combined.AttemptUuid);
        later.Result!.Disposition.Should().Be(NormalizationPeopleResolutionDisposition.Published);
        later.Preparation!.Inputs.Should().HaveCount(2);
        later
            .Preparation.Inputs.SelectMany(input => input.Claims)
            .Should()
            .ContainSingle()
            .Which.Proposal.FileOrigin.Should()
            .Be(claim.Proposal.FileOrigin);
    }

    [Test]
    public async Task ConcurrentPreparationWinnerCannotBeTerminalizedByAnUnpreparedLoser()
    {
        var source = await Source("Winner " + Guid.NewGuid().ToString("N"));
        var extraction = await Extract(source);
        var preparation = await Preparation(extraction);
        var attempt = await store.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            extraction.AttemptUuid,
            NormalizationPeopleResolutionProcessor.Version
        );
        var saved = await Task.WhenAll(
            store.SavePreparationAsync(attempt.AttemptUuid, preparation),
            store.SavePreparationAsync(
                attempt.AttemptUuid,
                preparation with
                {
                    ExpectedConfigurationGeneration = 99,
                }
            )
        );
        saved[0].Should().BeEquivalentTo(saved[1]);
        var loser = await store.CompleteAsync(
            attempt,
            NormalizationPeopleResolutionDisposition.Stale,
            "discarded_loser"
        );
        loser.Result.Should().BeNull();
        loser.Preparation.Should().BeEquivalentTo(saved[0].Preparation);
        var terminal = await store.CompleteAsync(
            loser,
            NormalizationPeopleResolutionDisposition.Stale,
            "winning_preparation_stale"
        );
        (
            await store.CompleteAsync(
                loser,
                NormalizationPeopleResolutionDisposition.Unresolved,
                "later_outcome"
            )
        )
            .Should()
            .BeEquivalentTo(terminal);
    }

    [Test]
    public async Task CancelledCheckpointLeavesPreparationRecoverableAndUnpublished()
    {
        var source = await Source("Cancelled Checkpoint " + Guid.NewGuid().ToString("N"));
        var extraction = await Extract(source);
        var attempt = await Stage(extraction);
        var ticket = await Admit(attempt);
        var batch = new NormalizationPeopleResolutionBatch(
            attempt.AttemptUuid,
            source.SourceUuid,
            extraction.AttemptUuid,
            attempt.ProcessingVersion,
            attempt.Preparation!,
            ticket
        );
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = () => store.SaveProposalsAsync(batch, cancellation.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        (await store.GetAttemptBatchAsync(attempt.AttemptUuid)).Should().BeNull();
        (await processor.ResolveRecordingAsync(attempt.AttemptUuid, extraction.AttemptUuid))
            .Result!.Disposition.Should()
            .Be(NormalizationPeopleResolutionDisposition.Published);
    }

    [Test]
    public async Task ChangedProviderWordingFromAReviewedFieldRemainsReviewAndKeepsAcceptedCredit()
    {
        var name = "Reviewed Alice";
        var person = await Person(name);
        var source = await Source(name);
        var extraction = await Extract(source);
        var proposal = extraction.Extraction.Credits.Single();
        var personOwner = await fixture.OwnerUuid("people", "person", person);
        var ticket = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("people", "recording", source.SourceUuid),
            "reviewed-resolution-fixture",
            extraction.Preparation.Binding.Inputs.Select(input => input.Resource).ToArray(),
            [personOwner]
        );
        var credit = new RecordingCredit(
            Guid.NewGuid(),
            source.SourceUuid,
            "taper",
            proposal.Mention.Evidence.Quote!,
            person,
            null,
            [proposal.Mention.Evidence]
        );
        await people.PublishAsync(ticket.WorkUuid, new([], [], [], [credit]));
        var owner = (await fixture.Work.GetSubjectAsync(ticket.SubjectUuid))!;
        await people.ApplyDecisionAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                1,
                "accept",
                credit,
                "Confirm original recording credit"
            ),
            "fixture-operator"
        );
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync("UPDATE sources SET taper='Updated Bob' WHERE id=@SourceId", source)
        );
        var changed = await Extract(source);

        var result = await processor.ResolveRecordingAsync(Guid.NewGuid(), changed.AttemptUuid);

        result.Result!.Disposition.Should().Be(NormalizationPeopleResolutionDisposition.Published);
        result
            .Preparation!.Inputs.Single()
            .Claims.Single()
            .Decision.Action.Should()
            .Be(PeopleResolutionAction.Review);
        var current = await people.ReadCreditContextAsync(source.SourceUuid);
        current.Credits.Should().ContainSingle().Which.Credit.PersonUuid.Should().Be(person);
        current.Credits.Single().Credit.Basis.Should().Be("reviewed");
    }

    [Test]
    public async Task ReviewedNameDoesNotHoldADisjointNameInTheSameUnchangedField()
    {
        var person = await Person("Reviewed Alice");
        await Person("Independent Bob");
        var source = await Source("Reviewed Alice\nIndependent Bob");
        var extraction = await Extract(source);
        var alice = extraction.Extraction.Credits.Single(proposal =>
            proposal.Mention.Name == "Reviewed Alice"
        );
        var bob = extraction.Extraction.Credits.Single(proposal =>
            proposal.Mention.Name == "Independent Bob"
        );
        alice
            .Mention.Evidence.InputRevisionUuid.Should()
            .Be(bob.Mention.Evidence.InputRevisionUuid);
        alice
            .Mention.Evidence.EndCodePoint!.Value.Should()
            .BeLessThan(bob.Mention.Evidence.StartCodePoint!.Value);
        var personOwner = await fixture.OwnerUuid("people", "person", person);
        var ticket = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("people", "recording", source.SourceUuid),
            "reviewed-span-fixture",
            extraction.Preparation.Binding.Inputs.Select(input => input.Resource).ToArray(),
            [personOwner]
        );
        var credit = new RecordingCredit(
            Guid.NewGuid(),
            source.SourceUuid,
            "taper",
            alice.Mention.Evidence.Quote!,
            person,
            null,
            [alice.Mention.Evidence]
        );
        await people.PublishAsync(ticket.WorkUuid, new([], [], [], [credit]));
        var owner = (await fixture.Work.GetSubjectAsync(ticket.SubjectUuid))!;
        await people.ApplyDecisionAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                1,
                "accept",
                credit,
                "Confirm Alice only"
            ),
            "fixture-operator"
        );

        var result = await processor.ResolveRecordingAsync(Guid.NewGuid(), extraction.AttemptUuid);

        var claims = result.Preparation!.Inputs.Single().Claims;
        claims
            .Single(claim => claim.Proposal.Mention.Name == "Reviewed Alice")
            .Decision.Action.Should()
            .Be(PeopleResolutionAction.Review);
        var independent = claims.Single(claim => claim.Proposal.Mention.Name == "Independent Bob");
        independent.Decision.Reason.Should().NotBe("reviewed_credit_claim");
        independent
            .Decision.Should()
            .Be(
                decisions
                    .Compare(
                        independent.Proposal,
                        extraction.Preparation.Binding,
                        independent.Candidates
                    )
                    .Decision
            );
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().ContainSingle();
    }

    private async Task<NormalizationPeopleResolutionAttempt> Stage(
        NormalizationRecordingProposalBatch extraction
    )
    {
        var attempt = await store.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            extraction.AttemptUuid,
            NormalizationPeopleResolutionProcessor.Version
        );
        return await store.SavePreparationAsync(attempt.AttemptUuid, await Preparation(extraction));
    }

    // Compose public snapshot and preparation contracts directly so races occur after a known
    // original read, without timing sleeps or changing the production processor's control flow.
    private async Task<NormalizationPeopleResolutionPreparation> Preparation(
        NormalizationRecordingProposalBatch extraction
    )
    {
        var original = NormalizationRecordingReadSet.Inputs(extraction.Preparation).Single();
        var membership = await fixture.Work.GetInputContributionsAsync(
            NormalizationPeopleResolutionProcessor.Owner(extraction.SourceUuid)
        );
        var context = await people.ReadCreditContextAsync(extraction.SourceUuid);
        var claims = new List<NormalizationPeopleResolutionClaim>();
        foreach (var proposal in extraction.Extraction.Credits)
        {
            var facts = NormalizationPeopleResolutionDecisionService.CanQueryName(
                proposal.Mention.Name
            )
                ? await candidates.FindPeopleFactsAsync(
                    new(IdentityMatchingDomain.People, [proposal.Mention.Name], [])
                )
                : null;
            claims.Add(decisions.Compare(proposal, original.Binding, facts, context));
        }
        return new(
            new(
                extraction.Ticket.SubjectUuid,
                extraction.Ticket.DesiredGeneration,
                extraction.Ticket.EditorialRevision,
                extraction.AttemptUuid
            ),
            context,
            membership.Owner?.DesiredGeneration,
            membership
                .Contributions.Select(value => new NormalizationInputContributionRevision(
                    value.Contributor,
                    value.Revision
                ))
                .ToArray(),
            [
                new(
                    original,
                    membership
                        .Contributions.SingleOrDefault(value =>
                            value.Contributor == original.Contributor
                        )
                        ?.Revision
                        ?? 0,
                    claims,
                    extraction.Extraction.UnresolvedCredits,
                    extraction.Extraction.DecodeIssues
                ),
            ]
        );
    }

    private Task<NormalizationWorkTicket> Admit(NormalizationPeopleResolutionAttempt attempt) =>
        fixture.Work.BeginContributedBatchAsync(
            attempt.AttemptUuid,
            NormalizationPeopleResolutionProcessor.Owner(attempt.SourceUuid),
            attempt.ProcessingVersion,
            NormalizationPeopleResolutionReadSet.Contributions(attempt.Preparation!),
            attempt.Preparation!.ExpectedConfigurationGeneration,
            expectedContributionRevisions: attempt.Preparation.ExpectedContributions
        );

    private async Task<Guid> Person(string name)
    {
        var input = await fixture.Capture(name, "people");
        var uuid = Guid.NewGuid();
        await fixture.PublishPeople(people, input, new([new(uuid, name)], [], [], []));
        return uuid;
    }

    private async Task<CatalogSource> EmptySource()
    {
        var source = await fixture.Source();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET taper=NULL,transferrer=NULL,lineage=NULL,source=NULL,description=NULL,taper_notes=NULL WHERE id=@SourceId",
                source
            )
        );
        return source;
    }

    private async Task<CatalogSource> Source(string name)
    {
        var source = await EmptySource();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET taper=@name WHERE id=@SourceId",
                new { name, source.SourceId }
            )
        );
        return source;
    }

    private async Task<NormalizationRecordingProposalBatch> Extract(CatalogSource source)
    {
        var result = await extractor.ProcessAsync(Guid.NewGuid(), source.SourceUuid);
        result.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Published);
        return (await extractions.GetAttemptBatchAsync(result.AttemptUuid))!;
    }

    private Task<long> PeopleEpoch() =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT generation FROM normalization.candidate_universe_revisions WHERE domain='people' AND scope_uuid=@scope",
                new { scope = Guid.Empty }
            )
        );

    private Task<string> AcceptedState() =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                """
                SELECT jsonb_build_array((SELECT count(*) FROM normalization.people),(SELECT count(*) FROM normalization.recording_credits),
                    (SELECT string_agg(uuid::text||mp3_url,',' ORDER BY uuid) FROM source_tracks))::text
                """
            )
        );
}
