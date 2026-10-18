using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationSourceActorAliasApplications
{
    [TestCase("request_hash")]
    [TestCase("ticket")]
    [TestCase("owner")]
    [TestCase("state")]
    public async Task CandidateAliasSupportsAuthenticateTheActualWorkBehindAnUnchangedCertificate(
        string fault
    )
    {
        await SelectPolicy(Policy());
        var name = IndependentActorName();
        var person = await Person(name);
        Guid? otherOwnerUuid = null;
        if (fault == "owner")
        {
            var other = await Person(IndependentActorName());
            otherOwnerUuid = (
                await fixture.Work.GetSubjectAsync(
                    new NormalizationSubjectKey("people", "person", other)
                )
            )!.SubjectUuid;
        }
        var source = await Source(Personnel(name, IndependentActorName()));
        var application = await Apply(source);
        application
            .Result!.Disposition.Should()
            .Be(SourceActorAliasApplicationDisposition.Complete);
        await AssertCertifiedAliasSupportCountApplication(application.AttemptUuid);
        var supports = Receipts(application)
            .SelectMany(receipt => receipt.Names)
            .Where(name => name.SupportUuid.HasValue)
            .Select(name => name.SupportUuid!.Value)
            .Distinct()
            .ToArray();
        supports.Should().NotBeEmpty();
        var workUuid = (
            await fixture.Db.WithWriteConnection(c =>
                c.QueryAsync<Guid>(
                    """
                    SELECT DISTINCT introduced_work_uuid FROM normalization.person_alias_contributor_support
                    WHERE uuid=ANY(@supports)
                    """,
                    new { supports }
                )
            )
        ).Single();
        var stage = application.Stages.Single(stage => stage.WorkUuid == workUuid);
        var originalWork = await ReadAliasSupportWork(workUuid);
        var certificate = await ReadAliasSupportCertificate(stage.StageUuid);
        var accepted = await AcceptedAuthority();
        var before = await ReadAliasSupportCountFacts(name);
        before.Complete.Should().BeTrue();
        before
            .Facts.Single(value => value.Hit.Uuid == person)
            .AliasSupportCount.Should()
            .Be(supports.LongLength);
        await AssertAliasSupportCountOwners(before, person, source);

        try
        {
            // Commit only a scoped test fault so the public reader's own transaction sees it.
            // The genuine aliases, support, certificate and original owner clocks stay intact.
            // Restore the exact Work bytes in finally, including when an assertion fails.
            var mutation = fault switch
            {
                "request_hash" => "request_hash=repeat('0',64)",
                "ticket" =>
                    "ticket=jsonb_set(ticket,'{PolicyVersion}',to_jsonb('different-policy'::text))",
                "owner" => "subject_uuid=@otherOwnerUuid",
                "state" => "state='running'",
                _ => throw new ArgumentException("Unknown actual Work fault."),
            };
            (
                await fixture.Db.WithWriteConnection(c =>
                    c.ExecuteAsync(
                        $"UPDATE normalization.work_runs SET {mutation} WHERE uuid=@workUuid",
                        new { workUuid, otherOwnerUuid }
                    )
                )
            )
                .Should()
                .Be(1);
            (await ReadAliasSupportCertificate(stage.StageUuid)).Should().Be(certificate);

            var invalid = await ReadAliasSupportCountFacts(name);

            invalid.Complete.Should().BeFalse();
            invalid
                .Facts.Single(value => value.Hit.Uuid == person)
                .AliasSupportCount.Should()
                .Be(supports.LongLength);
            invalid
                .Issues.Should()
                .Contain(issue =>
                    issue.CandidateUuid == person
                    && issue.Kind == CandidateFactsIssueKind.InvalidStoredFact
                    && issue.FactKind == "person_alias_support_proof"
                    && issue.PreventsAutomaticAcceptance
                );
            await AssertAliasSupportCountOwners(invalid, person, source);
            (await AcceptedAuthority()).Should().Be(accepted);
        }
        finally
        {
            (
                await fixture.Db.WithWriteConnection(c =>
                    c.ExecuteAsync(
                        """
                        UPDATE normalization.work_runs SET subject_uuid=@SubjectUuid,request_hash=@RequestHash,
                            state=@State,ticket=@Ticket::jsonb WHERE uuid=@workUuid
                        """,
                        new
                        {
                            workUuid,
                            originalWork.SubjectUuid,
                            originalWork.RequestHash,
                            originalWork.State,
                            originalWork.Ticket,
                        }
                    )
                )
            ).Should().Be(1);
        }

        (await ReadAliasSupportWork(workUuid)).Should().Be(originalWork);
        (await ReadAliasSupportCertificate(stage.StageUuid)).Should().Be(certificate);
        (await AcceptedAuthority()).Should().Be(accepted);
        var restored = await ReadAliasSupportCountFacts(name);
        restored.Complete.Should().BeTrue();
        restored
            .Facts.Single(value => value.Hit.Uuid == person)
            .AliasSupportCount.Should()
            .Be(supports.LongLength);
        await AssertAliasSupportCountOwners(restored, person, source);
    }

    private Task<AliasSupportWorkImage> ReadAliasSupportWork(Guid workUuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<AliasSupportWorkImage>(
                """
                SELECT subject_uuid AS "SubjectUuid",request_hash AS "RequestHash",state AS "State",
                    ticket::text AS "Ticket" FROM normalization.work_runs WHERE uuid=@workUuid
                """,
                new { workUuid }
            )
        );

    private Task<string> ReadAliasSupportCertificate(Guid stageUuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                """
                SELECT receipt_header::text FROM normalization.source_actor_alias_person_stages
                WHERE stage_uuid=@stageUuid
                """,
                new { stageUuid }
            )
        );

    private sealed record AliasSupportWorkImage(
        Guid SubjectUuid,
        string RequestHash,
        string State,
        string Ticket
    );
}
