using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using DomainInput = RelistenApiTests.Services.NormalizationDomainFixture.DomainInput;

namespace RelistenApiTests.Services;

/// <summary>
/// These disposable policies demonstrate the authenticated correspondence path. They are not
/// evaluated production rules, and no test depends on a live MusicBrainz response.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationCompositionResolution
{
    private const string Operator = "composition-fixture-operator";
    private NormalizationDomainFixture fixture = null!;
    private NormalizedMusicService music = null!;
    private CompositionResolutionPolicyService policies = null!;
    private readonly List<HttpClient> clients = [];

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        music = new(fixture.Db, fixture.Work);
        policies = new(fixture.Db, fixture.Work, allowFixturePolicies: true);
    }

    [TearDown]
    public async Task Reset()
    {
        if (await policies.ReadActiveAsync() != null)
        {
            await SetActivePolicy(null);
        }
        foreach (var client in clients)
        {
            client.Dispose();
        }
        clients.Clear();
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task CorroboratedRecordingCreatesAndLinksAWorkWithoutLocalWorkIdOrCompleteAuthors()
    {
        var source = await Claim();
        await ActivatePolicy();
        var upstream = new Upstream(source);
        var processor = Processor(upstream);
        var attemptUuid = Guid.NewGuid();

        var resolved = await Finish(processor, source.Reference, attemptUuid, upstream);

        resolved
            .Result!.Disposition.Should()
            .Be(CompositionResolutionDisposition.Applied, Audit(resolved));
        resolved.Proposal.Should().NotBeNull();
        resolved.Preparation!.Context.Repertoire.ExternalIdentities.Should().BeEmpty();
        resolved
            .Stages.Select(stage => stage.Kind)
            .Should()
            .Equal(
                CompositionResolutionStageKind.CreateComposition,
                CompositionResolutionStageKind.Link
            );
        resolved.Stages.Should().OnlyContain(stage => stage.Receipt != null);
        var hydratedWork = resolved
            .Proposal!.Facts.Candidates.Single(candidate =>
                candidate.ExternalWorkMbid == source.WorkMbid
            )
            .Work;
        var createdComposition = (
            await music.GetCompositionAsync(resolved.Proposal.CompositionUuid!.Value)
        )!;
        createdComposition
            .Value.Evidence.Should()
            .ContainSingle()
            .Which.Should()
            .Be(hydratedWork.Work.Name!.Evidence);
        createdComposition.Value.Evidence.Single().JsonPointer.Should().Be("/title");
        resolved
            .Checkpoints.Where(checkpoint => checkpoint.Search != null)
            .Should()
            .OnlyContain(checkpoint => !checkpoint.Search!.ExternalUniverseComplete);
        var link = (await music.GetCompositionLinksAsync(source.Reference.RepertoireSongUuid))
            .Should()
            .ContainSingle()
            .Subject;
        link.Value.CompleteAuthorship.Should().BeFalse();
        link.Value.MatchedAuthorUuids.Should().BeEmpty();
        (await music.GetCompositionAuthorshipAsync(link.Value.CompositionUuid)).Should().BeEmpty();
        (await music.GetWorkCreditsAsync(link.Value.CompositionUuid)).Should().BeEmpty();
        var savedInputs = resolved.Preparation.Context.ReadSet.Inputs;
        resolved
            .Stages.Should()
            .OnlyContain(stage => savedInputs.All(input => stage.ReadSet.Inputs.Contains(input)));
        var calls = upstream.Calls;

        var replay = await processor.ResolveRepertoireAsync(attemptUuid, source.Reference);

        replay.Result!.Replayed.Should().BeTrue();
        upstream.Calls.Should().Be(calls);
        (await music.GetCompositionLinksAsync(source.Reference.RepertoireSongUuid))
            .Should()
            .ContainSingle();
        (await processor.GetAsync(attemptUuid))!
            .Proposal.Should()
            .BeEquivalentTo(resolved.Proposal);
    }

    [Test]
    public async Task DurablePreparationRetainsCompleteCanonicalAuthorsFromAnExistingCandidate()
    {
        var source = await Claim();
        var authorInput = await fixture.Capture("Fixture Canonical Writer", "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "Fixture Canonical Writer");
        var personWork = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("people", "person", person.Uuid),
            "fixture-v1",
            [authorInput.Resource],
            []
        );
        await new NormalizedPeopleService(fixture.Db, fixture.Work).PublishAsync(
            personWork.WorkUuid,
            new(
                [person],
                [new(Guid.NewGuid(), person.Uuid, person.DisplayName, authorInput.Reference)],
                [],
                []
            )
        );

        var titleInput = await fixture.Capture(source.Title, "compositions");
        var compositionUuid = Guid.NewGuid();
        var compositionWork = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("compositions", "composition", compositionUuid),
            "fixture-v1",
            [titleInput.Resource, authorInput.Resource],
            [personWork.SubjectUuid]
        );
        await music.PublishAsync(
            compositionWork.WorkUuid,
            new()
            {
                Compositions =
                [
                    new()
                    {
                        Uuid = compositionUuid,
                        DisplayTitle = source.Title,
                        Evidence = [titleInput.Reference],
                    },
                ],
                AuthorshipAssertions =
                [
                    new()
                    {
                        Uuid = Guid.NewGuid(),
                        CompositionUuid = compositionUuid,
                        CompleteAuthorship = true,
                        Authors =
                        [
                            new("writer", person.DisplayName, person.Uuid, authorInput.Reference),
                        ],
                        Evidence = [authorInput.Reference],
                    },
                ],
            }
        );

        var upstream = new Upstream(source);
        var processor = Processor(upstream);
        var attemptUuid = Guid.NewGuid();
        var prepared = await processor.ResolveRepertoireAsync(attemptUuid, source.Reference);
        prepared.Result.Should().BeNull();
        upstream.Calls.Should().Be(0);
        var initial = prepared.Preparation!.Context.Candidates.Facts.Single(fact =>
            fact.Header.Uuid == compositionUuid
        );
        initial.CompleteAuthorship.Should().BeTrue();
        initial.AuthorUuids.Should().BeEquivalentTo(new[] { person.Uuid });

        var recovered = (await processor.GetAsync(attemptUuid))!;
        var retained = recovered.Preparation!.Context.Candidates.Facts.Single(fact =>
            fact.Header.Uuid == compositionUuid
        );
        retained.AuthorUuids.Should().BeEquivalentTo(initial.AuthorUuids);
        retained.AuthorshipAssertions.Should().BeEquivalentTo(initial.AuthorshipAssertions);
        retained.CompleteAuthorship.Should().BeTrue();
        recovered.Preparation.Should().BeEquivalentTo(prepared.Preparation);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ANewAttemptAcknowledgesTheAcceptedLinkWithoutRevisingItsEvidenceOrAuthority(
        bool refreshRawHead
    )
    {
        var source = await Claim();
        await ActivatePolicy();
        var upstream = new Upstream(source);
        var processor = Processor(upstream);
        var first = await Finish(processor, source.Reference, Guid.NewGuid(), upstream);
        first
            .Result!.Disposition.Should()
            .Be(CompositionResolutionDisposition.Applied, Audit(first));
        var accepted = (
            await music.GetCompositionLinksAsync(source.Reference.RepertoireSongUuid)
        ).Single();
        var before = await LinkState(accepted.Value.Uuid, source.ArtistUuid);
        var currentInput = refreshRawHead
            ? await fixture.Refresh(source.Input, source.Title + " updated upstream wording")
            : source.Input;

        var next = await Finish(processor, source.Reference, Guid.NewGuid(), upstream);

        next.AttemptUuid.Should().NotBe(first.AttemptUuid);
        next.Result!.Disposition.Should().Be(CompositionResolutionDisposition.Applied, Audit(next));
        next.Result.Replayed.Should().BeFalse();
        next.Stages.Should()
            .ContainSingle()
            .Which.Kind.Should()
            .Be(CompositionResolutionStageKind.Link);
        next.Stages.Single().Publication.CompositionLinks.Should().BeEmpty();
        next.Stages.Single().Receipt!.Status.Should().Be(NormalizationPublicationStatus.Published);
        (await LinkState(accepted.Value.Uuid, source.ArtistUuid)).Should().Be(before);
        (await music.GetCompositionLinksAsync(source.Reference.RepertoireSongUuid))
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(accepted);
        next.Preparation!.Context.LocalTitle!.Evidence.InputRevisionUuid.Should()
            .Be(source.Input.Reference.InputRevisionUuid);
        next.Preparation.Context.LocalTitle.Value.Should().Be(source.Title);
        next.Preparation.Context.OwnerBindings!.Inputs.Should()
            .Contain(input => input.RevisionUuid == currentInput.Reference.InputRevisionUuid);
        if (refreshRawHead)
        {
            next.Preparation.Context.LocalTitle.Evidence.InputRevisionUuid.Should()
                .NotBe(currentInput.Reference.InputRevisionUuid);
        }
    }

    [TestCase("title_only")]
    [TestCase("wrong_performer")]
    [TestCase("backward_relationship")]
    [TestCase("competing_work")]
    public async Task SearchCandidatesCannotSupplyMissingOrContradictedCorrespondence(string fault)
    {
        var source = await Claim();
        await ActivatePolicy();
        var upstream = new Upstream(source, fault);
        var resolved = await Finish(
            Processor(upstream),
            source.Reference,
            Guid.NewGuid(),
            upstream
        );

        resolved
            .Result!.Disposition.Should()
            .BeOneOf(
                CompositionResolutionDisposition.Review,
                CompositionResolutionDisposition.Unresolved
            );
        resolved.Stages.Should().BeEmpty();
        (await music.GetCompositionLinksAsync(source.Reference.RepertoireSongUuid))
            .Should()
            .BeEmpty();
        resolved.Checkpoints.Should().Contain(checkpoint => checkpoint.Hydration != null);
    }

    [TestCase("missing_id")]
    [TestCase("invalid_id")]
    public async Task AMalformedCompetingRelationshipCannotBeDiscardedFromAnOtherwiseCorroboratedRecording(
        string malformedTarget
    )
    {
        var source = await Claim();
        await ActivatePolicy();
        var upstream = new Upstream(source, "malformed_competing_" + malformedTarget);

        var resolved = await Finish(
            Processor(upstream),
            source.Reference,
            Guid.NewGuid(),
            upstream
        );

        resolved
            .Result!.Disposition.Should()
            .Be(CompositionResolutionDisposition.Review, Audit(resolved));
        var candidate = resolved.Proposal!.Facts.Candidates.Should().ContainSingle().Subject;
        candidate
            .Contradictions.Should()
            .Contain(contradiction =>
                contradiction.StartsWith("recording_decode:", StringComparison.Ordinal)
            );
        var recording = candidate.Recordings.Should().ContainSingle().Subject;
        recording.Relationships.Should().HaveCount(2);
        recording
            .Relationships![0]
            .Target!.Identity.Identity.ProviderKey.Should()
            .Be(source.WorkMbid.ToString("D"));
        recording.Relationships[1].Target.Should().BeNull();
        recording.Relationships[1].Evidence.JsonPointer.Should().Be("/relations/1");
        var lookup = resolved
            .Checkpoints.Last(checkpoint => checkpoint.Hydration != null)
            .Hydration!.Lookups.Single(value =>
                value.Request.Kind == MusicBrainzLookupKind.Recording
            );
        var retained = (
            await new NormalizationEvidenceService(fixture.Db).GetInputsAsync([
                lookup.Acquisition!.Result!.Input!,
            ])
        ).Single();
        retained.Uuid.Should().Be(recording.Origin.Evidence.InputRevisionUuid);
        using var raw = JsonDocument.Parse(retained.Payload);
        var rivalTarget = raw.RootElement.GetProperty("relations")[1].GetProperty("work");
        if (malformedTarget == "missing_id")
        {
            rivalTarget.TryGetProperty("id", out _).Should().BeFalse();
        }
        else
        {
            rivalTarget.GetProperty("id").GetString().Should().Be("invalid-work-id");
        }
        resolved.Stages.Should().BeEmpty();
        (await music.GetCompositionLinksAsync(source.Reference.RepertoireSongUuid))
            .Should()
            .BeEmpty();
    }

    [Test]
    public async Task AnInactivePolicyRetainsTheProposalWithoutPublishingACompositionLink()
    {
        var source = await Claim();
        var upstream = new Upstream(source);
        var resolved = await Finish(
            Processor(upstream),
            source.Reference,
            Guid.NewGuid(),
            upstream
        );

        resolved.Result!.Disposition.Should().NotBe(CompositionResolutionDisposition.Applied);
        resolved.Preparation!.Policy.Should().BeNull();
        resolved.Stages.Should().BeEmpty();
        (await music.GetCompositionLinksAsync(source.Reference.RepertoireSongUuid))
            .Should()
            .BeEmpty();
    }

    [Test]
    public async Task PendingAdmissionResumesTheSameSavedCommandAndNeverSendsTwiceInOneInvocation()
    {
        var source = await Claim();
        await ActivatePolicy();
        var upstream = new Upstream(source);
        var processor = Processor(upstream);
        var attemptUuid = Guid.NewGuid();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE normalization.musicbrainz_gate SET attempt_uuid=NULL,token=NULL,lease_until=NULL,not_before=clock_timestamp()+interval '3 seconds' WHERE singleton"
            )
        );
        CompositionResolutionAttempt pending = null!;
        for (var i = 0; i < 8; i++)
        {
            pending = await processor.ResolveRepertoireAsync(attemptUuid, source.Reference);
            if (
                pending.Checkpoints.Any(checkpoint =>
                    checkpoint.Search?.Pages.Any(page =>
                        page.Acquisition?.State == MusicBrainzAcquisitionState.Pending
                    ) == true
                )
            )
            {
                break;
            }
        }
        upstream.Calls.Should().Be(0);
        var page = pending
            .Checkpoints.Last(checkpoint => checkpoint.Search != null)
            .Search!.Pages.First();
        page.Acquisition!.State.Should().Be(MusicBrainzAcquisitionState.Pending);
        page.Acquisition.NotBefore.Should().NotBeNull();
        var remaining = page.Acquisition.NotBefore!.Value - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining + TimeSpan.FromMilliseconds(20));
        }

        var resolved = await Finish(processor, source.Reference, attemptUuid, upstream);

        resolved
            .Result!.Disposition.Should()
            .Be(CompositionResolutionDisposition.Applied, Audit(resolved));
        resolved
            .Checkpoints.Last(checkpoint => checkpoint.Search != null)
            .Search!.Pages.Should()
            .Contain(value =>
                value.CommandUuid == page.CommandUuid
                && value.Acquisition!.State == MusicBrainzAcquisitionState.Completed
            );
        upstream.Requests.Should().OnlyHaveUniqueItems();
    }

    [Test]
    public async Task AnAttemptCannotBeReusedForAnotherAcceptedLocalClaim()
    {
        var source = await Claim();
        var other = await Claim();
        var upstream = new Upstream(source);
        var processor = Processor(upstream);
        var attemptUuid = Guid.NewGuid();
        await processor.ResolveRepertoireAsync(attemptUuid, source.Reference);

        var reuse = () => processor.ResolveRepertoireAsync(attemptUuid, other.Reference);

        await reuse.Should().ThrowAsync<NormalizationConflictException>();
        upstream.Calls.Should().Be(0);
        (await processor.GetAsync(attemptUuid))!.Claim.Should().Be(source.Reference);
    }

    [TestCase("input")]
    [TestCase("policy")]
    [TestCase("candidate")]
    [TestCase("artist_identity")]
    [TestCase("owner_editorial")]
    public async Task OriginalClocksRejectChangesAfterTheCreationStageWasAdmitted(string change)
    {
        var source = await Claim();
        await ActivatePolicy();
        var upstream = new Upstream(source);
        var processor = Processor(upstream);
        var staged = await UntilAdmitted(processor, source.Reference, Guid.NewGuid(), upstream);
        if (change == "input")
        {
            await AdvanceCapturedInput(staged.Stages.Single().ReadSet.Inputs.First());
        }
        else if (change == "policy")
        {
            await SetActivePolicy(null);
        }
        else if (change == "owner_editorial")
        {
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    "UPDATE normalization.work_subjects SET editorial_revision=editorial_revision+1 WHERE domain='songs' AND subject_kind='artist_repertoire' AND subject_uuid=@artist",
                    new { artist = source.ArtistUuid }
                )
            );
        }
        else if (change == "artist_identity")
        {
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    "UPDATE artists SET musicbrainz_id=@mbid WHERE uuid=@artist",
                    new { mbid = Guid.NewGuid().ToString(), artist = source.ArtistUuid }
                )
            );
        }
        else
        {
            await Composition(source.Title);
        }

        var resolved = await Finish(processor, source.Reference, staged.AttemptUuid, upstream);

        resolved
            .Result!.Disposition.Should()
            .Be(CompositionResolutionDisposition.Stale, Audit(resolved));
        resolved.Stages.Should().OnlyContain(stage => stage.Receipt == null);
        (await music.GetCompositionLinksAsync(source.Reference.RepertoireSongUuid))
            .Should()
            .BeEmpty();
        resolved.Preparation.Should().BeEquivalentTo(staged.Preparation);
    }

    [Test]
    public async Task AStageCanOnlyBePublishedByItsSavedWorkAndReplaysItsAtomicReceipt()
    {
        var source = await Claim();
        await ActivatePolicy();
        var upstream = new Upstream(source);
        var processor = Processor(upstream);
        var staged = await UntilAdmitted(processor, source.Reference, Guid.NewGuid(), upstream);
        var stage = staged.Stages.Single();
        var wrongWork = () =>
            music.PublishCompositionResolutionAsync(Guid.NewGuid(), staged.AttemptUuid);
        await wrongWork.Should().ThrowAsync<Exception>();
        (await processor.GetAsync(staged.AttemptUuid))!.Stages.Single().Receipt.Should().BeNull();

        var published = await music.PublishCompositionResolutionAsync(
            stage.WorkUuid,
            staged.AttemptUuid
        );
        published.Status.Should().Be(NormalizationPublicationStatus.Published);
        var replay = await music.PublishCompositionResolutionAsync(
            stage.WorkUuid,
            staged.AttemptUuid
        );
        replay.Replayed.Should().BeTrue();
        (await processor.GetAsync(staged.AttemptUuid))!
            .Stages.Single()
            .Receipt.Should()
            .NotBeNull();

        var resolved = await Finish(processor, source.Reference, staged.AttemptUuid, upstream);
        resolved
            .Result!.Disposition.Should()
            .Be(CompositionResolutionDisposition.Applied, Audit(resolved));
        resolved.Stages.Should().HaveCount(2);
        (await music.GetCompositionLinksAsync(source.Reference.RepertoireSongUuid))
            .Should()
            .ContainSingle();
    }

    [Test]
    public async Task DomainCreationAndItsStageReceiptRollbackTogetherAndCanResume()
    {
        var source = await Claim();
        await ActivatePolicy();
        var upstream = new Upstream(source);
        var processor = Processor(upstream);
        var staged = await UntilAdmitted(processor, source.Reference, Guid.NewGuid(), upstream);
        var stage = staged.Stages.Single();
        var compositionUuid = stage.Publication.Compositions.Single().Uuid;
        var trigger = "composition_receipt_failure_" + Guid.NewGuid().ToString("N");
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                $"""
                CREATE FUNCTION normalization.{trigger}() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.attempt_uuid='{staged.AttemptUuid:D}'::uuid THEN
                        RAISE EXCEPTION 'synthetic composition receipt failure';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER {trigger} BEFORE UPDATE OF receipt ON normalization.composition_resolution_stages
                    FOR EACH ROW EXECUTE FUNCTION normalization.{trigger}();
                """
            )
        );
        try
        {
            var publish = () =>
                music.PublishCompositionResolutionAsync(stage.WorkUuid, staged.AttemptUuid);
            var failure = await publish.Should().ThrowAsync<Exception>();
            var databaseFailure = failure
                .Which.InnerException.Should()
                .BeOfType<PostgresException>()
                .Which;
            databaseFailure.SqlState.Should().Be("P0001");
            databaseFailure.MessageText.Should().Be("synthetic composition receipt failure");
            (await music.GetCompositionAsync(compositionUuid)).Should().BeNull();
            (await processor.GetAsync(staged.AttemptUuid))!
                .Stages.Single()
                .Receipt.Should()
                .BeNull();
        }
        finally
        {
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    $"""
                    DROP TRIGGER {trigger} ON normalization.composition_resolution_stages;
                    DROP FUNCTION normalization.{trigger}();
                    """
                )
            );
        }

        var resolved = await Finish(processor, source.Reference, staged.AttemptUuid, upstream);

        resolved
            .Result!.Disposition.Should()
            .Be(CompositionResolutionDisposition.Applied, Audit(resolved));
        (await music.GetCompositionAsync(compositionUuid)).Should().NotBeNull();
        resolved.Stages.First().Publication.Compositions.Single().Uuid.Should().Be(compositionUuid);
        (await music.GetCompositionLinksAsync(source.Reference.RepertoireSongUuid))
            .Should()
            .ContainSingle();
    }

    [Test]
    public async Task AReviewedRejectionAfterCreationCannotBecomePermissionToPublishTheLink()
    {
        var source = await Claim();
        await ActivatePolicy();
        var upstream = new Upstream(source);
        var processor = Processor(upstream);
        var staged = await UntilAdmitted(processor, source.Reference, Guid.NewGuid(), upstream);
        var creation = staged.Stages.Single();
        creation.Kind.Should().Be(CompositionResolutionStageKind.CreateComposition);
        var published = await music.PublishCompositionResolutionAsync(
            creation.WorkUuid,
            staged.AttemptUuid
        );
        published.Status.Should().Be(NormalizationPublicationStatus.Published);
        var interrupted = (await processor.GetAsync(staged.AttemptUuid))!;
        interrupted.Stages.Should().ContainSingle().Which.Receipt.Should().NotBeNull();
        var composition = (
            await music.GetCompositionAsync(creation.Publication.Compositions.Single().Uuid)
        )!;
        var compositionOwner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("compositions", "composition", composition.Value.Uuid)
            )
        )!;
        var repertoireOwnerKey = new NormalizationSubjectKey(
            "songs",
            "artist_repertoire",
            source.ArtistUuid
        );
        var repertoireOwner = (await fixture.Work.GetSubjectAsync(repertoireOwnerKey))!;

        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            compositionOwner.SubjectUuid,
            compositionOwner.EditorialRevision,
            "reject",
            new()
            {
                Compositions = [composition.Value with { ExpectedRevision = composition.Revision }],
            },
            "Reviewer rejected the newly materialized composition correspondence",
            Operator
        );

        (await fixture.Work.GetSubjectAsync(repertoireOwnerKey))
            .Should()
            .BeEquivalentTo(repertoireOwner);
        var resolved = await Finish(processor, source.Reference, staged.AttemptUuid, upstream);

        resolved
            .Result!.Disposition.Should()
            .BeOneOf(
                CompositionResolutionDisposition.Stale,
                CompositionResolutionDisposition.Review
            );
        resolved
            .Stages.Should()
            .ContainSingle()
            .Which.Kind.Should()
            .Be(CompositionResolutionStageKind.CreateComposition);
        resolved.Stages.Single().Receipt.Should().NotBeNull();
        (await music.GetCompositionLinksAsync(source.Reference.RepertoireSongUuid))
            .Should()
            .BeEmpty();
    }

    [Test]
    public async Task ANewAttemptAfterRevocationPreservesTheOldPreparationAndObservesTheNewPolicyClock()
    {
        var source = await Claim();
        await ActivatePolicy();
        var upstream = new Upstream(source);
        var processor = Processor(upstream);
        var staged = await UntilAdmitted(processor, source.Reference, Guid.NewGuid(), upstream);
        await SetActivePolicy(null);
        var stale = await Finish(processor, source.Reference, staged.AttemptUuid, upstream);
        stale.Result!.Disposition.Should().Be(CompositionResolutionDisposition.Stale);
        await ActivatePolicy();
        var newAttemptUuid = Guid.NewGuid();

        var retry = await processor.RetryAsync(newAttemptUuid, staged.AttemptUuid);
        retry.PreviousAttemptUuid.Should().Be(staged.AttemptUuid);
        var resolved = await Finish(processor, source.Reference, newAttemptUuid, upstream);

        resolved
            .Result!.Disposition.Should()
            .Be(CompositionResolutionDisposition.Applied, Audit(resolved));
        resolved
            .Preparation!.Policy!.Policy.Artifact.Uuid.Should()
            .NotBe(staged.Preparation!.Policy!.Policy.Artifact.Uuid);
        (await processor.GetAsync(staged.AttemptUuid))!
            .Preparation.Should()
            .BeEquivalentTo(staged.Preparation);
    }

    [Test]
    public async Task PolicyRegistrationRequiresExactAuthenticatedAttestationAndDoesNotActivateIt()
    {
        var policy = CompositionPolicyFixture.Policy(Operator);
        var wrongOperator = () =>
            policies.RegisterEvaluatedPolicyAsync(
                policy.Artifact,
                policy.Evaluation,
                "other-operator"
            );
        await wrongOperator.Should().ThrowAsync<ArgumentException>();
        var changed = policy.Artifact with { RuleId = "changed-after-evaluation" };
        var mutatedHash = () =>
            policies.RegisterEvaluatedPolicyAsync(changed, policy.Evaluation, Operator);
        await mutatedHash.Should().ThrowAsync<ArgumentException>();

        await policies.RegisterEvaluatedPolicyAsync(policy.Artifact, policy.Evaluation, Operator);
        var replay = await policies.RegisterEvaluatedPolicyAsync(
            policy.Artifact,
            policy.Evaluation,
            Operator
        );

        replay.Should().BeEquivalentTo(policy);
        (await policies.ReadActiveAsync()).Should().BeNull();
        var untrustedHost = new CompositionResolutionPolicyService(fixture.Db, fixture.Work);
        var fixtureOnUntrustedHost = () =>
            untrustedHost.RegisterEvaluatedPolicyAsync(
                policy.Artifact,
                policy.Evaluation,
                Operator
            );
        await fixtureOnUntrustedHost.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task AReviewedRetractionAcceptsRetainedReceiptProvenanceWhileGenericAutomaticPublicationRejectsIt()
    {
        var source = await Claim();
        await ActivatePolicy();
        var upstream = new Upstream(source);
        var resolved = await Finish(
            Processor(upstream),
            source.Reference,
            Guid.NewGuid(),
            upstream
        );
        resolved
            .Result!.Disposition.Should()
            .Be(CompositionResolutionDisposition.Applied, Audit(resolved));
        var accepted = (
            await music.GetCompositionLinksAsync(source.Reference.RepertoireSongUuid)
        ).Single();
        accepted.Value.EvaluatedCorrespondenceReceiptUuid.Should().NotBeNull();
        var subject = new NormalizationSubjectKey("songs", "artist_repertoire", source.ArtistUuid);
        var context = await new NormalizationCandidateService(
            fixture.Db
        ).ReadCompositionResolutionContextAsync(source.Reference);
        var bindings = context.OwnerBindings!;
        // A generic caller must still consume the contributor-managed owner's entire union.
        // Read current coordination stamps with membership; the prior stage's audit ticket
        // intentionally retains pre-publication candidate and owner observations.
        var generic = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            "generic-composition-fixture-v1",
            context.OwnerInputReferences!.Select(input => input.Resource).ToArray(),
            bindings.Dependencies.Select(dependency => dependency.SubjectUuid).ToArray(),
            context.OwnerMembership!.Owner!.DesiredGeneration,
            catalogInputs: bindings.CatalogInputs,
            sourceIdentities: bindings.SourceIdentities,
            mappingRegistry: bindings.MappingRegistry,
            expectedInputRevisions: context.OwnerInputReferences,
            importedVenues: bindings.ImportedVenues,
            candidateUniverses: bindings.CandidateUniverses,
            expectedOwnerObservations: bindings.OwnerObservations,
            sourceUuids: bindings.SourceUuids
        );
        var update = accepted.Value with { ExpectedRevision = accepted.Revision };

        var automatic = () =>
            music.PublishAsync(generic.WorkUuid, new() { CompositionLinks = [update] });
        await automatic
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("*authenticated processor publication route*");

        var owner = (await fixture.Work.GetSubjectAsync(subject))!;
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            owner.SubjectUuid,
            owner.EditorialRevision,
            "retract",
            new() { CompositionLinks = [update with { Retracted = true }] },
            "Reviewed removal of the accepted correspondence",
            Operator
        );

        var retracted = await LinkState(accepted.Value.Uuid, source.ArtistUuid);
        retracted.Revision.Should().Be(accepted.Revision + 1);
        retracted.Basis.Should().Be("reviewed");
        using var payload = JsonDocument.Parse(retracted.Payload);
        payload.RootElement.GetProperty("Retracted").GetBoolean().Should().BeTrue();
        payload
            .RootElement.GetProperty("EvaluatedCorrespondenceReceiptUuid")
            .GetGuid()
            .Should()
            .Be(accepted.Value.EvaluatedCorrespondenceReceiptUuid!.Value);
    }

    [Test]
    public async Task RemovingAnUnrelatedPeerRemovesItsResourceAndCatalogKeyFromTheOwnerUnion()
    {
        var source = await Claim();
        var subject = new NormalizationSubjectKey("songs", "artist_repertoire", source.ArtistUuid);
        var baseline = (await fixture.Work.GetInputContributionsAsync(subject)).UnmanagedBaseline!;
        var peerInput = await fixture.Capture("Unrelated peer recording context", "songs");
        var peerCatalog = await fixture.Source();
        var peerArtist = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "SELECT uuid FROM artists WHERE id=@id",
                new { id = peerCatalog.ArtistId }
            )
        );
        var peerClock = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT generation FROM normalization.catalog_artist_revisions WHERE artist_uuid=@uuid",
                new { uuid = peerArtist }
            )
        );
        var peer = new NormalizationResourceKey(
            "test",
            "unrelated-peer",
            Guid.NewGuid().ToString("N")
        );
        var owner = (await fixture.Work.GetSubjectAsync(subject))!;
        await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            subject,
            "peer-fixture-v1",
            new(peer, 0, [peerInput.Resource], []),
            owner.DesiredGeneration,
            catalogInputs: [new(peerArtist, peerClock)],
            expectedInputRevisions: [new(peerInput.Reference.InputRevisionUuid, peerInput.Resource)]
        );
        await ActivatePolicy();
        var upstream = new Upstream(source);
        var resolved = await Finish(
            Processor(upstream),
            source.Reference,
            Guid.NewGuid(),
            upstream
        );
        resolved
            .Result!.Disposition.Should()
            .Be(CompositionResolutionDisposition.Applied, Audit(resolved));
        var linkStage = resolved.Stages.Single(stage =>
            stage.Kind == CompositionResolutionStageKind.Link
        );
        var before = await fixture.Work.GetInputContributionsAsync(subject);
        var resolverContributor = before.Contributions.Single(contribution =>
            contribution.Contributor.Provider == "relisten"
            && contribution.Contributor.ResourceType == "composition_resolution"
        );
        resolverContributor.Resources.Should().NotContain(peerInput.Resource);
        resolverContributor.CatalogArtistUuids.Should().NotContain(peerArtist);
        var preservedBaseline = before.Contributions.Single(contribution =>
            contribution.Contributor == baseline.Contributor
        );
        preservedBaseline.Resources.Should().BeEquivalentTo(baseline.Resources);
        preservedBaseline.CatalogArtistUuids.Should().BeEquivalentTo(baseline.CatalogArtistUuids);
        var peerState = before.Contributions.Single(contribution =>
            contribution.Contributor == peer
        );
        var currentObservations = new List<NormalizationSubjectObservation>();
        foreach (var observation in linkStage.Ticket!.OwnerObservations ?? [])
        {
            var current = (await fixture.Work.GetSubjectAsync(observation.SubjectUuid))!;
            currentObservations.Add(
                new(
                    current.SubjectUuid,
                    current.DesiredGeneration,
                    current.EditorialRevision,
                    current.PublishedWorkUuid
                )
            );
        }
        owner = (await fixture.Work.GetSubjectAsync(subject))!;

        var tombstone = await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            subject,
            NormalizationCompositionResolutionProcessor.Version,
            new(peer, peerState.Revision, [], []),
            owner.DesiredGeneration,
            expectedOwnerObservations: currentObservations
        );

        tombstone
            .Inputs.Should()
            .NotContain(input => input.RevisionUuid == peerInput.Reference.InputRevisionUuid);
        tombstone.CatalogInputs.Should().NotContain(input => input.ArtistUuid == peerArtist);
        tombstone.CatalogInputs.Should().Contain(input => input.ArtistUuid == source.ArtistUuid);
        var peerResourceUuid = linkStage
            .Ticket.Inputs.Single(input =>
                input.RevisionUuid == peerInput.Reference.InputRevisionUuid
            )
            .ResourceUuid;
        var resolverInputs = linkStage
            .Ticket.Inputs.Where(input => input.ResourceUuid != peerResourceUuid)
            .ToArray();
        tombstone.Inputs.Should().Contain(resolverInputs);
        var after = await fixture.Work.GetInputContributionsAsync(subject);
        after
            .Contributions.Single(contribution => contribution.Contributor == peer)
            .Resources.Should()
            .BeEmpty();
        after
            .Contributions.Single(contribution =>
                contribution.Contributor == resolverContributor.Contributor
            )
            .Should()
            .BeEquivalentTo(resolverContributor);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReviewedRejectionsOnlyBlockTheRelevantRepertoireClaim(bool rejectTarget)
    {
        var source = await Claim();
        var accepted = (await music.GetRepertoireAsync(source.Reference.RepertoireSongUuid))!;
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("songs", "artist_repertoire", source.ArtistUuid)
            )
        )!;
        var rejected = rejectTarget
            ? accepted.Value with
            {
                ExpectedRevision = accepted.Revision,
            }
            : accepted.Value with
            {
                Uuid = Guid.NewGuid(),
                DisplayTitle = "Unrelated rejected song " + Guid.NewGuid().ToString("N"),
            };
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            owner.SubjectUuid,
            owner.EditorialRevision,
            "reject",
            new() { RepertoireSongs = [rejected] },
            "Reviewed repertoire exclusion",
            Operator
        );
        await ActivatePolicy();
        var upstream = new Upstream(source);

        var resolved = await Finish(
            Processor(upstream),
            source.Reference,
            Guid.NewGuid(),
            upstream
        );

        if (rejectTarget)
        {
            resolved
                .Result!.Disposition.Should()
                .Be(CompositionResolutionDisposition.Review, Audit(resolved));
            resolved.Proposal!.Decision.Reason.Should().Be("reviewed_music_exclusion");
            resolved.Stages.Should().BeEmpty();
            (await music.GetCompositionLinksAsync(source.Reference.RepertoireSongUuid))
                .Should()
                .BeEmpty();
        }
        else
        {
            resolved
                .Result!.Disposition.Should()
                .Be(CompositionResolutionDisposition.Applied, Audit(resolved));
            (await music.GetCompositionLinksAsync(source.Reference.RepertoireSongUuid))
                .Should()
                .ContainSingle();
        }
    }

    private async Task AdvanceCapturedInput(NormalizationInputDependency input)
    {
        var resource = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<NormalizationResourceKey>(
                "SELECT provider AS Provider,resource_type AS ResourceType,provider_key AS ProviderKey FROM normalization.input_resources WHERE uuid=@uuid",
                new { uuid = input.ResourceUuid }
            )
        );
        resource.Provider.Should().Be("musicbrainz.org");
        await new NormalizationEvidenceService(fixture.Db).CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                JsonSerializer.SerializeToUtf8Bytes(new { changed = true }),
                "application/json",
                "fixture-mutation-v1",
                input.Generation,
                DateTimeOffset.UtcNow
            )
        );
    }

    private NormalizationCompositionResolutionProcessor Processor(Upstream upstream)
    {
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var client = new HttpClient(upstream) { Timeout = Timeout.InfiniteTimeSpan };
        clients.Add(client);
        var inputs = new NormalizationMusicBrainzInputService(
            client,
            new(fixture.Db),
            evidence,
            new()
            {
                UserAgent = "RelistenNormalization.CompositionTests/1.0 (https://relisten.net)",
            }
        );
        return new(
            fixture.Db,
            new(fixture.Db),
            inputs,
            evidence,
            new(),
            policies,
            new(fixture.Db),
            fixture.Work,
            music
        );
    }

    private async Task<CompositionResolutionAttempt> Finish(
        NormalizationCompositionResolutionProcessor processor,
        CatalogWorkClaimReference claim,
        Guid attemptUuid,
        Upstream upstream
    )
    {
        for (var i = 0; i < 80; i++)
        {
            await ReadyGate();
            var before = upstream.Calls;
            var result = await processor.ResolveRepertoireAsync(attemptUuid, claim);
            (upstream.Calls - before).Should().BeLessThanOrEqualTo(1);
            if (
                result.Result != null
                && result.Result.Disposition != CompositionResolutionDisposition.Pending
            )
            {
                return result;
            }
        }
        throw new AssertionException(
            "Composition resolution did not reach a terminal receipt within its bounded phases."
        );
    }

    private async Task<CompositionResolutionAttempt> UntilAdmitted(
        NormalizationCompositionResolutionProcessor processor,
        CatalogWorkClaimReference claim,
        Guid attemptUuid,
        Upstream upstream
    )
    {
        for (var i = 0; i < 80; i++)
        {
            await ReadyGate();
            var before = upstream.Calls;
            var result = await processor.ResolveRepertoireAsync(attemptUuid, claim);
            (upstream.Calls - before).Should().BeLessThanOrEqualTo(1);
            if (result.Stages.Any(stage => stage.Ticket != null && stage.Receipt == null))
            {
                return result;
            }
            if (result.Result != null)
            {
                result
                    .Result.Disposition.Should()
                    .Be(CompositionResolutionDisposition.Pending, Audit(result));
            }
        }
        throw new AssertionException("No admitted composition stage was retained.");
    }

    private Task ReadyGate() =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE normalization.musicbrainz_gate SET attempt_uuid=NULL,token=NULL,lease_until=NULL,not_before=clock_timestamp()-interval '10 seconds' WHERE singleton"
            )
        );

    private async Task<ClaimFixture> Claim()
    {
        var source = await fixture.Source();
        var artistMbid = Guid.NewGuid();
        var artistUuid = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "UPDATE artists SET musicbrainz_id=@mbid WHERE id=@artist RETURNING uuid",
                new { mbid = artistMbid.ToString(), artist = source.ArtistId }
            )
        );
        var title = "Composition fixture " + Guid.NewGuid().ToString("N");
        var input = await fixture.Capture(title, "songs");
        var song = new RepertoireSongChange
        {
            Uuid = Guid.NewGuid(),
            ArtistUuid = artistUuid,
            DisplayTitle = title,
            Basis = "title_derived",
            Evidence = [input.Reference],
        };
        var ticket = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("songs", "artist_repertoire", artistUuid),
            "fixture-v1",
            [input.Resource],
            []
        );
        (await music.PublishAsync(ticket.WorkUuid, new() { RepertoireSongs = [song] }))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        var revision = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "SELECT revision_uuid FROM normalization.repertoire_songs WHERE uuid=@uuid",
                new { uuid = song.Uuid }
            )
        );
        return new(
            new(song.Uuid, revision),
            input,
            artistUuid,
            artistMbid,
            title,
            Guid.NewGuid(),
            Guid.NewGuid()
        );
    }

    private async Task Composition(string title)
    {
        var input = await fixture.Capture(title, "compositions");
        var uuid = Guid.NewGuid();
        var ticket = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("compositions", "composition", uuid),
            "fixture-v1",
            [input.Resource],
            []
        );
        await music.PublishAsync(
            ticket.WorkUuid,
            new()
            {
                Compositions =
                [
                    new()
                    {
                        Uuid = uuid,
                        DisplayTitle = title,
                        Evidence = [input.Reference],
                    },
                ],
            }
        );
    }

    private async Task ActivatePolicy()
    {
        var policy = CompositionPolicyFixture.Policy(Operator);
        await policies.RegisterEvaluatedPolicyAsync(policy.Artifact, policy.Evaluation, Operator);
        await SetActivePolicy(policy.Artifact.Uuid);
    }

    private async Task SetActivePolicy(Guid? artifactUuid)
    {
        var active = await policies.ReadActiveAsync();
        var owner = await fixture.Work.GetSubjectAsync(CompositionResolutionPolicyService.Owner);
        if (owner == null)
        {
            await fixture.Work.BeginWorkAsync(
                Guid.NewGuid(),
                CompositionResolutionPolicyService.Owner,
                CompositionResolutionPolicyService.RegistryVersion,
                [],
                []
            );
            owner = (await fixture.Work.GetSubjectAsync(CompositionResolutionPolicyService.Owner))!;
        }
        await policies.ApplyDecisionAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                active?.Policy.Artifact.Uuid,
                artifactUuid,
                "Isolated composition correspondence fixture"
            ),
            Operator
        );
    }

    private Task<AcceptedLinkState> LinkState(Guid uuid, Guid artist) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<AcceptedLinkState>(
                """
                SELECT r.uuid AS "RevisionUuid",r.revision AS "Revision",r.decision_uuid AS "DecisionUuid",
                    r.basis AS "Basis",r.payload::text AS "Payload",r.evidence::text AS "Evidence",
                    (SELECT count(*) FROM normalization.music_revisions WHERE entity_uuid=@uuid) AS "Revisions",
                    (SELECT count(*) FROM normalization.music_evidence_references e
                        JOIN normalization.music_revisions h ON h.uuid=e.revision_uuid
                        WHERE h.entity_uuid=@uuid) AS "EvidenceReferences",
                    COALESCE((SELECT generation FROM normalization.candidate_universe_revisions
                        WHERE domain='compositions' AND scope_uuid=@empty),0) AS "Compositions",
                    COALESCE((SELECT generation FROM normalization.candidate_universe_revisions
                        WHERE domain='repertoire' AND scope_uuid=@artist),0) AS "Repertoire"
                FROM normalization.repertoire_compositions x
                    JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid WHERE x.uuid=@uuid
                """,
                new
                {
                    uuid,
                    artist,
                    empty = Guid.Empty,
                }
            )
        );

    private sealed record AcceptedLinkState(
        Guid RevisionUuid,
        long Revision,
        Guid DecisionUuid,
        string Basis,
        string Payload,
        string Evidence,
        long Revisions,
        long EvidenceReferences,
        long Compositions,
        long Repertoire
    );

    private static string Audit(CompositionResolutionAttempt attempt) =>
        JsonSerializer.Serialize(attempt);

    private sealed record ClaimFixture(
        CatalogWorkClaimReference Reference,
        DomainInput Input,
        Guid ArtistUuid,
        Guid ArtistMbid,
        string Title,
        Guid RecordingMbid,
        Guid WorkMbid
    );

    private sealed class Upstream(ClaimFixture claim, string? fault = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string> Requests { get; } = [];
        private readonly Guid rival = Guid.NewGuid();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var uri = request.RequestUri!;
            Requests.Add(uri.ToString());
            var kind = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[2];
            var search = uri.Query.Contains("query=", StringComparison.Ordinal);
            object body;
            if (search && kind == "recording")
            {
                body = new
                {
                    count = 1,
                    offset = 0,
                    recordings = new[]
                    {
                        new
                        {
                            id = claim.RecordingMbid,
                            title = claim.Title,
                            score = 100,
                        },
                    },
                };
            }
            else if (search && kind == "work")
            {
                var works = new List<object>
                {
                    new
                    {
                        id = claim.WorkMbid,
                        title = claim.Title,
                        score = 100,
                    },
                };
                if (fault == "competing_work")
                    works.Add(
                        new
                        {
                            id = rival,
                            title = claim.Title,
                            score = 99,
                        }
                    );
                body = new
                {
                    count = works.Count,
                    offset = 0,
                    works,
                };
            }
            else if (search)
            {
                body = new
                {
                    count = 0,
                    offset = 0,
                    artists = Array.Empty<object>(),
                };
            }
            else if (kind == "recording")
            {
                var relations = new List<object>();
                if (fault != "title_only")
                {
                    relations.Add(Relation(claim.WorkMbid));
                    if (fault == "competing_work")
                        relations.Add(Relation(rival));
                    if (
                        fault
                        is "malformed_competing_missing_id"
                            or "malformed_competing_invalid_id"
                    )
                    {
                        var work = new Dictionary<string, object> { ["title"] = claim.Title };
                        if (fault == "malformed_competing_invalid_id")
                        {
                            work["id"] = "invalid-work-id";
                        }
                        relations.Add(
                            new Dictionary<string, object>
                            {
                                ["type"] = "performance",
                                ["direction"] = "forward",
                                ["target-type"] = "work",
                                ["work"] = work,
                            }
                        );
                    }
                }
                body = new Dictionary<string, object>
                {
                    ["id"] = claim.RecordingMbid,
                    ["title"] = claim.Title,
                    ["artist-credit"] = new[]
                    {
                        new
                        {
                            name = "Fixture performer",
                            artist = new
                            {
                                id = fault == "wrong_performer" ? Guid.NewGuid() : claim.ArtistMbid,
                                name = "Fixture performer",
                                type = "Group",
                            },
                        },
                    },
                    ["relations"] = relations,
                };
            }
            else if (kind == "work")
            {
                var id = Guid.Parse(uri.AbsolutePath.Split('/').Last());
                body = new
                {
                    id,
                    title = claim.Title,
                    relations = Array.Empty<object>(),
                };
            }
            else if (kind == "artist")
            {
                body = new
                {
                    id = claim.ArtistMbid,
                    name = "Fixture performer",
                    type = "Group",
                    aliases = Array.Empty<object>(),
                };
            }
            else
            {
                throw new AssertionException(
                    "Unexpected MusicBrainz fixture path: " + uri.AbsolutePath
                );
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body)),
                RequestMessage = request,
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(response);
        }

        private object Relation(Guid work) =>
            new Dictionary<string, object>
            {
                ["type"] = "performance",
                ["direction"] = fault == "backward_relationship" ? "backward" : "forward",
                ["target-type"] = "work",
                ["work"] = new { id = work, title = claim.Title },
            };
    }
}

