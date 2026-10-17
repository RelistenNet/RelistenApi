using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;
using DomainInput = RelistenApiTests.Services.NormalizationDomainFixture.DomainInput;

namespace RelistenApiTests.Services;

/// <summary>
/// A fresh normalization run can rediscover the same assertion. Only changed assertions or
/// new citations should change accepted credit facts and their candidate-pool clock.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationCreditIdempotence
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizedPeopleService people = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        people = new(fixture.Db, fixture.Work);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [TestCase(false)]
    [TestCase(true)]
    public async Task FreshWorkKeepsUnchangedAssertionAndExactEvidence(bool freshCreditUuid)
    {
        var scenario = await Setup();
        var before = await Head(scenario.Credit.Uuid);
        var epoch = await PeopleEpoch();
        var credit = freshCreditUuid
            ? scenario.Credit with
            {
                Uuid = Guid.NewGuid(),
            }
            : scenario.Credit;
        var first = await Publish(
            scenario,
            credit with
            {
                Evidence = [scenario.Input.Reference, scenario.Input.Reference],
            }
        );
        var second = await Publish(scenario, credit);

        second.WorkUuid.Should().NotBe(first.WorkUuid);
        (await Head(scenario.Credit.Uuid)).Should().Be(before);
        (await CreditCount(scenario.Source.SourceUuid)).Should().Be(1);
        (await HistoryCount(scenario.Credit.Uuid)).Should().Be(0);
        (await Evidence(scenario.Credit.Uuid)).Should().Equal(scenario.Input.Reference);
        (await PeopleEpoch()).Should().Be(epoch);
    }

    [Test]
    public async Task NewFullCitationTuplesAppendToCurrentRevisionAndThenConverge()
    {
        var scenario = await Setup();
        var before = await Head(scenario.Credit.Uuid);
        var epoch = await PeopleEpoch();
        var revision = scenario.Input.Reference.InputRevisionUuid;
        var citations = new NormalizedEvidenceReference[]
        {
            scenario.Input.Reference,
            new(revision, "/first", 0, 12, "Pat Recorder"),
            new(revision, "/first", 14, 26, "Pat Recorder"),
            new(revision, "/second", 0, 12, "Pat Recorder"),
            new(revision, "/first", 0, 26, "Pat Recorder; Pat Recorder"),
        };

        await Publish(
            scenario,
            scenario.Credit with
            {
                Uuid = Guid.NewGuid(),
                Evidence = citations.Concat(citations).ToArray(),
            }
        );

        (await Head(scenario.Credit.Uuid)).Should().Be(before);
        (await HistoryCount(scenario.Credit.Uuid)).Should().Be(0);
        (await Evidence(scenario.Credit.Uuid)).Should().BeEquivalentTo(citations);
        var enrichedEpoch = await PeopleEpoch();
        enrichedEpoch.Should().BeGreaterThan(epoch);
        await Publish(scenario, scenario.Credit with { Evidence = citations });
        (await PeopleEpoch()).Should().Be(enrichedEpoch);
        (await Evidence(scenario.Credit.Uuid)).Should().HaveCount(citations.Length);
    }

    [Test]
    public async Task NewInputRevisionAddsEvidenceWithoutInventingAnAssertionRevision()
    {
        var scenario = await Setup();
        var before = await Head(scenario.Credit.Uuid);
        var epoch = await PeopleEpoch();
        var refreshed = scenario with
        {
            Input = await fixture.Refresh(
                scenario.Input,
                "Pat Recorder, independently retained credit"
            ),
        };
        var repeated = scenario.Credit with
        {
            Uuid = Guid.NewGuid(),
            Evidence = [refreshed.Input.Reference],
        };

        await Publish(refreshed, repeated);

        (await Head(scenario.Credit.Uuid)).Should().Be(before);
        (await HistoryCount(scenario.Credit.Uuid)).Should().Be(0);
        (await Evidence(scenario.Credit.Uuid))
            .Should()
            .BeEquivalentTo(new[] { scenario.Input.Reference, refreshed.Input.Reference });
        var enrichedEpoch = await PeopleEpoch();
        enrichedEpoch.Should().BeGreaterThan(epoch);
        await Publish(refreshed, repeated with { Uuid = Guid.NewGuid() });
        (await PeopleEpoch()).Should().Be(enrichedEpoch);
    }

    [Test]
    public async Task DuplicateAcceptedPersonAddsOnlyMissingEvidenceAndKeepsOriginalWording()
    {
        var scenario = await Setup();
        var before = await Head(scenario.Credit.Uuid);
        var epoch = await PeopleEpoch();
        var alternate = scenario.Credit with
        {
            Uuid = Guid.NewGuid(),
            OriginalText = "P. Recorder",
        };
        await Publish(scenario, alternate);
        (await Head(scenario.Credit.Uuid)).Should().Be(before);
        (await PeopleEpoch()).Should().Be(epoch);
        var citation = new NormalizedEvidenceReference(
            scenario.Input.Reference.InputRevisionUuid,
            "/second"
        );

        await Publish(scenario, alternate with { Evidence = [citation, citation] });

        (await CreditCount(scenario.Source.SourceUuid)).Should().Be(1);
        (await Head(scenario.Credit.Uuid)).Should().Be(before);
        (await HistoryCount(scenario.Credit.Uuid)).Should().Be(0);
        (await Evidence(scenario.Credit.Uuid))
            .Should()
            .BeEquivalentTo(new[] { scenario.Input.Reference, citation });
        var enrichedEpoch = await PeopleEpoch();
        enrichedEpoch.Should().BeGreaterThan(epoch);
        await Publish(scenario, alternate with { Uuid = Guid.NewGuid(), Evidence = [citation] });
        (await PeopleEpoch()).Should().Be(enrichedEpoch);
    }

    [Test]
    public async Task PendingClaimRetainsNewLocatorsInTheSameInputWithoutMultiplyingClaims()
    {
        var scenario = await Setup("pending");
        var before = await Head(scenario.Credit.Uuid);
        var citation = new NormalizedEvidenceReference(
            scenario.Input.Reference.InputRevisionUuid,
            "/second"
        );
        var epoch = await PeopleEpoch();

        await Publish(
            scenario,
            scenario.Credit with
            {
                Uuid = Guid.NewGuid(),
                Evidence = [citation],
            }
        );

        (await CreditCount(scenario.Source.SourceUuid)).Should().Be(1);
        (await Head(scenario.Credit.Uuid)).Should().Be(before);
        (await Evidence(scenario.Credit.Uuid))
            .Should()
            .BeEquivalentTo(new[] { scenario.Input.Reference, citation });
        (await HistoryCount(scenario.Credit.Uuid)).Should().Be(0);
        var enrichedEpoch = await PeopleEpoch();
        enrichedEpoch.Should().BeGreaterThan(epoch);
        await Publish(
            scenario,
            scenario.Credit with
            {
                Uuid = Guid.NewGuid(),
                Evidence = [citation],
            }
        );
        (await PeopleEpoch()).Should().Be(enrichedEpoch);
    }

    [TestCase("role")]
    [TestCase("wording")]
    [TestCase("person")]
    [TestCase("state")]
    [TestCase("basis")]
    [TestCase("reason")]
    public async Task ChangedAssertionStillCreatesOneRevisionAndRetainsHistory(string field)
    {
        var scenario = await Setup();
        var before = await Head(scenario.Credit.Uuid);
        var secondPerson =
            field == "person" ? await CreatePerson("Other Recorder") : scenario.Person;
        var changed = field switch
        {
            "role" => scenario.Credit with { Role = "transferrer" },
            "wording" => scenario.Credit with { OriginalText = "Pat Recorder (full credit)" },
            "person" => scenario.Credit with { PersonUuid = secondPerson.Uuid },
            "state" => scenario.Credit with { State = "pending" },
            "basis" => scenario.Credit with { Basis = "validated_match" },
            _ => scenario.Credit with { ReasonCode = "explicit_named_credit" },
        };
        var epoch = await PeopleEpoch();

        await Publish(scenario, changed);

        var after = await Head(scenario.Credit.Uuid);
        after.Revision.Should().Be(before.Revision + 1);
        after.PersonUuid.Should().Be(changed.PersonUuid);
        after.Role.Should().Be(changed.Role);
        after.OriginalText.Should().Be(changed.OriginalText);
        after.State.Should().Be(changed.State);
        after.Basis.Should().Be(changed.Basis);
        after.ReasonCode.Should().Be(changed.ReasonCode);
        (await HistoryCount(scenario.Credit.Uuid)).Should().Be(1);
        (await PeopleEpoch()).Should().BeGreaterThan(epoch);
        var changedEpoch = await PeopleEpoch();
        await Publish(scenario, changed with { Uuid = Guid.NewGuid() });
        (await Head(scenario.Credit.Uuid)).Should().Be(after);
        (await PeopleEpoch()).Should().Be(changedEpoch);
    }

    [TestCase("source")]
    [TestCase("track")]
    public async Task ExistingCreditUuidCannotMoveToAnotherRecordingScope(string change)
    {
        var scenario = await Setup();
        var before = await Head(scenario.Credit.Uuid);
        var other = change == "source" ? await fixture.Source() : scenario.Source;
        var proposed = scenario.Credit with
        {
            SourceUuid = other.SourceUuid,
            ScopeTrackUuid = change == "track" ? scenario.Source.TrackUuid : null,
        };
        var attempt = scenario with { Source = other };
        var epoch = await PeopleEpoch();

        Func<Task> publish = () => Publish(attempt, proposed);
        await publish
            .Should()
            .ThrowAsync<NormalizationConflictException>()
            .WithMessage("*different recording scope*");

        (await Head(scenario.Credit.Uuid)).Should().Be(before);
        (await HistoryCount(scenario.Credit.Uuid)).Should().Be(0);
        (await PeopleEpoch()).Should().Be(epoch);
    }

    [TestCase("source_claim")]
    [TestCase("source_person")]
    [TestCase("track_claim")]
    public async Task ExistingTargetClaimCannotHideRebindingTheSuppliedCreditUuid(string target)
    {
        var scenario = await Setup();
        var source = target == "track_claim" ? scenario.Source : await fixture.Source();
        var targetCredit = scenario.Credit with
        {
            Uuid = Guid.NewGuid(),
            SourceUuid = source.SourceUuid,
            ScopeTrackUuid = target == "track_claim" ? source.TrackUuid : null,
            OriginalText = target == "source_person" ? "P. Recorder" : scenario.Credit.OriginalText,
        };
        var destination = scenario with { Source = source };
        await Publish(destination, targetCredit);
        var originalHead = await Head(scenario.Credit.Uuid);
        var targetHead = await Head(targetCredit.Uuid);
        var epoch = await PeopleEpoch();
        var moved = scenario.Credit with
        {
            SourceUuid = source.SourceUuid,
            ScopeTrackUuid = targetCredit.ScopeTrackUuid,
        };

        Func<Task> publish = () => Publish(destination, moved);
        await publish
            .Should()
            .ThrowAsync<NormalizationConflictException>()
            .WithMessage("*different recording scope*");

        (await Head(scenario.Credit.Uuid)).Should().Be(originalHead);
        (await Head(targetCredit.Uuid)).Should().Be(targetHead);
        (await Evidence(targetCredit.Uuid)).Should().Equal(scenario.Input.Reference);
        (await HistoryCount(scenario.Credit.Uuid)).Should().Be(0);
        (await HistoryCount(targetCredit.Uuid)).Should().Be(0);
        (await PeopleEpoch()).Should().Be(epoch);
    }

    [TestCase("accept")]
    [TestCase("reject")]
    public async Task ReviewedClaimStaysProtectedAndChangedEvidenceRemainsPending(string action)
    {
        var scenario = await Setup();
        var owner = (await fixture.Work.GetSubjectAsync(Owner(scenario.Source.SourceUuid)))!;
        await people.ApplyDecisionAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                1,
                action,
                scenario.Credit,
                "Reviewed credit fixture"
            ),
            "test"
        );
        var reviewed = await Head(scenario.Credit.Uuid);
        var epoch = await PeopleEpoch();
        await Publish(scenario, scenario.Credit with { Uuid = Guid.NewGuid() });
        (await Head(scenario.Credit.Uuid)).Should().Be(reviewed);
        (await PeopleEpoch()).Should().Be(epoch);
        var refreshed = scenario with
        {
            Input = await fixture.Refresh(scenario.Input, "Pat Recorder, a newly retained credit"),
        };
        var incoming = scenario.Credit with
        {
            Uuid = Guid.NewGuid(),
            Evidence = [refreshed.Input.Reference],
        };

        await Publish(refreshed, incoming);

        (await Head(scenario.Credit.Uuid)).Should().Be(reviewed);
        (await CountState(scenario.Source.SourceUuid, "pending")).Should().Be(1);
        (await CountState(scenario.Source.SourceUuid, "accepted"))
            .Should()
            .Be(action == "accept" ? 1 : 0);
        var pendingEpoch = await PeopleEpoch();
        await Publish(refreshed, incoming with { Uuid = Guid.NewGuid() });
        (await CreditCount(scenario.Source.SourceUuid)).Should().Be(2);
        (await PeopleEpoch()).Should().Be(pendingEpoch);
        (await Head(scenario.Credit.Uuid)).Should().Be(reviewed);
    }

    [Test]
    public async Task TwoCandidateConsumersConvergeWhenBothRediscoverUnchangedCredits()
    {
        var first = await Setup();
        var secondSource = await fixture.Source();
        var second = first with
        {
            Source = secondSource,
            Credit = first.Credit with
            {
                Uuid = Guid.NewGuid(),
                SourceUuid = secondSource.SourceUuid,
            },
        };
        await Publish(second, second.Credit);
        var before = await PeopleEpoch();
        var candidate = new NormalizationCandidateUniverseDependency(
            NormalizationCandidateUniverseKey.People,
            before
        );
        var firstWork = await Begin(first, first.Credit, [candidate]);
        var secondWork = await Begin(second, second.Credit, [candidate]);

        (
            await people.PublishAsync(
                firstWork.WorkUuid,
                new([], [], [], [first.Credit with { Uuid = Guid.NewGuid() }])
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        (
            await people.PublishAsync(
                secondWork.WorkUuid,
                new([], [], [], [second.Credit with { Uuid = Guid.NewGuid() }])
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);

        (await PeopleEpoch()).Should().Be(before);
        (await Pending(firstWork.SubjectUuid)).Should().BeFalse();
        (await Pending(secondWork.SubjectUuid)).Should().BeFalse();
        (await Head(first.Credit.Uuid)).Revision.Should().Be(1);
        (await Head(second.Credit.Uuid)).Revision.Should().Be(1);
    }

    private async Task<Scenario> Setup(string state = "accepted")
    {
        var input = await fixture.CaptureJson(
            """{"first":"Pat Recorder; Pat Recorder","second":"Pat Recorder; Pat Recorder"}""",
            "people",
            "/first"
        );
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat Recorder");
        await fixture.PublishPeople(people, input, new([person], [], [], []));
        var source = await fixture.Source();
        var scenario = new Scenario(
            input,
            source,
            person,
            new(
                Guid.NewGuid(),
                source.SourceUuid,
                "taper",
                "Pat Recorder",
                person.Uuid,
                null,
                [input.Reference],
                State: state
            )
        );
        await Publish(scenario, scenario.Credit);
        return scenario;
    }

    private async Task<CanonicalPerson> CreatePerson(string name)
    {
        var input = await fixture.Capture(name, "people");
        var person = new CanonicalPerson(Guid.NewGuid(), name);
        await fixture.PublishPeople(people, input, new([person], [], [], []));
        return person;
    }

    private async Task<NormalizationWorkTicket> Begin(
        Scenario scenario,
        RecordingCredit credit,
        IReadOnlyList<NormalizationCandidateUniverseDependency>? candidates = null
    )
    {
        var subject = Owner(scenario.Source.SourceUuid);
        var current = await fixture.Work.GetSubjectAsync(subject);
        var dependencies = credit.PersonUuid.HasValue
            ? new[] { await fixture.OwnerUuid("people", "person", credit.PersonUuid.Value) }
            : [];
        return await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            "policy-1",
            [scenario.Input.Resource],
            dependencies,
            current?.DesiredGeneration,
            candidateUniverses: candidates
        );
    }

    private async Task<NormalizationWorkTicket> Publish(Scenario scenario, RecordingCredit credit)
    {
        var ticket = await Begin(scenario, credit);
        (await people.PublishAsync(ticket.WorkUuid, new([], [], [], [credit])))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        return ticket;
    }

    private Task<CreditHead> Head(Guid uuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<CreditHead>(
                """
                SELECT uuid AS "Uuid",revision AS "Revision",person_uuid AS "PersonUuid",role AS "Role",original_text AS "OriginalText",
                    state AS "State",basis AS "Basis",reason_code AS "ReasonCode",decision_uuid AS "DecisionUuid",updated_at AS "UpdatedAt"
                FROM normalization.recording_credits WHERE uuid=@uuid
                """,
                new { uuid }
            )
        );

    private Task<NormalizedEvidenceReference[]> Evidence(Guid uuid) =>
        fixture.Db.WithWriteConnection(async c =>
            (
                await c.QueryAsync<NormalizedEvidenceReference>(
                    """
                    SELECT input_revision_uuid AS "InputRevisionUuid",json_pointer AS "JsonPointer",start_code_point AS "StartCodePoint",end_code_point AS "EndCodePoint",quote AS "Quote"
                    FROM normalization.recording_credit_evidence WHERE credit_uuid=@uuid AND credit_revision=(SELECT revision FROM normalization.recording_credits WHERE uuid=@uuid)
                    """,
                    new { uuid }
                )
            ).ToArray()
        );

    private Task<long> CreditCount(Guid sourceUuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM normalization.recording_credits WHERE source_uuid=@sourceUuid",
                new { sourceUuid }
            )
        );

    private Task<long> CountState(Guid sourceUuid, string state) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM normalization.recording_credits WHERE source_uuid=@sourceUuid AND state=@state",
                new { sourceUuid, state }
            )
        );

    private Task<long> HistoryCount(Guid uuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM normalization.recording_credit_history WHERE uuid=@uuid",
                new { uuid }
            )
        );

    private Task<long> PeopleEpoch() =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT generation FROM normalization.candidate_universe_revisions WHERE domain='people' AND scope_uuid=@empty",
                new { empty = Guid.Empty }
            )
        );

    private async Task<bool> Pending(Guid subjectUuid)
    {
        Guid? after = null;
        for (var pages = 0; pages < 1000; pages++)
        {
            var page = await fixture.Work.GetPendingSubjectsAsync(after, 1000);
            if (page.Any(row => row.SubjectUuid == subjectUuid))
            {
                return true;
            }
            if (page.Count < 1000)
            {
                return false;
            }
            after = page[^1].SubjectUuid;
        }
        throw new AssertionException("Pending work pagination did not terminate.");
    }

    private static NormalizationSubjectKey Owner(Guid source) => new("people", "recording", source);

    private sealed record Scenario(
        DomainInput Input,
        CatalogSource Source,
        CanonicalPerson Person,
        RecordingCredit Credit
    );

    private sealed record CreditHead(
        Guid Uuid,
        long Revision,
        Guid? PersonUuid,
        string Role,
        string OriginalText,
        string State,
        string Basis,
        string ReasonCode,
        Guid DecisionUuid,
        DateTime UpdatedAt
    );
}
