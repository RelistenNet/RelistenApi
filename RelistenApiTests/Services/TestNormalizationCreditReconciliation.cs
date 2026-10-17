using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

/// <summary>Accepted credit replacement consumes real published extraction and preserves independent support.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationCreditReconciliation
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizedPeopleService people = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationRecordingProposalService proposals = null!;
    private NormalizationRecordingProcessor processor = null!;
    private NormalizationRecordingLookupService lookups = null!;
    private NormalizationRecordingLookupDispatcher dispatcher = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        people = new(fixture.Db, fixture.Work);
        evidence = new(fixture.Db);
        proposals = new(fixture.Db);
        var legacy = new NormalizationLegacyInputService(fixture.Db, evidence);
        var bindings = new NormalizationCatalogBindingService(fixture.Db);
        var lookupStore = new NormalizationRecordingLookupStore(fixture.Db);
        processor = new(
            legacy,
            evidence,
            bindings,
            new(),
            new(new(), new()),
            fixture.Work,
            proposals,
            lookupStore
        );
        lookups = new(lookupStore, legacy, evidence, bindings, new(), fixture.Work);
        dispatcher = new(lookupStore, processor);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task CorrectionReplacesOnlyManagedClaimsAndUnchangedRunsConverge()
    {
        var source = await Source("Alice Recorder");
        var alice = await Person("Alice Recorder");
        var bob = await Person("Bob Transfer");
        var first = await Extract(source);
        var firstRequest = Request(
            first,
            new Dictionary<string, Guid> { { "Alice Recorder", alice } }
        );
        await Reconcile(first, firstRequest);
        var original = (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Single();
        var repeat = await Reconcile(
            first,
            Request(first, new Dictionary<string, Guid> { { "Alice Recorder", alice } })
        );
        (await people.ReadCreditContextAsync(source.SourceUuid))
            .Credits.Single()
            .Should()
            .BeEquivalentTo(original);
        (await SupportCount(source.SourceUuid)).Should().Be(1);
        var replay = await people.ReconcileRecordingCreditsAsync(repeat.WorkUuid, firstRequest);
        replay.Replayed.Should().BeTrue();

        await SetTaper(source, "Bob Transfer");
        var changed = await Extract(source);
        await Reconcile(
            changed,
            Request(changed, new Dictionary<string, Guid> { { "Bob Transfer", bob } })
        );
        var context = await people.ReadCreditContextAsync(source.SourceUuid);
        context
            .Credits.Should()
            .Contain(credit =>
                credit.Credit.PersonUuid == alice && credit.Credit.State == "superseded"
            );
        context
            .Credits.Should()
            .Contain(credit =>
                credit.Credit.PersonUuid == bob && credit.Credit.State == "accepted"
            );
        await SetTaper(source, null);
        var empty = await Extract(source);
        await Reconcile(empty, Request(empty, new Dictionary<string, Guid>()));
        (await people.ReadCreditContextAsync(source.SourceUuid))
            .Credits.Should()
            .OnlyContain(credit => credit.Credit.State == "superseded");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SharedContributorsWithdrawIndependentlyAndAppendSupportSurvives(
        bool appendBaseline
    )
    {
        var source = await Source("Alice Recorder");
        var alice = await Person("Alice Recorder");
        await Extract(source);
        var resource = await ArchiveResource(source);
        var shared = await ExtractArchive(source, resource, "Alice Recorder");
        var names = new Dictionary<string, Guid> { { "Alice Recorder", alice } };
        var request = Request(shared, names);
        if (appendBaseline)
        {
            var credit = request.Contributors.First().Credits.Single();
            var append = await Begin(shared, [credit]);
            (await people.PublishAsync(append.WorkUuid, new([], [], [], [credit])))
                .Status.Should()
                .Be(NormalizationPublicationStatus.Published);
        }
        await Reconcile(shared, request);
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().ContainSingle();
        await SetTaper(source, null);
        var onlyProvider = await Extract(source);
        await Reconcile(onlyProvider, Request(onlyProvider, names));
        (await people.ReadCreditContextAsync(source.SourceUuid))
            .Credits.Single()
            .Credit.State.Should()
            .Be("accepted");
        var empty = await ExtractArchive(source, resource, null);
        await Reconcile(empty, Request(empty, new Dictionary<string, Guid>()));
        (await people.ReadCreditContextAsync(source.SourceUuid))
            .Credits.Single()
            .Credit.State.Should()
            .Be(appendBaseline ? "accepted" : "superseded");
    }

    [TestCase("accept")]
    [TestCase("reject")]
    public async Task ReviewedFieldSurvivesChangedWordingAndKeepsNewClaimPending(string action)
    {
        var source = await Source("Alice Recorder");
        var alice = await Person("Alice Recorder");
        var bob = await Person("Bob Transfer");
        var batch = await Extract(source);
        await Reconcile(
            batch,
            Request(batch, new Dictionary<string, Guid> { { "Alice Recorder", alice } })
        );
        var initial = (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Single();
        var owner = (await fixture.Work.GetSubjectAsync(Owner(source.SourceUuid)))!;
        await people.ApplyDecisionAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                initial.Revision,
                action,
                initial.Credit,
                "Reviewed original credit"
            ),
            "fixture"
        );
        var reviewed = (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Single();
        await SetTaper(source, "Bob Transfer");
        var changed = await Extract(source);
        await Reconcile(
            changed,
            Request(changed, new Dictionary<string, Guid> { { "Bob Transfer", bob } })
        );
        var context = await people.ReadCreditContextAsync(source.SourceUuid);
        context.Credits.Should().ContainEquivalentOf(reviewed);
        context
            .Credits.Should()
            .Contain(credit => credit.Credit.PersonUuid == bob && credit.Credit.State == "pending");
        context
            .ReviewedExclusions.Should()
            .Contain(exclusion => exclusion.Claim.Credit.PersonUuid == alice);
        await SetTaper(source, null);
        var empty = await Extract(source);
        await Reconcile(empty, Request(empty, new Dictionary<string, Guid>()));
        (await people.ReadCreditContextAsync(source.SourceUuid))
            .Credits.Should()
            .ContainEquivalentOf(reviewed);
    }

    [Test]
    public async Task UnresolvedProseCannotAuthorizeWithdrawal()
    {
        var source = await Source("Alice Recorder");
        var alice = await Person("Alice Recorder");
        var first = await Extract(source);
        await Reconcile(
            first,
            Request(first, new Dictionary<string, Guid> { { "Alice Recorder", alice } })
        );
        await SetTaper(source, "unknown");
        var unresolved = await Extract(source);
        unresolved.Extraction.UnresolvedCredits.Should().NotBeEmpty();
        var request = Request(unresolved, new Dictionary<string, Guid>());
        var ticket = await Begin(unresolved, []);
        var publish = () => people.ReconcileRecordingCreditsAsync(ticket.WorkUuid, request);
        await publish.Should().ThrowAsync<ArgumentException>();
        (await people.ReadCreditContextAsync(source.SourceUuid))
            .Credits.Single()
            .Credit.State.Should()
            .Be("accepted");
        (await SupportCount(source.SourceUuid)).Should().Be(1);
    }

    [Test]
    public async Task OneWordingCannotCoverTwoExtractedNamesByCombiningTheirCitations()
    {
        var source = await Source("Alice Recorder; Bob Transfer");
        var alice = await Person("Alice Recorder");
        var batch = await Extract(source);
        batch.Extraction.Credits.Should().HaveCount(2);
        var input = NormalizationRecordingReadSet.Inputs(batch.Preparation).Single();
        var mentions = batch.Extraction.Credits;
        var credit = new RecordingCredit(
            Guid.NewGuid(),
            source.SourceUuid,
            "taper",
            mentions[0].Mention.Name,
            alice,
            null,
            mentions.Select(mention => mention.Mention.Evidence).ToArray()
        );
        var request = new RecordingCreditReconciliation(
            source.SourceUuid,
            batch.AttemptUuid,
            [new(input.Contributor, [new("taper")], [credit])]
        );
        var ticket = await Begin(batch, [credit]);
        await FluentActions
            .Awaiting(() => people.ReconcileRecordingCreditsAsync(ticket.WorkUuid, request))
            .Should()
            .ThrowAsync<ArgumentException>();
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().BeEmpty();
    }

    [Test]
    public async Task ReconciliationFreezesCallerListsAndRejectsInventedOriginalWording()
    {
        var source = await Source("Alice Recorder");
        var alice = await Person("Alice Recorder");
        var batch = await Extract(source);
        var valid = Request(batch, new Dictionary<string, Guid> { { "Alice Recorder", alice } });
        var invented = valid with
        {
            Contributors =
            [
                valid.Contributors.Single() with
                {
                    Credits =
                    [
                        valid.Contributors.Single().Credits.Single() with
                        {
                            OriginalText = "Mallory",
                        },
                    ],
                },
            ],
        };
        var rejected = await Begin(batch, valid.Contributors.Single().Credits);
        await FluentActions
            .Awaiting(() => people.ReconcileRecordingCreditsAsync(rejected.WorkUuid, invented))
            .Should()
            .ThrowAsync<ArgumentException>();
        var mutable = valid.Contributors.Single().Credits.ToList();
        var request = valid with
        {
            Contributors = [valid.Contributors.Single() with { Credits = mutable }],
        };
        var ticket = await Begin(batch, mutable);
        var publication = people.ReconcileRecordingCreditsAsync(ticket.WorkUuid, request);
        mutable.Clear();
        (await publication).Status.Should().Be(NormalizationPublicationStatus.Published);
        (await people.ReadCreditContextAsync(source.SourceUuid))
            .Credits.Single()
            .Credit.PersonUuid.Should()
            .Be(alice);
    }

    [Test]
    public async Task AppendCannotRewriteAnotherContributorsManagedClaim()
    {
        var source = await Source("Alice Recorder");
        var alice = await Person("Alice Recorder");
        var bob = await Person("Bob Recorder");
        var batch = await Extract(source);
        var original = Request(batch, new Dictionary<string, Guid> { { "Alice Recorder", alice } });
        await Reconcile(batch, original);
        var before = (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Single();
        var independent = original.Contributors.Single().Credits.Single() with
        {
            Uuid = before.Credit.Uuid,
            PersonUuid = bob,
        };
        var append = await Begin(batch, [independent]);
        (await people.PublishAsync(append.WorkUuid, new([], [], [], [independent])))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        var context = await people.ReadCreditContextAsync(source.SourceUuid);
        context.Credits.Should().ContainEquivalentOf(before);
        context
            .Credits.Should()
            .Contain(credit =>
                credit.Credit.PersonUuid == bob && credit.Credit.State == "accepted"
            );
        await SetTaper(source, null);
        var empty = await Extract(source);
        await Reconcile(empty, Request(empty, new Dictionary<string, Guid>()));
        var after = await people.ReadCreditContextAsync(source.SourceUuid);
        after
            .Credits.Should()
            .Contain(credit =>
                credit.Credit.PersonUuid == alice && credit.Credit.State == "superseded"
            );
        after
            .Credits.Should()
            .Contain(credit =>
                credit.Credit.PersonUuid == bob && credit.Credit.State == "accepted"
            );
    }

    [Test]
    public async Task OneMentionCannotAuthorizeTwoAcceptedPeople()
    {
        var source = await Source("Alice Recorder");
        var alice = await Person("Alice Recorder");
        var namesake = await Person("Other Alice");
        var batch = await Extract(source);
        var valid = Request(batch, new Dictionary<string, Guid> { { "Alice Recorder", alice } });
        var credit = valid.Contributors.Single().Credits.Single();
        var request = valid with
        {
            Contributors =
            [
                valid.Contributors.Single() with
                {
                    Credits =
                    [
                        credit,
                        credit with
                        {
                            Uuid = Guid.NewGuid(),
                            PersonUuid = namesake,
                        },
                    ],
                },
            ],
        };
        var ticket = await Begin(batch, request.Contributors.Single().Credits);
        await FluentActions
            .Awaiting(() => people.ReconcileRecordingCreditsAsync(ticket.WorkUuid, request))
            .Should()
            .ThrowAsync<ArgumentException>();
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().BeEmpty();
    }

    [Test]
    public async Task ReviewedNameDoesNotHoldADifferentMentionInTheSameOriginalField()
    {
        var source = await Source("Alice Recorder; Bob Transfer");
        var alice = await Person("Alice Recorder");
        var bob = await Person("Bob Transfer");
        var batch = await Extract(source);
        var names = new Dictionary<string, Guid>
        {
            { "Alice Recorder", alice },
            { "Bob Transfer", bob },
        };
        var all = Request(batch, names);
        var first = all with
        {
            Contributors =
            [
                all.Contributors.Single() with
                {
                    Credits = all
                        .Contributors.Single()
                        .Credits.Select(credit =>
                            credit.PersonUuid == bob ? credit with { State = "pending" } : credit
                        )
                        .ToArray(),
                },
            ],
        };
        await Reconcile(batch, first);
        var claim = (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Single(claim =>
            claim.Credit.PersonUuid == alice
        );
        var owner = (await fixture.Work.GetSubjectAsync(Owner(source.SourceUuid)))!;
        await people.ApplyDecisionAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                claim.Revision,
                "accept",
                claim.Credit,
                "Reviewed one independently cited name"
            ),
            "fixture"
        );
        await Reconcile(batch, all);
        (await people.ReadCreditContextAsync(source.SourceUuid))
            .Credits.Should()
            .Contain(credit =>
                credit.Credit.PersonUuid == bob && credit.Credit.State == "accepted"
            );
    }

    [Test]
    public async Task CreditCannotBorrowAnUnrelatedCitationFromTheSameInput()
    {
        var source = await Source("Alice Recorder");
        var alice = await Person("Alice Recorder");
        var batch = await Extract(source);
        var request = Request(batch, new Dictionary<string, Guid> { { "Alice Recorder", alice } });
        var original = request.Contributors.Single();
        var credit = original.Credits.Single();
        var title = batch.Extraction.Titles.Single().Title!.Evidence;
        title.InputRevisionUuid.Should().Be(credit.Evidence.Single().InputRevisionUuid);
        var tampered = request with
        {
            Contributors =
            [
                original with
                {
                    Credits = [credit with { Evidence = [.. credit.Evidence, title] }],
                },
            ],
        };
        var ticket = await Begin(batch, [credit]);
        await FluentActions
            .Awaiting(() => people.ReconcileRecordingCreditsAsync(ticket.WorkUuid, tampered))
            .Should()
            .ThrowAsync<ArgumentException>();
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().BeEmpty();
    }

    [Test]
    public async Task CatalogChangeAfterAdmissionCannotRetireOrPublishClaims()
    {
        var source = await Source("Alice Recorder");
        var alice = await Person("Alice Recorder");
        var batch = await Extract(source);
        var request = Request(batch, new Dictionary<string, Guid> { { "Alice Recorder", alice } });
        var ticket = await Begin(
            batch,
            request.Contributors.SelectMany(input => input.Credits).ToArray()
        );
        await SetTaper(source, "Bob Transfer");
        (await people.ReconcileRecordingCreditsAsync(ticket.WorkUuid, request))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().BeEmpty();
        (await SupportCount(source.SourceUuid)).Should().Be(0);
    }

    [Test]
    public async Task FailureAfterFirstContributorWriteRollsBackTheWholeReplacement()
    {
        var source = await Source("Alice Recorder");
        var alice = await Person("Alice Recorder");
        await Extract(source);
        var resource = await ArchiveResource(source);
        var batch = await ExtractArchive(source, resource, "Alice Recorder");
        var valid = Request(batch, new Dictionary<string, Guid> { { "Alice Recorder", alice } });
        var request = valid with
        {
            Contributors =
            [
                valid.Contributors[0],
                valid.Contributors[1] with
                {
                    CompleteScopes = [new("taper", Guid.NewGuid())],
                },
            ],
        };
        var ticket = await Begin(
            batch,
            valid.Contributors.SelectMany(input => input.Credits).ToArray()
        );
        await FluentActions
            .Awaiting(() => people.ReconcileRecordingCreditsAsync(ticket.WorkUuid, request))
            .Should()
            .ThrowAsync<ArgumentException>();
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().BeEmpty();
        (await SupportCount(source.SourceUuid)).Should().Be(0);
        (await people.ReconcileRecordingCreditsAsync(ticket.WorkUuid, valid))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
    }

    [Test]
    public async Task AbsentRecordingContextIsFencedAgainstTheFirstReview()
    {
        var source = await Source("Alice Recorder");
        var alice = await Person("Alice Recorder");
        var context = await people.ReadCreditContextAsync(source.SourceUuid);
        context.RecordingOwnerObservation.Should().BeNull();
        context.Credits.Should().BeEmpty();
        var consumer = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("people", "recording_resolution", source.SourceUuid),
            "context-test",
            [],
            [],
            candidateUniverses: [context.PeopleUniverse]
        );
        var batch = await Extract(source);
        var request = Request(batch, new Dictionary<string, Guid> { { "Alice Recorder", alice } });
        await Reconcile(batch, request);
        var claim = (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Single();
        var owner = (await fixture.Work.GetSubjectAsync(Owner(source.SourceUuid)))!;
        await people.ApplyDecisionAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                claim.Revision,
                "reject",
                claim.Credit,
                "First recording review"
            ),
            "fixture"
        );
        (await fixture.Work.TryPublishAsync(consumer.WorkUuid, (_, _, _) => Task.CompletedTask))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
        var after = await people.ReadCreditContextAsync(source.SourceUuid);
        after.RecordingOwnerObservation.Should().NotBeNull();
        after
            .ReviewedExclusions.Should()
            .Contain(exclusion =>
                exclusion.Claim.Credit.Evidence.SequenceEqual(claim.Credit.Evidence)
            );
    }

    [Test]
    public async Task CurrentExtractionCanWithdrawACompleteBoundFileScope()
    {
        var source = await Source(null);
        var alice = await Person("Alice Recorder");
        var resource = await ArchiveResource(source);
        var first = await ExtractArchive(
            source,
            resource,
            null,
            new
            {
                name = "file.mp3",
                format = "VBR MP3",
                taper = "Alice Recorder",
            }
        );
        var request = FileRequest(
            first,
            new Dictionary<string, Guid> { { "Alice Recorder", alice } },
            source.TrackUuid
        );
        await Reconcile(first, request);
        var before = (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Single();
        before.Credit.ScopeTrackUuid.Should().Be(source.TrackUuid);
        before.Credit.State.Should().Be("accepted");
        var empty = await ExtractArchive(
            source,
            resource,
            null,
            new { name = "file.mp3", format = "VBR MP3" }
        );
        await Reconcile(
            empty,
            FileRequest(empty, new Dictionary<string, Guid>(), source.TrackUuid)
        );
        var after = (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Single();
        after.Credit.Uuid.Should().Be(before.Credit.Uuid);
        after.Credit.State.Should().Be("superseded");
    }

    [Test]
    public async Task UnboundFileClaimCannotWithdrawOldTrackCreditOrBecomeRecordingWide()
    {
        var source = await Source(null);
        var alice = await Person("Alice Recorder");
        var bob = await Person("Bob Transfer");
        var resource = await ArchiveResource(source);
        var first = await ExtractArchive(
            source,
            resource,
            null,
            new
            {
                name = "file.mp3",
                format = "VBR MP3",
                taper = "Alice Recorder",
            }
        );
        await Reconcile(
            first,
            FileRequest(
                first,
                new Dictionary<string, Guid> { { "Alice Recorder", alice } },
                source.TrackUuid
            )
        );
        var changed = await ExtractArchive(
            source,
            resource,
            null,
            new { name = "file.mp3", format = "VBR MP3" },
            new
            {
                name = "unknown.flac",
                format = "Flac",
                taper = "Bob Transfer",
            }
        );
        var input = NormalizationRecordingReadSet
            .Inputs(changed.Preparation)
            .Single(input => input.Contributor == resource);
        var group =
            changed.Preparation.Binding.Resource == resource
                ? changed.Extraction
                : changed
                    .AdditionalExtractions!.Single(group =>
                        group.InputRevisionUuid == input.Binding.InputRevisionUuid
                    )
                    .Extraction;
        var mention = group.Credits.Single();
        input
            .Binding.Files.Single(file => file.Observation.Origin == mention.FileOrigin)
            .Binding.Value.Should()
            .BeNull();
        var withdraw = new RecordingCreditReconciliation(
            source.SourceUuid,
            changed.AttemptUuid,
            [new(resource, [new("taper", source.TrackUuid)], [])]
        );
        var ticket = await Begin(changed, []);
        await FluentActions
            .Awaiting(() => people.ReconcileRecordingCreditsAsync(ticket.WorkUuid, withdraw))
            .Should()
            .ThrowAsync<ArgumentException>();
        var widened = new RecordingCredit(
            Guid.NewGuid(),
            source.SourceUuid,
            "taper",
            mention.Mention.Evidence.Quote ?? mention.Mention.Name,
            bob,
            null,
            [mention.Mention.Evidence]
        );
        var request = withdraw with { Contributors = [new(resource, [new("taper")], [widened])] };
        var second = await Begin(changed, [widened]);
        await FluentActions
            .Awaiting(() => people.ReconcileRecordingCreditsAsync(second.WorkUuid, request))
            .Should()
            .ThrowAsync<ArgumentException>();
        (await people.ReadCreditContextAsync(source.SourceUuid))
            .Credits.Single()
            .Credit.State.Should()
            .Be("accepted");
    }

    [Test]
    public async Task HistoricalPublishedExtractionCannotAuthorizeCompleteWithdrawal()
    {
        var source = await Source("Alice Recorder");
        var alice = await Person("Alice Recorder");
        var current = await Extract(source);
        var membership = await fixture.Work.GetInputContributionsAsync(
            NormalizationRecordingProcessor.Owner(source.SourceUuid)
        );
        var contributor = NormalizationRecordingProcessor.Contributor(source.SourceUuid);
        var revision = membership
            .Contributions.Single(state => state.Contributor == contributor)
            .Revision;
        const string oldVersion = "catalog-recording-processing-v2/historical-file-coverage";
        var attemptUuid = Guid.NewGuid();
        var preparation = current.Preparation with
        {
            ExpectedConfigurationGeneration = membership.Owner!.DesiredGeneration,
            ExpectedContributionRevision = revision,
            ExpectedContributions = membership
                .Contributions.Select(state => new NormalizationInputContributionRevision(
                    state.Contributor,
                    state.Revision
                ))
                .ToArray(),
        };
        await proposals.GetOrCreateAttemptAsync(attemptUuid, source.SourceUuid, oldVersion);
        await proposals.SavePreparationAsync(attemptUuid, preparation);
        var binding = preparation.Binding;
        var ticket = await fixture.Work.BeginContributedBatchAsync(
            attemptUuid,
            NormalizationRecordingProcessor.Owner(source.SourceUuid),
            oldVersion,
            [
                new(
                    new(
                        contributor,
                        revision,
                        binding.Inputs.Select(input => input.Resource).ToArray(),
                        []
                    ),
                    binding.Inputs,
                    binding.CatalogInputs,
                    binding.SourceIdentities,
                    binding.MappingRegistry,
                    SourceUuids: binding.SourceUuids
                ),
            ],
            membership.Owner.DesiredGeneration,
            expectedContributionRevisions: preparation.ExpectedContributions
        );
        var historical = current with
        {
            AttemptUuid = attemptUuid,
            ProcessingVersion = oldVersion,
            Preparation = preparation,
            Ticket = ticket,
        };
        await proposals.SaveExtractionAsync(historical);
        (
            await fixture.Work.TryPublishAsync(
                attemptUuid,
                (c, t, ct) => proposals.WritePublishedAsync(c, t, historical, ct)
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        var request = Request(
            historical,
            new Dictionary<string, Guid> { { "Alice Recorder", alice } }
        );
        var accepted = await Begin(historical, request.Contributors.Single().Credits);
        await FluentActions
            .Awaiting(() => people.ReconcileRecordingCreditsAsync(accepted.WorkUuid, request))
            .Should()
            .ThrowAsync<ArgumentException>();
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().BeEmpty();
    }

    private static RecordingCreditReconciliation FileRequest(
        NormalizationRecordingProposalBatch batch,
        IReadOnlyDictionary<string, Guid> names,
        Guid trackUuid
    )
    {
        var request = Request(batch, names);
        return request with
        {
            Contributors =
            [
                request.Contributors.Single(input =>
                    input.Contributor.Provider == "archive.org"
                ) with
                {
                    CompleteScopes = [new("taper", trackUuid)],
                },
            ],
        };
    }

    private async Task<Guid> Person(string name)
    {
        var input = await fixture.Capture(name, "people");
        var uuid = Guid.NewGuid();
        await fixture.PublishPeople(people, input, new([new(uuid, name)], [], [], []));
        return uuid;
    }

    private async Task<CatalogSource> Source(string? name)
    {
        var source = await fixture.Source();
        await fixture.Db.WithWriteConnection(async c =>
        {
            var show = await c.QuerySingleAsync<long>(
                "INSERT INTO shows(artist_id,date,display_date,uuid,updated_at) VALUES(@ArtistId,'2026-10-17','2026-10-17',@uuid,now()) RETURNING id",
                new { source.ArtistId, uuid = Guid.NewGuid() }
            );
            await c.ExecuteAsync(
                "UPDATE sources SET show_id=@show,taper=@name,transferrer=NULL,lineage=NULL,source=NULL,description=NULL,taper_notes=NULL WHERE id=@SourceId",
                new
                {
                    source.SourceId,
                    show,
                    name,
                }
            );
        });
        return source;
    }

    private Task SetTaper(CatalogSource source, string? name) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET taper=@name WHERE id=@SourceId",
                new { source.SourceId, name }
            )
        );

    private async Task<NormalizationRecordingProposalBatch> Extract(CatalogSource source)
    {
        (await processor.ProcessAsync(Guid.NewGuid(), source.SourceUuid))
            .Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);
        return (await proposals.GetAsync(source.SourceUuid))!;
    }

    private async Task<NormalizationResourceKey> ArchiveResource(CatalogSource source)
    {
        var key = "credit-support-" + Guid.NewGuid().ToString("N");
        await fixture.Db.WithWriteConnection(async c =>
        {
            var upstream = await c.QuerySingleOrDefaultAsync<int?>(
                "SELECT id FROM upstream_sources WHERE name='archive.org' ORDER BY id LIMIT 1"
            );
            upstream ??= await c.QuerySingleAsync<int>(
                "INSERT INTO upstream_sources(id,name,url) SELECT COALESCE(max(id),0)+1,'archive.org','https://archive.org' FROM upstream_sources RETURNING id"
            );
            await c.ExecuteAsync(
                "UPDATE sources SET upstream_identifier=@key WHERE id=@SourceId; UPDATE source_tracks SET mp3_url=@media WHERE uuid=@TrackUuid; INSERT INTO links(source_id,upstream_source_id,for_source,for_reviews,for_ratings,url,label) VALUES(@SourceId,@upstream,true,false,false,@url,'Archive')",
                new
                {
                    source.SourceId,
                    source.TrackUuid,
                    media = "https://archive.org/download/" + key + "/file.mp3",
                    key,
                    upstream,
                    url = "https://archive.org/details/" + key,
                }
            );
        });
        // Capture this changed ownership context before preparing the provider peer.
        await Extract(source);
        return new("archive.org", "item_metadata", key);
    }

    private async Task<NormalizationRecordingProposalBatch> ExtractArchive(
        CatalogSource source,
        NormalizationResourceKey resource,
        string? name,
        params object[] files
    )
    {
        var current = await evidence.GetCurrentInputAsync(resource);
        var captured = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        metadata = new { identifier = resource.ProviderKey, taper = name },
                        files,
                    }
                ),
                "application/json",
                "archive-item-response-v1",
                current?.Generation ?? 0,
                DateTimeOffset.UtcNow
            )
        );
        var lookup = await lookups.LookupRetainedAsync(
            Guid.NewGuid(),
            new(captured.Revision.Uuid, resource)
        );
        (await dispatcher.DispatchAsync(lookup.Result!.HandoffUuid!.Value))
            .ProcessingResult.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);
        return (await proposals.GetAsync(source.SourceUuid))!;
    }

    private static RecordingCreditReconciliation Request(
        NormalizationRecordingProposalBatch batch,
        IReadOnlyDictionary<string, Guid> names
    )
    {
        var groups = new[]
        {
            new NormalizationRecordingInputExtraction(
                batch.Preparation.Binding.InputRevisionUuid,
                batch.Extraction
            ),
        }
            .Concat(batch.AdditionalExtractions ?? [])
            .ToDictionary(group => group.InputRevisionUuid);
        return new(
            batch.SourceUuid,
            batch.AttemptUuid,
            NormalizationRecordingReadSet
                .Inputs(batch.Preparation)
                .Select(input => new RecordingCreditContributorReplacement(
                    input.Contributor,
                    [new("taper")],
                    groups[input.Binding.InputRevisionUuid]
                        .Extraction.Credits.Where(proposal =>
                            proposal.Mention.Role == RecordingCreditRole.Taper
                        )
                        .Select(proposal => new RecordingCredit(
                            Guid.NewGuid(),
                            batch.SourceUuid,
                            "taper",
                            proposal.Mention.Name,
                            names[proposal.Mention.Name],
                            proposal.FileOrigin == null
                                ? null
                                : input
                                    .Binding.Files.Single(file =>
                                        file.Observation.Origin == proposal.FileOrigin
                                    )
                                    .Binding.Value?.TrackUuid,
                            [proposal.Mention.Evidence]
                        ))
                        .ToArray()
                ))
                .ToArray()
        );
    }

    private async Task<NormalizationWorkTicket> Begin(
        NormalizationRecordingProposalBatch batch,
        IReadOnlyList<RecordingCredit> credits
    )
    {
        var owner = await fixture.Work.GetSubjectAsync(Owner(batch.SourceUuid));
        var dependencies = new List<Guid> { batch.Ticket.SubjectUuid };
        foreach (
            var person in credits.Select(credit => credit.PersonUuid).OfType<Guid>().Distinct()
        )
        {
            dependencies.Add(await fixture.OwnerUuid("people", "person", person));
        }
        return await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            Owner(batch.SourceUuid),
            "credit-replacement-test",
            NormalizationRecordingReadSet
                .Inputs(batch.Preparation)
                .SelectMany(input => input.Binding.Inputs)
                .Select(input => input.Resource)
                .Distinct()
                .ToArray(),
            dependencies,
            owner?.DesiredGeneration,
            catalogInputs: batch.Ticket.CatalogInputs,
            sourceIdentities: batch.Ticket.SourceIdentities,
            mappingRegistry: batch.Ticket.MappingRegistry,
            expectedInputRevisions: NormalizationRecordingReadSet
                .Inputs(batch.Preparation)
                .SelectMany(input => input.Binding.Inputs)
                .Distinct()
                .ToArray(),
            sourceUuids: batch.Ticket.SourceUuids
        );
    }

    private async Task<NormalizationWorkTicket> Reconcile(
        NormalizationRecordingProposalBatch batch,
        RecordingCreditReconciliation request
    )
    {
        var ticket = await Begin(
            batch,
            request.Contributors.SelectMany(input => input.Credits).ToArray()
        );
        (await people.ReconcileRecordingCreditsAsync(ticket.WorkUuid, request))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        return ticket;
    }

    private Task<long> SupportCount(Guid sourceUuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT count(*) FROM normalization.recording_credit_contributor_support support JOIN normalization.recording_credits credit ON credit.uuid=support.credit_uuid WHERE credit.source_uuid=@sourceUuid AND retired_work_uuid IS NULL",
                new { sourceUuid }
            )
        );

    private static NormalizationSubjectKey Owner(Guid sourceUuid) =>
        new("people", "recording", sourceUuid);
}