internal static class CompositionPolicyFixture
{
    public static RegisteredCompositionAcceptancePolicy Policy(
        string approvedBy = "composition-fixture-operator"
    )
    {
        var artifact = new CompositionAcceptancePolicyArtifact(
            Guid.NewGuid(),
            "composition-fixture-v1",
            "",
            CompositionResolutionPolicyEvaluator.ProcessorVersion,
            CompositionResolutionPolicyEvaluator.FactsVersion,
            MusicBrainzCandidateQueryBuilder.Version,
            NormalizationProviderDecoder.Version,
            CompositionResolutionPolicyEvaluator.FixtureRuleId,
            FixtureOnly: true
        );
        artifact = artifact with
        {
            ContentHash = CompositionResolutionPolicyService.ComputeArtifactHash(artifact),
        };
        var evaluation = new CompositionAcceptanceEvaluationReceipt(
            Guid.NewGuid(),
            artifact.Uuid,
            artifact.ContentHash,
            "",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            "fixture-correspondence-labels-v1",
            "independent-fixture-adjudicator",
            approvedBy,
            new(100, 0),
            FixtureOnly: true
        );
        evaluation = evaluation with
        {
            ReceiptHash = CompositionResolutionPolicyService.ComputeEvaluationHash(evaluation),
        };
        return new(artifact, evaluation);
    }
}

