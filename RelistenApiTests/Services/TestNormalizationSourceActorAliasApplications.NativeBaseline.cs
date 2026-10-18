using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationSourceActorAliasApplications
{
    [Test]
    public async Task NewSourceStagesObserveTheCurrentNativeBaselineWithoutRewritingItsAcceptedProof()
    {
        await SelectPolicy(Policy());
        var native = await PublishNativeBaseline();
        var originalProof = await ReadNativeBaselineProof(native);
        var first = await Source(Personnel(native.Name, Unique("First native baseline Finch")));
        (await Apply(first))
            .Result!.Disposition.Should()
            .Be(SourceActorAliasApplicationDisposition.Complete);
        var before = await fixture.Work.GetInputContributionsAsync(
            new("people", "person", native.PersonUuid)
        );
        var baseline = before.Contributions.Single(value =>
            value.Contributor.ResourceType == "preexisting_owner_bindings"
        );
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var changed = await AdvanceNativeBaseline(evidence, native.Resource);
        var source = await Source(
            Personnel(native.Name, Unique("Refreshed native baseline Finch"))
        );

        var result = await Apply(source);

        result.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        var expectedRevision = baseline.Revision;
        foreach (var stage in result.Stages)
        {
            stage.Ticket.Should().NotBeNull();
            stage.Receipt.Should().NotBeNull();
            var observed = stage.Facts.Membership.Contributions.Single(value =>
                value.Contributor == baseline.Contributor
            );
            observed.Revision.Should().Be(expectedRevision);
            var replacement = stage.Contributions.Single(value =>
                value.Contribution.Contributor == baseline.Contributor
            );
            replacement.Contribution.ExpectedRevision.Should().Be(expectedRevision);
            replacement
                .Contribution.Should()
                .BeEquivalentTo(
                    new NormalizationInputContribution(
                        baseline.Contributor,
                        expectedRevision,
                        baseline.Resources,
                        baseline.DependencySubjectUuids,
                        baseline.OwnerObservationSubjectUuids
                    )
                );
            replacement
                .ExpectedInputRevisions.Should()
                .ContainSingle()
                .Which.Should()
                .Be(
                    new NormalizationBindingInputReference(
                        changed.Revision.Uuid,
                        native.Resource,
                        changed.Fetch.ResourceGeneration
                    )
                );
            stage
                .Ticket!.Inputs.Should()
                .Contain(
                    new NormalizationInputDependency(
                        native.ResourceUuid,
                        changed.Revision.Uuid,
                        changed.Fetch.ResourceGeneration
                    )
                );
            expectedRevision++;
        }
        var after = await fixture.Work.GetInputContributionsAsync(
            new("people", "person", native.PersonUuid)
        );
        after
            .Contributions.Single(value => value.Contributor == baseline.Contributor)
            .Revision.Should()
            .Be(
                expectedRevision,
                "every admitted complete stage deliberately replaces the native configuration header once"
            );
        (await ReadNativeBaselineProof(native))
            .Should()
            .Be(
                originalProof,
                "a current binding observation must retain the old canonical rows, review history, unmanaged support and publication ticket"
            );
        (await CurrentAliases(native.PersonUuid))
            .Should()
            .Contain(value => value.Wording == native.Alias.Wording);
    }

    [Test]
    public async Task AnAdmittedSourceStageCannotRestampANativeBaselineHeadThatChangedAfterPreparation()
    {
        await SelectPolicy(Policy());
        var native = await PublishNativeBaseline();
        var initial = await Source(
            Personnel(native.Name, Unique("Retained native baseline Finch"))
        );
        (await Apply(initial))
            .Result!.Disposition.Should()
            .Be(SourceActorAliasApplicationDisposition.Complete);
        var source = await Source(Personnel(native.Name, Unique("Stale native baseline Finch")));
        var prepared = await PrepareAdmittedSourceStage(source, native.PersonUuid);
        var stage = prepared.Stages.Single();
        var originalProof = await ReadNativeBaselineProof(native);
        var accepted = await AcceptedAuthority();
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var originalHead = (await evidence.GetCurrentInputAsync(native.Resource))!;
        var changed = await AdvanceNativeBaseline(evidence, native.Resource);
        stage
            .Ticket!.Inputs.Should()
            .Contain(
                new NormalizationInputDependency(
                    native.ResourceUuid,
                    originalHead.Revision.Uuid,
                    originalHead.Generation
                )
            );
        changed.Revision.Uuid.Should().NotBe(originalHead.Revision.Uuid);

        var publication = await people.PublishSourceActorAliasStageAsync(
            prepared.AttemptUuid,
            stage.StageUuid
        );
        var result = await NewPublisher()
            .ApplyAsync(prepared.AttemptUuid, source.Resolution.AttemptUuid);

        publication.Status.Should().Be(NormalizationPublicationStatus.Stale);
        result.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Stale);
        var retained = result.Stages.Single();
        retained.StageUuid.Should().Be(stage.StageUuid);
        retained.WorkUuid.Should().Be(stage.WorkUuid);
        retained.Facts.Should().BeEquivalentTo(stage.Facts);
        retained.Contributions.Should().BeEquivalentTo(stage.Contributions);
        retained.Ticket.Should().BeEquivalentTo(stage.Ticket);
        retained.Receipt.Should().BeNull();
        retained.TerminalReason.Should().NotBeNull();
        (await AcceptedAuthority())
            .Should()
            .Be(accepted, "stale Work must refuse the accepted callback");
        (await ReadNativeBaselineProof(native)).Should().Be(originalProof);
    }

    private async Task<NativeBaseline> PublishNativeBaseline()
    {
        var name = Unique("Native baseline Robin");
        var wording = Unique("Native baseline old Finch");
        var input = await fixture.Capture(name + "; " + wording, "people");
        var person = Guid.NewGuid();
        var alias = new PersonAlias(Guid.NewGuid(), person, wording, input.Reference);
        (await fixture.PublishPeople(people, input, new([new(person, name)], [alias], [], [])))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", person)
            )
        )!;
        var originalWork = owner.PublishedWorkUuid!.Value;
        var assertionRevision = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT revision FROM normalization.person_aliases WHERE uuid=@Uuid",
                alias
            )
        );
        await people.ApplyMetadataDecisionAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                person,
                await PersonRevision(person),
                "accept",
                "Retain actual native alias review history",
                Alias: alias,
                ExpectedAssertionRevision: assertionRevision
            ),
            Operator
        );
        var resource = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<Guid>(
                "SELECT uuid FROM normalization.input_resources WHERE provider=@Provider AND resource_type=@ResourceType AND provider_key=@ProviderKey",
                input.Resource
            )
        );
        var history = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM normalization.person_aliases_history WHERE uuid=@Uuid",
                alias
            )
        );
        history
            .Should()
            .BeGreaterThan(0, "the preserved native proof must include actual review history");
        var support = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM normalization.person_alias_unmanaged_support WHERE alias_uuid=@Uuid",
                alias
            )
        );
        support
            .Should()
            .Be(1, "ordinary canonical publication supplies independent native authority");
        return new(
            person,
            name,
            alias,
            input.Resource,
            resource,
            input.Reference.InputRevisionUuid,
            originalWork
        );
    }

    private static async Task<NormalizationCaptureResult> AdvanceNativeBaseline(
        NormalizationEvidenceService evidence,
        NormalizationResourceKey resource
    )
    {
        var before = (await evidence.GetCurrentInputAsync(resource))!;
        var changed = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                JsonSerializer.SerializeToUtf8Bytes(
                    new { text = "Current native bytes " + Guid.NewGuid().ToString("N") }
                ),
                before.Revision.MediaType,
                before.Revision.CaptureVersion,
                before.Generation,
                DateTimeOffset.UtcNow
            )
        );
        changed.Fetch.AcceptedAsCurrent.Should().BeTrue();
        changed.Fetch.AdvancedCurrent.Should().BeTrue();
        changed.Fetch.ResourceGeneration.Should().Be(before.Generation + 1);
        return changed;
    }

    private Task<string> ReadNativeBaselineProof(NativeBaseline native) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                """
                SELECT jsonb_build_object(
                    'person',(SELECT jsonb_build_array(uuid,display_name,kind) FROM normalization.people WHERE uuid=@PersonUuid),
                    'aliases',(SELECT jsonb_agg(to_jsonb(alias) ORDER BY uuid) FROM normalization.person_aliases alias WHERE uuid=ANY(@aliases)),
                    'history',(SELECT jsonb_agg(to_jsonb(history) ORDER BY uuid,revision) FROM normalization.person_aliases_history history WHERE uuid=ANY(@aliases)),
                    'unmanaged',(SELECT jsonb_agg(to_jsonb(support) ORDER BY uuid) FROM normalization.person_alias_unmanaged_support support WHERE alias_uuid=ANY(@aliases)),
                    'original_input',(SELECT to_jsonb(revision) FROM normalization.input_revisions revision WHERE uuid=@InputRevisionUuid),
                    'original_work',(SELECT to_jsonb(run) FROM normalization.work_runs run WHERE uuid=@OriginalWorkUuid))::text
                """,
                new
                {
                    native.PersonUuid,
                    aliases = new[] { native.PersonUuid, native.Alias.Uuid },
                    native.InputRevisionUuid,
                    native.OriginalWorkUuid,
                }
            )
        );

    private sealed record NativeBaseline(
        Guid PersonUuid,
        string Name,
        PersonAlias Alias,
        NormalizationResourceKey Resource,
        Guid ResourceUuid,
        Guid InputRevisionUuid,
        Guid OriginalWorkUuid
    );
}
