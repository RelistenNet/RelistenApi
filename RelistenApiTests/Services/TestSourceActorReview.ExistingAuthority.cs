using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestSourceActorReview
{
    [Test]
    public async Task PositivePreparationRequiresTheOriginalProducerCatalogContextToRemainCurrent()
    {
        var source = await Source();
        var target = await Person("Current catalog target " + Guid.NewGuid());
        var extractionOwner = (
            await fixture.Work.GetSubjectAsync(source.Extraction.Ticket.SubjectUuid)
        )!;
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET description=@description WHERE id=@SourceId",
                new
                {
                    source.Source.SourceId,
                    description = "The catalog now describes a different personnel field.",
                }
            )
        );

        var preparePositive = () => Prepare(source, SourceActorReviewAction.Accept, target);

        await AssertConflict(preparePositive);
        // No new retained capture or extraction was published. Current producer head IDs
        // alone cannot establish that the catalog facts used to bind that producer still hold.
        (await fixture.Work.GetSubjectAsync(source.Extraction.Ticket.SubjectUuid))
            .Should()
            .BeEquivalentTo(extractionOwner);
        var negative = await Prepare(source, SourceActorReviewAction.Reject);
        negative.ReadSet.Inputs.Should().BeEquivalentTo(source.Extraction.Ticket.Inputs);
        negative.OriginalField.Coordinates.Should().Be(source.Claim);
        await reviews.ApplyAsync(Guid.NewGuid(), negative.PreparationUuid, Reason, Operator);
        (await Decision(negative.ReviewStateUuid)).State.Should().Be("rejected");
    }

    [TestCase(SourceActorReviewAction.Reject, "rejected")]
    [TestCase(SourceActorReviewAction.Retract, "retracted")]
    public async Task ExplicitRestorationUsesTheHumanStateWhileEarlierMachineSupportRemainsRetained(
        SourceActorReviewAction negativeAction,
        string negativeState
    )
    {
        var source = await Source();
        var person = await Person("Machine assigned target " + Guid.NewGuid());
        var authority = await SeedMachineAuthority(source, person);
        var before = await AcceptedState();
        var negative = await Prepare(source, negativeAction);
        negative.PriorReview.Should().BeNull();
        negative.PriorAssignedPersonUuid.Should().Be(person.Uuid);

        await reviews.ApplyAsync(Guid.NewGuid(), negative.PreparationUuid, Reason, Operator);

        (await Decision(negative.ReviewStateUuid)).State.Should().Be(negativeState);
        (await AcceptedState())
            .Should()
            .Be(
                before,
                "source review retains physical authority until the accepted application phase retires it"
            );
        var restoration = await Prepare(source, SourceActorReviewAction.Accept, person);
        restoration.PriorReview!.State.Should().Be(negativeState);
        restoration.PriorAssignedPersonUuid.Should().Be(person.Uuid);
        await reviews.ApplyAsync(Guid.NewGuid(), restoration.PreparationUuid, Reason, Operator);

        (await History(restoration.ReviewStateUuid))
            .Select(row => row.State)
            .Should()
            .Equal(negativeState, "accepted");
        (await Decision(restoration.ReviewStateUuid)).SelectedPersonUuid.Should().Be(person.Uuid);
        (await AcceptedState()).Should().Be(before);
        (
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<int>(
                    """
                    SELECT count(*) FROM normalization.source_actor_alias_assignments assignment
                    JOIN normalization.person_alias_contributor_support support ON support.assignment_uuid=assignment.uuid
                    WHERE assignment.uuid=@AssignmentUuid AND support.uuid=@SupportUuid
                        AND assignment.retired_work_uuid IS NULL AND support.retired_work_uuid IS NULL
                    """,
                    authority
                )
            )
        ).Should().Be(1);
    }

    [Test]
    public async Task PreparationStorageRefusesOversizedProofWithoutTruncatingOrKeepingAPartialRow()
    {
        var source = await Source();
        var prepared = await Prepare(source, SourceActorReviewAction.Reject);
        var oversizedUuid = Guid.NewGuid();
        // Exercise the database contract independently of the trusted writer's encoded-size
        // check. Build the payload on the server to avoid transferring a 32 MiB fixture.
        var insert = () =>
            fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    """
                    INSERT INTO normalization.source_actor_review_preparations(
                        preparation_uuid,source_uuid,source_owner_uuid,extraction_attempt_uuid,
                        processing_version,request_hash,request,preparation)
                    SELECT @oversizedUuid,source_uuid,source_owner_uuid,extraction_attempt_uuid,
                        processing_version,request_hash,request,
                        preparation || jsonb_build_object('OversizedFixture',repeat('x',33554433))
                    FROM normalization.source_actor_review_preparations WHERE preparation_uuid=@originalUuid
                    """,
                    new { oversizedUuid, originalUuid = prepared.PreparationUuid }
                )
            );

        var failure = await insert.Should().ThrowAsync<Exception>();

        HasCheckConstraintRejection(failure.Which).Should().BeTrue();
        (await store.GetPreparationAsync(oversizedUuid)).Should().BeNull();
        (await store.GetPreparationAsync(prepared.PreparationUuid))
            .Should()
            .BeEquivalentTo(prepared);
    }

    // This fixture supplies migration 53 authority for an already authenticated producer
    // claim. Its receipt is explicitly synthetic; no machine policy acceptance is exercised.
    // Public review must still use these original claim coordinates and its own trusted receipt.
    private async Task<SeededAuthority> SeedMachineAuthority(
        SourceFixture source,
        PersonFixture person
    )
    {
        var owner = (await fixture.Work.GetSubjectAsync(SourceOwner(source.Source.SourceUuid)))!;
        var input = await fixture.Capture("Fixture machine authority", "people");
        var ticket = (await fixture.Own(input, "person", person.Uuid)).Ticket;
        var coordinates = source.Claim;
        var scope = source.Extraction.Extraction.SourceActorAliases!.Single(value =>
            value.Extraction.Assertions.Any(assertion =>
                assertion.AssertionUuid == coordinates.AssertionUuid
            )
        );
        var assertion = scope.Extraction.Assertions.Single(value =>
            value.AssertionUuid == coordinates.AssertionUuid
        );
        var authority = new SeededAuthority(Guid.NewGuid(), Guid.NewGuid());
        var publication = await fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            (c, t, ct) =>
                c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO normalization.source_actor_alias_assignments(
                            uuid,source_uuid,contributor_provider,contributor_type,contributor_key,
                            resource_uuid,input_revision_uuid,field_json_pointer,producer_version,
                            assertion_uuid,actor_reference_uuid,field_sha256,assertion_sha256,
                            original_assertion,original_scope,authority_receipt,person_uuid,
                            source_resolution_owner_uuid,extraction_owner_uuid,extraction_attempt_uuid,
                            application_attempt_uuid,stage_uuid,basis,introduced_work_uuid)
                        VALUES(@AssignmentUuid,@SourceUuid,@provider,@type,@key,@ResourceUuid,@InputRevisionUuid,
                            @FieldJsonPointer,@ProducerVersion,@AssertionUuid,@ActorReferenceUuid,@FieldSha256,
                            @assertionHash,@originalAssertion::jsonb,@originalScope::jsonb,
                            '{"FixtureOnly":true}'::jsonb,@personUuid,@sourceOwner,@extractionOwner,@extraction,
                            @application,@stage,'validated_match',@work);
                        INSERT INTO normalization.person_alias_contributor_support(
                            uuid,assignment_uuid,alias_uuid,introduced_work_uuid)
                        VALUES(@SupportUuid,@AssignmentUuid,@personUuid,@work);
                        """,
                        new
                        {
                            authority.AssignmentUuid,
                            authority.SupportUuid,
                            coordinates.SourceUuid,
                            provider = coordinates.Contributor.Provider,
                            type = coordinates.Contributor.ResourceType,
                            key = coordinates.Contributor.ProviderKey,
                            coordinates.ResourceUuid,
                            coordinates.InputRevisionUuid,
                            coordinates.FieldJsonPointer,
                            coordinates.ProducerVersion,
                            coordinates.AssertionUuid,
                            coordinates.ActorReferenceUuid,
                            coordinates.FieldSha256,
                            assertionHash = Convert.ToHexStringLower(
                                System.Security.Cryptography.SHA256.HashData(
                                    JsonSerializer.SerializeToUtf8Bytes(assertion)
                                )
                            ),
                            originalAssertion = JsonSerializer.Serialize(assertion),
                            originalScope = JsonSerializer.Serialize(scope),
                            personUuid = person.Uuid,
                            sourceOwner = owner.SubjectUuid,
                            extractionOwner = source.Extraction.Ticket.SubjectUuid,
                            extraction = source.Extraction.AttemptUuid,
                            application = Guid.NewGuid(),
                            stage = Guid.NewGuid(),
                            work = ticket.WorkUuid,
                        },
                        t,
                        cancellationToken: ct
                    )
                ),
            candidateWriteScopes: TestNormalizationSourceActorAliasAuthority.WriteScopes
        );
        publication.Status.Should().Be(NormalizationPublicationStatus.Published);
        return authority;
    }

    private static bool HasCheckConstraintRejection(Exception exception) =>
        exception is Npgsql.PostgresException { SqlState: "23514" }
        || exception.InnerException != null
            && HasCheckConstraintRejection(exception.InnerException);

    private sealed record SeededAuthority(Guid AssignmentUuid, Guid SupportUuid);
}