[TestFixture]
public sealed class TestNormalizationCompositionCorrespondenceRule
{
    private readonly CompositionResolutionPolicyEvaluator evaluator = new();

    [Test]
    public void AffirmativeCitedCorroborationCanSucceedWithAnIncompleteExternalUniverse()
    {
        var facts = Facts();

        var result = evaluator.Evaluate(facts, CompositionPolicyFixture.Policy());

        facts.ExternalUniverseComplete.Should().BeFalse();
        result.Action.Should().Be(CompositionCorrespondenceAction.ProposeAcceptance);
        result.ExternalWorkMbid.Should().Be(facts.Candidates.Single().ExternalWorkMbid);
        result.Candidates.Single().Evidence.Should().Contain(facts.SourceTitle.Evidence);
        result
            .Candidates.Single()
            .Features.Should()
            .Contain("explicit_forward_recording_performance_of_work");
        facts.Candidates.Single().Work.CompleteAuthorship.Should().BeNull();
    }

    [TestCase("title_only")]
    [TestCase("wrong_performer")]
    [TestCase("missing_catalog_mbid")]
    [TestCase("backward_relationship")]
    [TestCase("wrong_relation_type")]
    [TestCase("wrong_target_type")]
    [TestCase("recording_title_differs")]
    [TestCase("work_identity_differs")]
    [TestCase("alternative_work_relationship")]
    public void TitleAgreementCannotReplaceTheRequiredCorrespondencePath(string fault)
    {
        var facts = Facts(fault);

        var result = evaluator.Evaluate(facts, CompositionPolicyFixture.Policy());

        result.Action.Should().Be(CompositionCorrespondenceAction.NeedsReview);
        result.ExternalWorkMbid.Should().BeNull();
        result.Candidates.Should().ContainSingle().Which.Contradictions.Should().NotBeEmpty();
    }

