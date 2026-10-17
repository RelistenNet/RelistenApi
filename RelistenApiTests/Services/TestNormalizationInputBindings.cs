using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationInputBindings
{
    private NormalizationDomainFixture fixture = null!;

    [OneTimeSetUp]
    public void Start() => fixture = new();

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task ConcurrentRecordingsKeepTheCompleteUnionOfInputsAndDependencies()
    {
        var a = await fixture.Capture("recording A", "songs");
        var b = await fixture.Capture("recording B", "songs");
        var dependencyA = await Dependency();
        var dependencyB = await Dependency();
        var owner = Owner();
        var keyA = Key();
        var keyB = Key();
        var tickets = await Task.WhenAll(
            fixture.Work.BeginContributedWorkAsync(
                Guid.NewGuid(),
                owner,
                "policy-1",
                new(keyA, 0, [a.Resource], [dependencyA])
            ),
            fixture.Work.BeginContributedWorkAsync(
                Guid.NewGuid(),
                owner,
                "policy-1",
                new(keyB, 0, [b.Resource], [dependencyB])
            )
        );
        var latest = tickets.MaxBy(ticket => ticket.DesiredGeneration)!;
        latest
            .Inputs.Select(input => input.RevisionUuid)
            .Should()
            .BeEquivalentTo([a.Reference.InputRevisionUuid, b.Reference.InputRevisionUuid]);
        latest
            .Dependencies.Select(dependency => dependency.SubjectUuid)
            .Should()
            .BeEquivalentTo([dependencyA, dependencyB]);
        (await fixture.Work.GetInputContributionAsync(owner, keyA))!
            .Resources.Should()
            .Equal(a.Resource);
        (await fixture.Work.GetInputContributionAsync(owner, keyB))!
            .Resources.Should()
            .Equal(b.Resource);
        (await Publish(tickets.MinBy(ticket => ticket.DesiredGeneration)!))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
        (await Publish(latest)).Status.Should().Be(NormalizationPublicationStatus.Published);
    }

    [Test]
    public async Task RemovingOneRecordingRetainsSharedEvidenceAndOtherRecordingDependencies()
    {
        var a = await fixture.Capture("A", "songs");
        var shared = await fixture.Capture("shared catalog", "songs");
        var dependency = await Dependency();
        var owner = Owner();
        var keyA = Key();
        var keyB = Key();
        var old = await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            owner,
            "policy-1",
            new(keyA, 0, [a.Resource, shared.Resource], [])
        );
        await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            owner,
            "policy-1",
            new(keyB, 0, [shared.Resource], [dependency])
        );
        var remaining = await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            owner,
            "policy-1",
            new(keyA, 1, [], [])
        );
        remaining
            .Inputs.Should()
            .ContainSingle()
            .Which.RevisionUuid.Should()
            .Be(shared.Reference.InputRevisionUuid);
        remaining.Dependencies.Should().ContainSingle().Which.SubjectUuid.Should().Be(dependency);
        var tombstone = (await fixture.Work.GetInputContributionAsync(owner, keyA))!;
        tombstone.Revision.Should().Be(2);
        tombstone.Resources.Should().BeEmpty();
        (await Publish(old)).Status.Should().Be(NormalizationPublicationStatus.Stale);
        (await Publish(remaining)).Status.Should().Be(NormalizationPublicationStatus.Published);
    }

    [Test]
    public async Task ConcurrentEditsToTheSameContributorHaveOneCasWinner()
    {
        var a = await fixture.Capture("A", "songs");
        var b = await fixture.Capture("B", "songs");
        var owner = Owner();
        var key = Key();
        var outcomes = await Task.WhenAll(Attempt(a.Resource), Attempt(b.Resource));
        outcomes.Count(ticket => ticket != null).Should().Be(1);
        var winner = outcomes.Single(ticket => ticket != null)!;
        var stored = (await fixture.Work.GetInputContributionAsync(owner, key))!;
        stored.Revision.Should().Be(1);
        stored.Resources.Should().ContainSingle();
        winner.Inputs.Should().ContainSingle();

        async Task<NormalizationWorkTicket?> Attempt(NormalizationResourceKey resource)
        {
            try
            {
                return await fixture.Work.BeginContributedWorkAsync(
                    Guid.NewGuid(),
                    owner,
                    "policy-1",
                    new(key, 0, [resource], [])
                );
            }
            catch (NormalizationConflictException)
            {
                return null;
            }
        }
    }

    [Test]
    public async Task ExactReplayReturnsTheOriginalUnionWithoutRepeatingAContributorEdit()
    {
        var a = await fixture.Capture("A", "songs");
        var b = await fixture.Capture("B", "songs");
        var owner = Owner();
        var key = Key();
        var workUuid = Guid.NewGuid();
        var first = await fixture.Work.BeginContributedWorkAsync(
            workUuid,
            owner,
            "policy-1",
            new(key, 0, [a.Resource], [])
        );
        await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            owner,
            "policy-1",
            new(key, 1, [b.Resource], [])
        );
        var replay = await fixture.Work.BeginContributedWorkAsync(
            workUuid,
            owner,
            "policy-1",
            new(key, 0, [a.Resource, a.Resource], [])
        );
        replay.Should().BeEquivalentTo(first);
        (await fixture.Work.GetInputContributionAsync(owner, key))!.Revision.Should().Be(2);
        var reused = () =>
            fixture.Work.BeginContributedWorkAsync(
                workUuid,
                owner,
                "policy-1",
                new(key, 0, [b.Resource], [])
            );
        await reused.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task OrdinaryWorkCannotReplaceAContributorUnionWithOneRecordingsInputs()
    {
        var a = await fixture.Capture("A", "songs");
        var b = await fixture.Capture("B", "songs");
        var owner = Owner();
        await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            owner,
            "policy-1",
            new(Key(), 0, [a.Resource], [])
        );
        var both = await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            owner,
            "policy-1",
            new(Key(), 0, [b.Resource], [])
        );
        var partial = () =>
            fixture.Work.BeginWorkAsync(
                Guid.NewGuid(),
                owner,
                "policy-1",
                [b.Resource],
                [],
                both.DesiredGeneration
            );
        await partial.Should().ThrowAsync<NormalizationConflictException>();
        var complete = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            owner,
            "policy-1",
            [b.Resource, a.Resource],
            []
        );
        complete.Inputs.Should().HaveCount(2);
    }

    [Test]
    public async Task AdoptingAnExistingOwnerPreservesAnExplicitRemovableBaseline()
    {
        var a = await fixture.Capture("existing manual input", "songs");
        var b = await fixture.Capture("new recording", "songs");
        var dependency = await Dependency();
        var owner = Owner();
        var previous = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            owner,
            "policy-1",
            [a.Resource],
            [dependency]
        );
        var adopted = await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            owner,
            "policy-1",
            new(Key(), 0, [b.Resource], [])
        );
        adopted.Inputs.Should().HaveCount(2);
        adopted.Dependencies.Should().ContainSingle().Which.SubjectUuid.Should().Be(dependency);
        var baselineKey = new NormalizationResourceKey(
            "relisten",
            "preexisting_owner_bindings",
            previous.SubjectUuid.ToString("D")
        );
        var baseline = (await fixture.Work.GetInputContributionAsync(owner, baselineKey))!;
        baseline.Revision.Should().Be(1);
        baseline.Resources.Should().Equal(a.Resource);
        var reconciled = await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            owner,
            "policy-1",
            new(baselineKey, 1, [], [])
        );
        reconciled
            .Inputs.Should()
            .ContainSingle()
            .Which.RevisionUuid.Should()
            .Be(b.Reference.InputRevisionUuid);
        reconciled.Dependencies.Should().BeEmpty();
    }

    [Test]
    public async Task MissingInputOrDependencyDoesNotConsumeTheContributorRevision()
    {
        var input = await fixture.Capture("A", "songs");
        var owner = Owner();
        var key = Key();
        var missing = new NormalizationResourceKey("test", "missing", Guid.NewGuid().ToString());
        var missingInput = () =>
            fixture.Work.BeginContributedWorkAsync(
                Guid.NewGuid(),
                owner,
                "policy-1",
                new(key, 0, [missing], [])
            );
        await missingInput.Should().ThrowAsync<ArgumentException>();
        (await fixture.Work.GetInputContributionAsync(owner, key)).Should().BeNull();
        var initial = await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            owner,
            "policy-1",
            new(key, 0, [input.Resource], [])
        );
        var missingDependency = () =>
            fixture.Work.BeginContributedWorkAsync(
                Guid.NewGuid(),
                owner,
                "policy-1",
                new(key, 1, [], [Guid.NewGuid()])
            );
        await missingDependency.Should().ThrowAsync<ArgumentException>();
        var selfDependency = () =>
            fixture.Work.BeginContributedWorkAsync(
                Guid.NewGuid(),
                owner,
                "policy-1",
                new(key, 1, [], [initial.SubjectUuid])
            );
        await selfDependency.Should().ThrowAsync<ArgumentException>();
        (await fixture.Work.GetInputContributionAsync(owner, key))!.Revision.Should().Be(1);
        (await fixture.Work.GetSubjectAsync(initial.SubjectUuid))!
            .DesiredGeneration.Should()
            .Be(initial.DesiredGeneration);
    }

    [Test]
    public async Task PolicyCasFailureRollsBackContributorChangesAndAnObsoletePolicyCannotReturn()
    {
        var a = await fixture.Capture("A", "songs");
        var b = await fixture.Capture("B", "songs");
        var owner = Owner();
        var key = Key();
        var initial = await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            owner,
            "policy-1",
            new(key, 0, [a.Resource], [])
        );
        var unversioned = () =>
            fixture.Work.BeginContributedWorkAsync(
                Guid.NewGuid(),
                owner,
                "policy-2",
                new(key, 1, [b.Resource], [])
            );
        await unversioned.Should().ThrowAsync<NormalizationConflictException>();
        var unchanged = (await fixture.Work.GetInputContributionAsync(owner, key))!;
        unchanged.Revision.Should().Be(1);
        unchanged.Resources.Should().Equal(a.Resource);
        var changed = await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            owner,
            "policy-2",
            new(key, 1, [b.Resource], []),
            initial.DesiredGeneration
        );
        changed.PolicyVersion.Should().Be("policy-2");
        var obsolete = () =>
            fixture.Work.BeginContributedWorkAsync(
                Guid.NewGuid(),
                owner,
                "policy-1",
                new(key, 2, [a.Resource], []),
                initial.DesiredGeneration
            );
        await obsolete.Should().ThrowAsync<NormalizationConflictException>();
        (await fixture.Work.GetInputContributionAsync(owner, key))!.Revision.Should().Be(2);
    }

    [Test]
    public async Task CatalogAndGlobalSourceKeysJoinTheUnionWithoutFresheningCallerSnapshots()
    {
        var inputA = await fixture.Capture("A", "songs");
        var inputB = await fixture.Capture("B", "songs");
        var sourceA = await fixture.Source();
        var sourceB = await fixture.Source();
        var snapshotA = await Catalog(sourceA.ArtistId, sourceA.SourceUuid);
        var snapshotB = await Catalog(sourceB.ArtistId, sourceB.SourceUuid);
        var owner = Owner();
        await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            owner,
            "policy-1",
            new(Key(), 0, [inputA.Resource], []),
            catalogInputs: [snapshotA.Artist],
            sourceIdentities: [snapshotA.Source]
        );
        var keyB = Key();
        var both = await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            owner,
            "policy-1",
            new(keyB, 0, [inputB.Resource], []),
            catalogInputs: [snapshotA.Artist, snapshotB.Artist],
            sourceIdentities: [snapshotA.Source, snapshotB.Source]
        );
        both.CatalogInputs!.Select(input => input.ArtistUuid)
            .Should()
            .BeEquivalentTo([snapshotA.Artist.ArtistUuid, snapshotB.Artist.ArtistUuid]);
        both.SourceIdentities!.Select(input => input.UpstreamIdentifier)
            .Should()
            .BeEquivalentTo([
                snapshotA.Source.UpstreamIdentifier,
                snapshotB.Source.UpstreamIdentifier,
            ]);
        await fixture.Db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                "UPDATE artists SET name=name||' revised' WHERE id=@ArtistId",
                sourceB
            )
        );
        var stale = () =>
            fixture.Work.BeginContributedWorkAsync(
                Guid.NewGuid(),
                owner,
                "policy-1",
                new(keyB, 1, [inputB.Resource], []),
                catalogInputs: [snapshotB.Artist],
                sourceIdentities: [snapshotB.Source]
            );
        await stale.Should().ThrowAsync<NormalizationConflictException>();
        (await fixture.Work.GetInputContributionAsync(owner, keyB))!.Revision.Should().Be(1);

        var withdrawn = await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            owner,
            "policy-1",
            new(keyB, 1, [], [])
        );
        withdrawn
            .Inputs.Should()
            .ContainSingle()
            .Which.RevisionUuid.Should()
            .Be(inputA.Reference.InputRevisionUuid);
        withdrawn.CatalogInputs!.Should().ContainSingle().Which.Should().Be(snapshotA.Artist);
        withdrawn.SourceIdentities!.Should().ContainSingle().Which.Should().Be(snapshotA.Source);
        var tombstone = (await fixture.Work.GetInputContributionAsync(owner, keyB))!;
        tombstone.Revision.Should().Be(2);
        tombstone.CatalogArtistUuids.Should().BeEmpty();
        tombstone.SourceIdentifiers.Should().BeEmpty();
    }

    private static NormalizationSubjectKey Owner() =>
        new("songs", "artist_repertoire", Guid.NewGuid());

    private static NormalizationResourceKey Key() =>
        new("relisten", "recording", Guid.NewGuid().ToString("D"));

    private async Task<Guid> Dependency() =>
        (
            await fixture.Work.BeginWorkAsync(
                Guid.NewGuid(),
                new("compositions", "composition", Guid.NewGuid()),
                "policy-1",
                [],
                []
            )
        ).SubjectUuid;

    private Task<NormalizationPublicationResult> Publish(NormalizationWorkTicket ticket) =>
        fixture.Work.TryPublishAsync(ticket.WorkUuid, (_, _, _) => Task.CompletedTask);

    private async Task<(
        NormalizationCatalogDependency Artist,
        NormalizationSourceIdentityDependency Source
    )> Catalog(int artistId, Guid sourceUuid)
    {
        var row = await fixture.Db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<CatalogSnapshot>(
                """
                SELECT a.uuid AS ArtistUuid,c.generation AS ArtistGeneration,s.upstream_identifier AS Identifier,i.generation AS SourceGeneration
                FROM artists a JOIN sources s ON s.artist_id=a.id
                JOIN normalization.catalog_artist_revisions c ON c.artist_uuid=a.uuid
                JOIN normalization.catalog_source_identity_revisions i ON i.upstream_identifier=s.upstream_identifier
                WHERE a.id=@artistId AND s.uuid=@sourceUuid
                """,
                new { artistId, sourceUuid }
            )
        );
        return (
            new(row.ArtistUuid, row.ArtistGeneration),
            new(row.Identifier, row.SourceGeneration)
        );
    }

    private sealed record CatalogSnapshot(
        Guid ArtistUuid,
        long ArtistGeneration,
        string Identifier,
        long SourceGeneration
    );
}
