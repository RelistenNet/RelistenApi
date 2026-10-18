using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

public sealed partial class TestSourceActorReview
{
    private async Task<SourceFixture> Source()
    {
        var source = await fixture.Source();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET description=@description,taper=NULL,transferrer=NULL,lineage=NULL,source=NULL,taper_notes=NULL WHERE id=@SourceId",
                new { description = "Personnel:\nReview Robin aka Finch - vocals", source.SourceId }
            )
        );
        var extracted = await ExtractSource(source);
        // Registration is enough for a human decision. A published machine resolution,
        // candidate query and acceptance policy are deliberately absent from this path.
        await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            SourceOwner(source.SourceUuid),
            "source-review-fixture-owner-v1",
            [],
            []
        );
        return extracted;
    }

    private async Task<SourceFixture> ExtractSource(CatalogSource source)
    {
        var result = await extractor.ProcessAsync(Guid.NewGuid(), source.SourceUuid);
        result.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Published);
        var batch = (await extractions.GetAttemptBatchAsync(result.AttemptUuid))!;
        var scope = batch.Extraction.SourceActorAliases!.Single(value =>
            value.Extraction.Assertions.Count != 0
        );
        var assertion = scope.Extraction.Assertions.Single();
        var input = batch.Ticket.Inputs.Single(value =>
            value.RevisionUuid == scope.InputRevisionUuid
        );
        var contributor = NormalizationRecordingReadSet
            .Inputs(batch.Preparation)
            .Single(value => value.Binding.InputRevisionUuid == scope.InputRevisionUuid)
            .Contributor;
        var claim = new SourceActorAliasCoordinates(
            source.SourceUuid,
            contributor,
            input.ResourceUuid,
            scope.InputRevisionUuid,
            assertion.NameEvidence.JsonPointer,
            scope.ProducerVersion,
            assertion.AssertionUuid,
            assertion.ActorReferenceUuid,
            assertion.FieldSha256
        );
        return new(source, batch, claim);
    }

    private async Task<PersonFixture> Person(string name)
    {
        var input = await fixture.Capture(name, "people");
        var person = new CanonicalPerson(Guid.NewGuid(), name);
        await fixture.PublishPeople(people, input, new([person], [], [], []));
        return new(person.Uuid);
    }

    private async Task AddAlias(PersonFixture person)
    {
        var input = await fixture.Capture("A later identity alias " + Guid.NewGuid(), "people");
        await fixture.PublishPeople(
            people,
            input,
            new(
                [],
                [new(Guid.NewGuid(), person.Uuid, input.Reference.Quote!, input.Reference)],
                [],
                []
            )
        );
    }

    private async Task<SourceActorReviewPreparation> Prepare(
        SourceFixture source,
        SourceActorReviewAction action,
        PersonFixture? target = null
    )
    {
        var owner = (await fixture.Work.GetSubjectAsync(SourceOwner(source.Source.SourceUuid)))!;
        var revision = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT coalesce(max(review_revision),0) FROM normalization.source_actor_alias_review_state WHERE source_uuid=@SourceUuid AND assertion_uuid=@AssertionUuid",
                source.Claim
            )
        );
        return await reviews.PrepareAsync(
            Guid.NewGuid(),
            new(
                source.Extraction.AttemptUuid,
                source.Claim,
                action,
                owner.EditorialRevision,
                revision,
                target?.Uuid,
                target == null ? null : await PersonRevision(target.Uuid)
            )
        );
    }

    private async Task<Guid> AdvanceInput(SourceActorReviewPreparation preparation)
    {
        var scope = preparation.OriginalField.Scope;
        var current = (
            await new NormalizationEvidenceService(fixture.Db).GetCurrentInputAsync(scope.Resource)
        )!;
        var result = await new NormalizationEvidenceService(fixture.Db).CaptureInputAsync(
            new(
                Guid.NewGuid(),
                scope.Resource,
                JsonSerializer.SerializeToUtf8Bytes(
                    new { text = "The original personnel field is no longer present." }
                ),
                "application/json",
                "fixture",
                current.Generation,
                DateTimeOffset.UtcNow
            )
        );
        return result.Revision.Uuid;
    }

    private Task<long> PersonRevision(Guid uuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT revision FROM normalization.people WHERE uuid=@uuid",
                new { uuid }
            )
        );

    private async Task<Dictionary<Guid, NormalizationSubjectState>> Owners(
        SourceActorReviewPreparation preparation
    )
    {
        var result = new Dictionary<Guid, NormalizationSubjectState>();
        foreach (
            var uuid in preparation
                .ReadSet.OwnerObservations!.Select(owner => owner.SubjectUuid)
                .Append(preparation.SourceOwner.SubjectUuid)
                .Distinct()
        )
        {
            result.Add(uuid, (await fixture.Work.GetSubjectAsync(uuid))!);
        }
        return result;
    }

    private async Task AssertOnlySourceOwnerAdvanced(
        SourceActorReviewPreparation preparation,
        Dictionary<Guid, NormalizationSubjectState> before
    )
    {
        var after = await Owners(preparation);
        foreach (var (uuid, owner) in before)
        {
            if (uuid == preparation.SourceOwner.SubjectUuid)
            {
                after[uuid]
                    .Should()
                    .BeEquivalentTo(
                        owner with
                        {
                            EditorialRevision = owner.EditorialRevision + 1,
                            DesiredGeneration = owner.DesiredGeneration + 1,
                        }
                    );
            }
            else
            {
                after[uuid].Should().BeEquivalentTo(owner);
            }
        }
    }

    private async Task AssertOriginalProof(SourceActorReviewPreparation preparation)
    {
        var proof = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<OriginalProof>(
                """
                SELECT original_assertion::text AS "Assertion",original_scope::text AS "Scope"
                FROM normalization.source_actor_alias_review_state WHERE uuid=@uuid
                """,
                new { uuid = preparation.ReviewStateUuid }
            )
        );
        var assertion = preparation.OriginalField.Scope.Extraction.Assertions.Single(value =>
            value.AssertionUuid == preparation.Request.Claim.AssertionUuid
        );
        using var storedAssertion = JsonDocument.Parse(proof.Assertion);
        using var storedScope = JsonDocument.Parse(proof.Scope);
        JsonElement
            .DeepEquals(storedAssertion.RootElement, JsonSerializer.SerializeToElement(assertion))
            .Should()
            .BeTrue();
        JsonElement
            .DeepEquals(
                storedScope.RootElement,
                JsonSerializer.SerializeToElement(preparation.OriginalField.Scope)
            )
            .Should()
            .BeTrue();
    }

    private Task<ReviewRow> Decision(Guid uuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<ReviewRow>(
                ReviewColumns
                    + " FROM normalization.source_actor_alias_review_state WHERE uuid=@uuid",
                new { uuid }
            )
        );

    private async Task<ReviewRow[]> History(Guid uuid) =>
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QueryAsync<ReviewRow>(
                    ReviewColumns
                        + " FROM normalization.source_actor_alias_review_state_history WHERE uuid=@uuid ORDER BY review_revision",
                    new { uuid }
                )
            )
        ).ToArray();

    private async Task<ReviewRow[]> HistoryForClaim(SourceActorAliasCoordinates claim) =>
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QueryAsync<ReviewRow>(
                    ReviewColumns
                        + " FROM normalization.source_actor_alias_review_state_history WHERE source_uuid=@SourceUuid AND assertion_uuid=@AssertionUuid ORDER BY review_revision",
                    claim
                )
            )
        ).ToArray();

    private Task<int> ReceiptCount(Guid[] operations) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM normalization.review_operations WHERE uuid=ANY(@operations)",
                new { operations }
            )
        );

    private Task<string> ReceiptAction(Guid uuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT action FROM normalization.review_operations WHERE uuid=@uuid",
                new { uuid }
            )
        );

    private Task<string> AcceptedState() =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                """
                SELECT jsonb_build_object(
                    'people',(SELECT coalesce(jsonb_agg(to_jsonb(p) ORDER BY uuid),'[]') FROM normalization.people p),
                    'aliases',(SELECT coalesce(jsonb_agg(to_jsonb(a) ORDER BY uuid),'[]') FROM normalization.person_aliases a),
                    'assignments',(SELECT coalesce(jsonb_agg(to_jsonb(a) ORDER BY uuid),'[]') FROM normalization.source_actor_alias_assignments a),
                    'support',(SELECT coalesce(jsonb_agg(to_jsonb(s) ORDER BY uuid),'[]') FROM normalization.person_alias_contributor_support s),
                    'unmanaged',(SELECT coalesce(jsonb_agg(to_jsonb(s) ORDER BY uuid),'[]') FROM normalization.person_alias_unmanaged_support s),
                    'credits',(SELECT coalesce(jsonb_agg(to_jsonb(c) ORDER BY uuid),'[]') FROM normalization.recording_credits c)
                )::text
                """
            )
        );

    private static bool HasArgumentRejection(Exception exception) =>
        exception is ArgumentException
        || exception.InnerException != null && HasArgumentRejection(exception.InnerException);

    private static bool IsConflict(Exception exception) =>
        exception is NormalizationConflictException
        || exception.InnerException != null && IsConflict(exception.InnerException);

    private static async Task AssertConflict(Func<Task> action)
    {
        var exception = await action.Should().ThrowAsync<Exception>();
        IsConflict(exception.Which)
            .Should()
            .BeTrue("the public command must refuse stale decisions as a domain conflict");
    }

    private static NormalizationSubjectKey SourceOwner(Guid uuid) =>
        new("people", "source_actor_resolution", uuid);

    private const string ReviewColumns = """
        SELECT state AS "State",review_revision AS "ReviewRevision",selected_person_uuid AS "SelectedPersonUuid",
            last_operation_uuid AS "LastOperationUuid",input_revision_uuid AS "InputRevisionUuid"
        """;

    private sealed record SourceFixture(
        CatalogSource Source,
        NormalizationRecordingProposalBatch Extraction,
        SourceActorAliasCoordinates Claim
    );

    private sealed record PersonFixture(Guid Uuid);

    private sealed record OriginalProof(string Assertion, string Scope);

    private sealed record ReviewRow(
        string State,
        long ReviewRevision,
        Guid? SelectedPersonUuid,
        Guid LastOperationUuid,
        Guid InputRevisionUuid
    );
}