    [Test]
    public void EquallyCorroboratedSameTitleWorksRemainAlternatives()
    {
        var facts = Facts();
        var rival = Facts();
        var recording = rival.Candidates.Single().Recordings.Single();
        recording = recording with
        {
            ArtistCredits = facts.Candidates.Single().Recordings.Single().ArtistCredits,
        };
        facts = facts with
        {
            Candidates =
            [
                facts.Candidates.Single(),
                rival.Candidates.Single() with
                {
                    Recordings = [recording],
                },
            ],
        };

        var result = evaluator.Evaluate(facts, CompositionPolicyFixture.Policy());

        result.Action.Should().Be(CompositionCorrespondenceAction.NeedsReview);
        result.Reason.Should().Be("competing_corroborated_candidates");
        result.Candidates.Should().HaveCount(2).And.OnlyContain(candidate => candidate.Eligible);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AContradictoryRecordingCannotBeHiddenBehindACleanRecordingOrder(
        bool conflictingFirst
    )
    {
        var facts = Facts();
        var candidate = facts.Candidates.Single();
        var clean = candidate.Recordings.Single();
        var cleanRelation = clean.Relationships!.Single();
        var conflicting = clean with
        {
            Recording = clean.Recording with
            {
                Identity = new(
                    new("musicbrainz.org", "recording", Guid.NewGuid().ToString()),
                    clean.Recording.Identity.Evidence
                ),
            },
            Relationships =
            [
                cleanRelation,
                cleanRelation with
                {
                    Target = candidate.Work.Work with
                    {
                        Identity = new(
                            new("musicbrainz.org", "work", Guid.NewGuid().ToString()),
                            candidate.Work.Work.Identity.Evidence
                        ),
                    },
                },
            ],
        };
        var first = evaluator.Evaluate(
            facts with
            {
                Candidates =
                [
                    candidate with
                    {
                        Recordings = conflictingFirst ? [conflicting, clean] : [clean, conflicting],
                    },
                ],
            },
            CompositionPolicyFixture.Policy()
        );
        var reversed = evaluator.Evaluate(
            facts with
            {
                Candidates =
                [
                    candidate with
                    {
                        Recordings = conflictingFirst ? [clean, conflicting] : [conflicting, clean],
                    },
                ],
            },
            CompositionPolicyFixture.Policy()
        );

        first.Action.Should().Be(CompositionCorrespondenceAction.NeedsReview);
        reversed.Action.Should().Be(CompositionCorrespondenceAction.NeedsReview);
        first
            .Candidates.Single()
            .Contradictions.Should()
            .Contain("corroborating_recording_has_alternative_work_relationship");
        first
            .Candidates.Single()
            .Contradictions.Should()
            .BeEquivalentTo(reversed.Candidates.Single().Contradictions);
        first
            .Candidates.Single()
            .Evidence.Should()
            .BeEquivalentTo(reversed.Candidates.Single().Evidence);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TruncatedOrDuplicateLocalAlternativesCannotBeEvaluatedAsACompletePool(
        bool duplicate
    )
    {
        var facts = Facts();
        facts = duplicate
            ? facts with
            {
                Candidates = [facts.Candidates.Single(), facts.Candidates.Single()],
            }
            : facts with
            {
                LocalCandidatesComplete = false,
            };

        var result = evaluator.Evaluate(facts, CompositionPolicyFixture.Policy());

        result.Action.Should().Be(CompositionCorrespondenceAction.NeedsReview);
        result.Reason.Should().Be("incomplete_or_inconsistent_local_candidate_pool");
    }

    [Test]
    public void AFixtureRuleCannotBeRelabeledAsAProductionAcceptancePolicy()
    {
        var policy = CompositionPolicyFixture.Policy();
        policy = policy with { Artifact = policy.Artifact with { FixtureOnly = false } };

        evaluator
            .Evaluate(Facts(), policy)
            .Action.Should()
            .Be(CompositionCorrespondenceAction.NeedsReview);
    }

    private static CompositionCorrespondenceFacts Facts(string? fault = null)
    {
        var input = Guid.NewGuid();
        var workMbid = Guid.NewGuid();
        var artistMbid = Guid.NewGuid();
        NormalizedEvidenceReference At(string pointer) => new(input, pointer);
        ObservedValue<string> Value(string text, string pointer) => new(text, At(pointer));
        MusicBrainzEntityReference Entity(string kind, Guid uuid, string name, string pointer) =>
            new()
            {
                Evidence = At(pointer),
                Identity = new(new("musicbrainz.org", kind, uuid.ToString()), At(pointer + "/id")),
                Name = Value(name, pointer + (kind == "artist" ? "/name" : "/title")),
            };
        var work = new MusicBrainzWorkObservation
        {
            Origin = new(
                new("musicbrainz.org", "work", workMbid.ToString()),
                At(""),
                ProviderObservationScope.ResourceItem
            ),
            Work = Entity(
                "work",
                fault == "work_identity_differs" ? Guid.NewGuid() : workMbid,
                "Song",
                ""
            ),
        };
        var relation = new MusicBrainzRelationshipObservation
        {
            Evidence = At("/relations/0"),
            Type = Value(
                fault == "wrong_relation_type" ? "cover" : "performance",
                "/relations/0/type"
            ),
            Direction = Value(
                fault == "backward_relationship" ? "backward" : "forward",
                "/relations/0/direction"
            ),
            TargetType = Value(
                fault == "wrong_target_type" ? "artist" : "work",
                "/relations/0/target-type"
            ),
            Target = Entity("work", workMbid, "Song", "/relations/0/work"),
        };
        var recording = new MusicBrainzRecordingObservation
        {
            Origin = new(
                new("musicbrainz.org", "recording", Guid.NewGuid().ToString()),
                At(""),
                ProviderObservationScope.ResourceItem
            ),
            Recording = Entity(
                "recording",
                Guid.NewGuid(),
                fault == "recording_title_differs" ? "Other song" : "Song",
                ""
            ),
            ArtistCredits =
            [
                new()
                {
                    Evidence = At("/artist-credit/0"),
                    Artist = Entity(
                        "artist",
                        fault == "wrong_performer" ? Guid.NewGuid() : artistMbid,
                        "Performer",
                        "/artist-credit/0/artist"
                    ),
                },
            ],
            Relationships =
                fault == "title_only" ? []
                : fault == "alternative_work_relationship"
                    ?
                    [
                        relation,
                        relation with
                        {
                            Target = Entity("work", Guid.NewGuid(), "Song", "/relations/1/work"),
                        },
                    ]
                : [relation],
        };
        return new(
            Guid.NewGuid(),
            Value("Song", "/text"),
            new(Guid.NewGuid(), "Performer", fault == "missing_catalog_mbid" ? null : artistMbid),
            [new(workMbid, work, [recording], [])],
            true,
            false,
            [],
            CompositionResolutionPolicyEvaluator.ProcessorVersion,
            CompositionResolutionPolicyEvaluator.FactsVersion,
            MusicBrainzCandidateQueryBuilder.Version,
            NormalizationProviderDecoder.Version
        );
    }
}
