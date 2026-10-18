using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationSourceActorAliasApplications
{
    [Test]
    public async Task CertifiedAliasSupportCountsRetainEveryOwnerEvenWhenNoAliasWitnessFits()
    {
        await SelectPolicy(Policy());
        var name = IndependentActorName();
        var person = await Person(name);
        var first = await Source(Personnel(name, IndependentActorName()));
        var firstApplication = await Apply(first);
        firstApplication
            .Result!.Disposition.Should()
            .Be(SourceActorAliasApplicationDisposition.Complete);
        var second = await Source(Personnel(name, IndependentActorName()));
        var secondApplication = await Apply(second);
        secondApplication
            .Result!.Disposition.Should()
            .Be(SourceActorAliasApplicationDisposition.Complete);
        await AssertCertifiedAliasSupportCountApplication(firstApplication.AttemptUuid);
        await AssertCertifiedAliasSupportCountApplication(secondApplication.AttemptUuid);
        var supports = Receipts(firstApplication)
            .Concat(Receipts(secondApplication))
            .SelectMany(receipt => receipt.Names)
            .Where(name => name.SupportUuid.HasValue)
            .Select(name => name.SupportUuid!.Value)
            .Distinct()
            .ToArray();
        supports.Should().NotBeEmpty();

        var snapshot = await ReadAliasSupportCountFacts(name);

        snapshot.Complete.Should().BeTrue();
        var fact = snapshot.Facts.Single(value => value.Hit.Uuid == person);
        fact.AliasSupportCount.Should().Be(supports.LongLength);
        fact.Aliases.Should().BeEmpty();
        fact.AliasEvidenceTruncated.Should().BeTrue();
        fact.AliasMatchingNamesComplete.Should().BeTrue();
        await AssertAliasSupportCountOwners(snapshot, person, first, second);
        var universe = await AliasSupportCountPeopleGeneration();
        var accepted = await AcceptedAuthority();

        // A source decision advances its read-only owner before physical support retirement.
        // Neither bounded witnesses nor a publication certificate can omit that fresh stamp.
        await Review(first, SourceActorReviewAction.Reject);
        var afterReview = await ReadAliasSupportCountFacts(name);

        afterReview.Complete.Should().BeTrue();
        afterReview
            .Facts.Single(value => value.Hit.Uuid == person)
            .AliasSupportCount.Should()
            .Be(supports.LongLength);
        await AssertAliasSupportCountOwners(afterReview, person, first, second);
        (await AliasSupportCountPeopleGeneration()).Should().Be(universe);
        (await AcceptedAuthority()).Should().Be(accepted);
        var sourceOwner = (
            await fixture.Work.GetSubjectAsync(
                SourceActorResolutionService.Owner(first.Source.SourceUuid)
            )
        )!;
        var earlier = snapshot.ReadSet.OwnerObservations.Single(owner =>
            owner.SubjectUuid == sourceOwner.SubjectUuid
        );
        var current = afterReview.ReadSet.OwnerObservations.Single(owner =>
            owner.SubjectUuid == sourceOwner.SubjectUuid
        );
        current.EditorialRevision.Should().Be(earlier.EditorialRevision + 1);
    }

    [TestCase("wrong_hash")]
    [TestCase("unknown_version")]
    [TestCase("partial")]
    [TestCase("null_fields")]
    [TestCase("version_type")]
    [TestCase("names_not_array")]
    [TestCase("missing_names")]
    [TestCase("wrong_support")]
    public async Task ContradictorySupportCertificateBlocksFactsInsteadOfUsingStructuralFallback(
        string mode
    )
    {
        await SelectPolicy(Policy());
        var name = IndependentActorName();
        var person = await Person(name);
        var source = await Source(Personnel(name, IndependentActorName()));
        var stage = await PublishAliasSupportCountProjection(source, person, mode);
        var supportCount = stage
            .Receipt!.Names.Where(name => name.SupportUuid.HasValue)
            .Select(name => name.SupportUuid!.Value)
            .Distinct()
            .LongCount();
        supportCount.Should().BeGreaterThan(0);

        var snapshot = await ReadAliasSupportCountFacts(name);

        snapshot.Complete.Should().BeFalse();
        snapshot
            .Facts.Single(value => value.Hit.Uuid == person)
            .AliasSupportCount.Should()
            .Be(supportCount);
        snapshot
            .Issues.Should()
            .Contain(issue =>
                issue.CandidateUuid == person
                && issue.Kind == CandidateFactsIssueKind.InvalidStoredFact
                && issue.FactKind == "person_alias_support_proof"
                && issue.PreventsAutomaticAcceptance
            );
        await AssertAliasSupportCountOwners(snapshot, person, source);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task GenuinelyAbsentCertificateUsesTheOriginalStructuralProof(bool brokenScope)
    {
        await SelectPolicy(Policy());
        var name = IndependentActorName();
        var person = await Person(name);
        var source = await Source(Personnel(name, IndependentActorName()));
        var stage = await PublishAliasSupportCountProjection(source, person, "legacy", brokenScope);
        var supportCount = stage
            .Receipt!.Names.Where(name => name.SupportUuid.HasValue)
            .Select(name => name.SupportUuid!.Value)
            .Distinct()
            .LongCount();
        supportCount.Should().BeGreaterThan(0);

        var snapshot = await ReadAliasSupportCountFacts(name);

        snapshot.Complete.Should().Be(!brokenScope);
        snapshot
            .Facts.Single(value => value.Hit.Uuid == person)
            .AliasSupportCount.Should()
            .Be(supportCount);
        snapshot
            .Issues.Any(issue =>
                issue.CandidateUuid == person
                && issue.Kind == CandidateFactsIssueKind.InvalidStoredFact
                && issue.FactKind == "person_alias_support_proof"
            )
            .Should()
            .Be(brokenScope);
        await AssertAliasSupportCountOwners(snapshot, person, source);
    }

    private Task<
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts>
    > ReadAliasSupportCountFacts(string name) =>
        candidates.FindPeopleFactsAsync(
            new(IdentityMatchingDomain.People, [name], [], MinimumNameSimilarity: .99),
            new() { MaximumAliasEvidenceBytes = 1 }
        );

    private async Task AssertAliasSupportCountOwners(
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts> snapshot,
        Guid person,
        params SourceContext[] sources
    )
    {
        var ownerUuids = new List<Guid>
        {
            (
                await fixture.Work.GetSubjectAsync(
                    new NormalizationSubjectKey("people", "person", person)
                )
            )!.SubjectUuid,
        };
        foreach (var source in sources)
        {
            ownerUuids.Add(source.Extraction.Ticket.SubjectUuid);
            ownerUuids.Add(
                (
                    await fixture.Work.GetSubjectAsync(
                        SourceActorResolutionService.Owner(source.Source.SourceUuid)
                    )
                )!.SubjectUuid
            );
        }
        foreach (var uuid in ownerUuids.Distinct())
        {
            var owner = (await fixture.Work.GetSubjectAsync(uuid))!;
            snapshot
                .ReadSet.OwnerObservations.Should()
                .ContainEquivalentOf(
                    new NormalizationSubjectObservation(
                        owner.SubjectUuid,
                        owner.DesiredGeneration,
                        owner.EditorialRevision,
                        owner.PublishedWorkUuid
                    )
                );
        }
    }

    private async Task AssertCertifiedAliasSupportCountApplication(Guid applicationUuid)
    {
        var application = (await applications.GetApplicationAsync(applicationUuid))!;
        foreach (var stage in application.Stages.Where(stage => stage.Receipt != null))
        {
            var actual = await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<string>(
                    "SELECT request_hash FROM normalization.work_runs WHERE uuid=@WorkUuid",
                    new { stage.WorkUuid }
                )
            );
            stage.Receipt!.CertificateVersion.Should().NotBeNullOrEmpty();
            stage.Receipt.AuthenticatedRequestHash.Should().Be(actual);
        }
    }

    private Task<long> AliasSupportCountPeopleGeneration() =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                """
                SELECT generation FROM normalization.candidate_universe_revisions
                WHERE domain='people' AND scope_uuid='00000000-0000-0000-0000-000000000000'
                """
            )
        );

    private async Task<SourceActorAliasPersonStage> PublishAliasSupportCountProjection(
        SourceContext source,
        Guid person,
        string mode,
        bool brokenScope = false
    )
    {
        var saved = await PrepareAdmittedSourceStage(source, person);
        var stage = saved.Stages.Single();
        var hook = "alias_counts_fault_" + Guid.NewGuid().ToString("N");
        try
        {
            // Faults affect only the genuine first write. No retained receipt, history,
            // assignment or support is subsequently rewritten to manufacture authority.
            await InstallAliasSupportCountProjection(hook, stage.StageUuid, mode, brokenScope);
            (await people.PublishSourceActorAliasStageAsync(saved.AttemptUuid, stage.StageUuid))
                .Status.Should()
                .Be(NormalizationPublicationStatus.Published);
        }
        finally
        {
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    $"""
                    DROP TRIGGER IF EXISTS {hook}_receipt ON normalization.source_actor_alias_person_stages;
                    DROP FUNCTION IF EXISTS normalization.{hook}_receipt();
                    DROP TRIGGER IF EXISTS {hook}_scope ON normalization.source_actor_alias_assignments;
                    DROP FUNCTION IF EXISTS normalization.{hook}_scope();
                    """
                )
            );
        }
        // Read the genuine full receipt through its public audit API. Certificate projection
        // changes only the metadata consumed by the optimized candidate-facts reader.
        return (await applications.GetStageAsync(stage.StageUuid))!;
    }

    private Task InstallAliasSupportCountProjection(
        string hook,
        Guid stageUuid,
        string mode,
        bool brokenScope
    )
    {
        var projection = mode switch
        {
            "unchanged" => "NEW.receipt_header",
            "legacy" => "NEW.receipt_header-'CertificateVersion'-'AuthenticatedRequestHash'",
            "wrong_hash" =>
                "jsonb_set(NEW.receipt_header,'{AuthenticatedRequestHash}',to_jsonb(repeat('0',64)))",
            "unknown_version" =>
                "jsonb_set(NEW.receipt_header,'{CertificateVersion}',to_jsonb('unsupported-certificate'::text))",
            "partial" => "NEW.receipt_header-'CertificateVersion'",
            "null_fields" =>
                "NEW.receipt_header || '{\"CertificateVersion\":null,\"AuthenticatedRequestHash\":null}'::jsonb",
            "version_type" => "jsonb_set(NEW.receipt_header,'{CertificateVersion}','42'::jsonb)",
            "names_not_array" => "jsonb_set(NEW.receipt_header,'{Names}','{}'::jsonb)",
            "missing_names" => "NEW.receipt_header-'Names'",
            "wrong_support" =>
                "jsonb_set(NEW.receipt_header,'{Names}',(SELECT jsonb_agg(name || jsonb_build_object('SupportUuid','00000000-0000-0000-0000-000000000001')) FROM jsonb_array_elements(NEW.receipt_header->'Names') name))",
            _ => throw new ArgumentException("Unknown support-count receipt projection."),
        };
        var scopeProjection = brokenScope
            ? $"""
                CREATE FUNCTION normalization.{hook}_scope() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.stage_uuid='{stageUuid:D}'::uuid THEN
                        NEW.original_scope:=jsonb_build_object();
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER {hook}_scope BEFORE INSERT ON normalization.source_actor_alias_assignments
                    FOR EACH ROW EXECUTE FUNCTION normalization.{hook}_scope();
                """
            : "";
        return fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                $"""
                CREATE FUNCTION normalization.{hook}_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.stage_uuid='{stageUuid:D}'::uuid AND OLD.receipt IS NULL AND NEW.receipt IS NOT NULL THEN
                        NEW.receipt_header:={projection};
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER {hook}_receipt BEFORE UPDATE ON normalization.source_actor_alias_person_stages
                    FOR EACH ROW EXECUTE FUNCTION normalization.{hook}_receipt();
                {scopeProjection}
                """
            )
        );
    }
}
