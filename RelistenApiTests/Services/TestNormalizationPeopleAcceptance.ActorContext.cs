using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationPeopleAcceptance
{
    [Test]
    public async Task AcceptedActorContextBindsOriginalProposalToActualContributorSupportAndReplays()
    {
        var name = UniqueName();
        var alias = UniqueName();
        var person = await Person(name);
        var source = await Source(
            $"{name} aka {alias} (fixture@example.test) (https://archive.org/details/@fixture_actor)"
        );
        await ActivatePolicy();
        var resolution = await Resolve(source);

        var application = await applier.ApplyRecordingAsync(Guid.NewGuid(), resolution.AttemptUuid);

        application
            .Result!.Disposition.Should()
            .Be(
                PeopleAcceptanceDisposition.Applied,
                "application audit: {0}",
                ApplicationAudit(application)
            );
        var context = (await people.GetRecordingActorContextsAsync(source.SourceUuid))
            .Should()
            .ContainSingle()
            .Which;
        var proposal = resolution.Preparation!.Inputs.Single().Claims.Single().Proposal;
        var credit = (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Single();
        context.StoredPersonUuid.Should().Be(person);
        context.CreditedName.Should().Be(name);
        context.CreditUuid.Should().Be(credit.Credit.Uuid);
        context.CreditRevision.Should().Be(credit.Revision);
        context.CreditBasis.Should().Be("validated_match");
        context.ProposalUuid.Should().Be(proposal.ProposalUuid);
        context.ExtractionAttemptUuid.Should().Be(resolution.ExtractionAttemptUuid);
        context.ApplicationAttemptUuid.Should().Be(application.AttemptUuid);
        context
            .Contributor.Should()
            .Be(resolution.Preparation.Inputs.Single().OriginalInput.Contributor);
        context.Facts.Should().BeEquivalentTo(proposal.Mention.ActorFacts);
        context.Facts.Aliases.Should().Contain(value => value.Wording == alias);
        context.Facts.Contacts.Should().Contain(value => value.LookupKey == "fixture@example.test");
        context.Facts.Profiles.Should().Contain(value => value.LookupKey == "fixture_actor");
        var supports = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<int>(
                "SELECT count(*) FROM normalization.recording_credit_contributor_support WHERE uuid=@SupportUuid AND credit_uuid=@CreditUuid AND retired_work_uuid IS NULL",
                context
            )
        );
        supports.Should().Be(1);

        (await applier.ApplyRecordingAsync(application.AttemptUuid, resolution.AttemptUuid))
            .Result!.Replayed.Should()
            .BeTrue();
        (await people.GetRecordingActorContextsAsync(source.SourceUuid))
            .Should()
            .ContainSingle()
            .Which.ContextUuid.Should()
            .Be(context.ContextUuid);
    }

    [Test]
    public async Task UnknownCurrentCreditPreservesContextAndExplicitWithdrawalRetiresOnlyItsIndexes()
    {
        var name = UniqueName();
        await Person(name);
        await ActivatePolicy();
        var source = await Source($"{name} (fixture@example.test)");
        var initial = await Resolve(source);
        await applier.ApplyRecordingAsync(Guid.NewGuid(), initial.AttemptUuid);
        var original = (await people.GetRecordingActorContextsAsync(source.SourceUuid))
            .Should()
            .ContainSingle()
            .Which;

        await SetTaper(source, "unknown");
        var unresolved = await Resolve(source);
        var held = await applier.ApplyRecordingAsync(Guid.NewGuid(), unresolved.AttemptUuid);
        held.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Unresolved);
        (await people.GetRecordingActorContextsAsync(source.SourceUuid))
            .Should()
            .ContainSingle()
            .Which.ContextUuid.Should()
            .Be(original.ContextUuid);

        await SetTaper(source, null);
        var cleared = await Resolve(source);
        var withdrawal = await applier.ApplyRecordingAsync(Guid.NewGuid(), cleared.AttemptUuid);
        withdrawal.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Applied);
        (await people.GetRecordingActorContextsAsync(source.SourceUuid)).Should().BeEmpty();
        var history = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<RetiredActorContext>(
                """
                SELECT facts::text AS FactsJson,retired_work_uuid AS RetiredWorkUuid,
                    (SELECT count(*) FROM normalization.recording_actor_context_aliases WHERE context_uuid=actor.uuid)
                    +(SELECT count(*) FROM normalization.recording_actor_context_contacts WHERE context_uuid=actor.uuid)
                    +(SELECT count(*) FROM normalization.recording_actor_context_profiles WHERE context_uuid=actor.uuid) AS LookupRows
                FROM normalization.recording_actor_contexts actor WHERE uuid=@ContextUuid
                """,
                original
            )
        );
        history.RetiredWorkUuid.Should().NotBeNull();
        history.LookupRows.Should().Be(0);
        JsonSerializer
            .Deserialize<CreditedActorFacts<NormalizedEvidenceReference>>(history.FactsJson)
            .Should()
            .BeEquivalentTo(original.Facts);
    }

    [Test]
    public async Task ClearingArchiveContributionPreservesTheIndependentLocalActorAccount()
    {
        var name = UniqueName();
        await Person(name);
        await ActivatePolicy();
        var source = await Source($"{name} (local_fixture@example.test)");
        var local = await Resolve(source);
        await applier.ApplyRecordingAsync(Guid.NewGuid(), local.AttemptUuid);
        var localContext = (await people.GetRecordingActorContextsAsync(source.SourceUuid))
            .Should()
            .ContainSingle()
            .Which;
        var archive = await ExtractArchive(source, $"{name} (archive_fixture@example.test)");
        var combined = await resolver.ResolveRecordingAsync(Guid.NewGuid(), archive.AttemptUuid);
        var accepted = await applier.ApplyRecordingAsync(Guid.NewGuid(), combined.AttemptUuid);
        accepted
            .Result!.Disposition.Should()
            .Be(
                PeopleAcceptanceDisposition.Applied,
                "application audit: {0}",
                ApplicationAudit(accepted)
            );
        var contexts = await people.GetRecordingActorContextsAsync(source.SourceUuid);
        contexts.Select(value => value.Contributor).Distinct().Should().HaveCount(2);

        var clearArchive = await ExtractArchive(source, null);
        var clearing = await resolver.ResolveRecordingAsync(
            Guid.NewGuid(),
            clearArchive.AttemptUuid
        );
        var result = await applier.ApplyRecordingAsync(Guid.NewGuid(), clearing.AttemptUuid);
        result.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Applied);
        var retained = (await people.GetRecordingActorContextsAsync(source.SourceUuid))
            .Should()
            .ContainSingle()
            .Which;
        retained.Contributor.Should().Be(localContext.Contributor);
        retained
            .Facts.Contacts.Should()
            .Contain(value => value.LookupKey == "local_fixture@example.test");
        (await people.ReadCreditContextAsync(source.SourceUuid))
            .Credits.Should()
            .Contain(value => value.Credit.State == "accepted");
    }

    [Test]
    public async Task ReviewedMergePreservesOriginalActorEvidenceButUnrelatedReassignmentHidesIt()
    {
        var name = UniqueName();
        var originalPerson = await Person(name);
        var survivorName = UniqueName();
        var survivor = await Person(survivorName);
        var unrelated = await Person(UniqueName());
        await ActivatePolicy();
        var source = await Source($"{name} (fixture@example.test)");
        var resolution = await Resolve(source);
        await applier.ApplyRecordingAsync(Guid.NewGuid(), resolution.AttemptUuid);
        var context = (await people.GetRecordingActorContextsAsync(source.SourceUuid))
            .Should()
            .ContainSingle()
            .Which;
        var personOwner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", originalPerson)
            )
        )!;
        var originalFacts = (
            await candidates.FindPeopleFactsAsync(new(IdentityMatchingDomain.People, [name], []))
        ).Facts.Single(value => value.Hit.Uuid == originalPerson);
        var survivorFacts = (
            await candidates.FindPeopleFactsAsync(
                new(IdentityMatchingDomain.People, [survivorName], [])
            )
        ).Facts.Single(value => value.Hit.Uuid == survivor);
        await people.ApplyIdentityDecisionAsync(
            new(
                Guid.NewGuid(),
                personOwner.SubjectUuid,
                personOwner.EditorialRevision,
                "merge",
                originalPerson,
                originalFacts.Header.Revision,
                new(survivor, survivorName),
                survivorFacts.Header.Revision,
                [context.CreditUuid],
                "Fixture documents one person under two identities"
            ),
            Operator
        );
        var afterMerge = (await people.GetRecordingActorContextsAsync(source.SourceUuid))
            .Should()
            .ContainSingle()
            .Which;
        afterMerge.ContextUuid.Should().Be(context.ContextUuid);
        afterMerge.StoredPersonUuid.Should().Be(originalPerson);
        var credit = (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Single();
        credit.Credit.PersonUuid.Should().Be(survivor);
        var recordingOwner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "recording", source.SourceUuid)
            )
        )!;
        await people.ApplyDecisionAsync(
            new(
                Guid.NewGuid(),
                recordingOwner.SubjectUuid,
                recordingOwner.EditorialRevision,
                credit.Revision,
                "replace",
                credit.Credit with
                {
                    PersonUuid = unrelated,
                },
                "Fixture corrects an unrelated attribution"
            ),
            Operator
        );

        (await people.GetRecordingActorContextsAsync(source.SourceUuid)).Should().BeEmpty();
        (
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM normalization.recording_actor_contexts WHERE uuid=@ContextUuid AND retired_work_uuid IS NULL)",
                    context
                )
            )
        )
            .Should()
            .BeTrue("reassignment changes applicability, not immutable contributor history");
    }

    [Test]
    public async Task UnmanagedAndPendingCreditsCannotInventAcceptedActorContext()
    {
        var name = UniqueName();
        var person = await Person(name);
        var source = await fixture.Source();
        var evidence = await fixture.Capture(name, "people");
        await fixture.PublishPeople(
            people,
            evidence,
            new(
                [],
                [],
                [],
                [
                    new(
                        Guid.NewGuid(),
                        source.SourceUuid,
                        "taper",
                        name,
                        person,
                        null,
                        [evidence.Reference]
                    ),
                ]
            )
        );
        (await people.GetRecordingActorContextsAsync(source.SourceUuid)).Should().BeEmpty();
        var pending = await fixture.Source();
        await fixture.PublishPeople(
            people,
            evidence,
            new(
                [],
                [],
                [],
                [
                    new(
                        Guid.NewGuid(),
                        pending.SourceUuid,
                        "taper",
                        name,
                        person,
                        null,
                        [evidence.Reference],
                        State: "pending"
                    ),
                ]
            )
        );
        (await people.GetRecordingActorContextsAsync(pending.SourceUuid)).Should().BeEmpty();
    }

    [Test]
    public async Task ActorContextAndCreditRollbackWithTheirReceiptThenResumeExactlyOnce()
    {
        var name = UniqueName();
        await Person(name);
        await ActivatePolicy();
        var source = await Source($"{name} (rollback_fixture@example.test)");
        var resolution = await Resolve(source);
        var attempt = Guid.NewGuid();
        var triggerName = "actor_context_receipt_failure_" + Guid.NewGuid().ToString("N");
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                $"""
                CREATE FUNCTION normalization.{triggerName}() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.application_uuid='{attempt:D}'::uuid THEN
                        RAISE EXCEPTION 'synthetic actor context receipt failure';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER {triggerName} BEFORE UPDATE OF receipt ON normalization.people_acceptance_stages
                    FOR EACH ROW EXECUTE FUNCTION normalization.{triggerName}();
                """
            )
        );
        try
        {
            var publish = () => applier.ApplyRecordingAsync(attempt, resolution.AttemptUuid);
            await publish.Should().ThrowAsync<Exception>();
            (await people.GetRecordingActorContextsAsync(source.SourceUuid)).Should().BeEmpty();
            (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().BeEmpty();
            var saved = (await applier.GetApplicationAsync(attempt))!;
            saved.Stages.Single().Receipt.Should().BeNull();
            (
                await fixture.Work.GetSubjectAsync(
                    new NormalizationSubjectKey("people", "recording", source.SourceUuid)
                )
            )!
                .PublishedWorkUuid.Should()
                .BeNull();
        }
        finally
        {
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    $"""
                    DROP TRIGGER {triggerName} ON normalization.people_acceptance_stages;
                    DROP FUNCTION normalization.{triggerName}();
                    """
                )
            );
        }
        var recovered = await applier.ApplyRecordingAsync(attempt, resolution.AttemptUuid);
        recovered
            .Result!.Disposition.Should()
            .Be(
                PeopleAcceptanceDisposition.Applied,
                "application audit: {0}",
                ApplicationAudit(recovered)
            );
        var context = (await people.GetRecordingActorContextsAsync(source.SourceUuid))
            .Should()
            .ContainSingle()
            .Which;
        (await applier.ApplyRecordingAsync(attempt, resolution.AttemptUuid))
            .Result!.Replayed.Should()
            .BeTrue();
        (await people.GetRecordingActorContextsAsync(source.SourceUuid))
            .Should()
            .ContainSingle()
            .Which.ContextUuid.Should()
            .Be(context.ContextUuid);
    }

    private sealed record RetiredActorContext(
        string FactsJson,
        Guid? RetiredWorkUuid,
        long LookupRows
    );
}
