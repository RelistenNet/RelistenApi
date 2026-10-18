using System.Data;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationCompletedRepertoireConsumption
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizedMusicService music = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationRepertoireResolutionStore store = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        music = new(fixture.Db, fixture.Work);
        evidence = new(fixture.Db);
        store = new(fixture.Db);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [TestCase(false)]
    [TestCase(true)]
    public async Task CompletedDictionaryReadsEveryDecisionAndCurrentAuthorityWithoutWrites(
        bool empty
    )
    {
        var account = await Dictionary(empty);
        var before = await Counts();

        var consumption = await Read(account.AttemptUuid);

        consumption.Attempt.AttemptUuid.Should().Be(account.AttemptUuid);
        consumption.Source.Claims.Count.Should().Be(empty ? 0 : 1);
        consumption
            .Decisions.Select(value => value.ClaimUuid)
            .Should()
            .BeEquivalentTo(consumption.Source.Claims.Select(value => value.ClaimUuid));
        consumption.Targets.Count.Should().Be(empty ? 0 : 1);
        consumption.Recording.Should().BeNull();
        consumption.Setlist.Should().BeNull();
        consumption
            .AcknowledgmentReceipt.Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        consumption
            .AcknowledgmentPublication.PublishedWorkUuid.Should()
            .Be(consumption.AcknowledgmentTicket.WorkUuid);
        consumption
            .InputReferences.Select(value => value.InputRevisionUuid)
            .Should()
            .BeEquivalentTo(consumption.ProducerReadSet.Inputs.Select(value => value.RevisionUuid));
        if (!empty)
        {
            var target = consumption.Targets.Single();
            target.CanonicalUuid.Should().Be(target.Accepted.Value.Uuid);
            target.Accepted.Value.ExpectedRevision.Should().Be(target.Accepted.Revision);
            consumption.AcceptedReadSet.Dependencies.Should().ContainSingle();
            consumption.AcceptedReadSet.OwnerObservations.Should().ContainSingle();
        }
        (await Counts()).Should().Be(before, "the caller rolls back a read-only transaction");
    }

    [Test]
    public async Task PartialAcceptedAccountCannotBeConsumedBeforeAcknowledgment()
    {
        var (request, _) = await DictionaryRequest(false);
        var processor = Processor();
        var uuid = Guid.NewGuid();
        RepertoireResolutionAttempt? partial = null;
        for (var step = 0; step < 30; step++)
        {
            partial = await processor.ResolveAsync(uuid, request);
            if (
                partial.Stages.Any(value =>
                    value.Kind == RepertoireResolutionStageKind.AcceptedWrite
                    && value.Receipt?.Status == NormalizationPublicationStatus.Published
                )
            )
            {
                break;
            }
        }
        partial!
            .Stages.Should()
            .Contain(value =>
                value.Kind == RepertoireResolutionStageKind.AcceptedWrite
                && value.Receipt!.Status == NormalizationPublicationStatus.Published
            );
        partial.Result.Should().BeNull();

        await FluentActions
            .Invoking(() => Read(uuid))
            .Should()
            .ThrowAsync<NormalizationConflictException>();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CompletePublishedProducerKeepsTypedAccountsAndAllOriginalFamilies(
        bool setlist
    )
    {
        var request = await PublishedTitle(setlist);
        var completed = await Resolve(request);

        var consumption = await Read(completed.AttemptUuid);

        consumption.Source.Claims.Should().ContainSingle();
        var ticket = setlist ? consumption.Setlist!.Ticket : consumption.Recording!.Ticket;
        (setlist ? consumption.Recording == null : consumption.Setlist == null).Should().BeTrue();
        consumption.ProducerReadSet.Inputs.Should().BeEquivalentTo(ticket.Inputs);
        consumption
            .ProducerReadSet.CatalogInputs.Should()
            .BeEquivalentTo(ticket.CatalogInputs ?? []);
        consumption
            .ProducerReadSet.SourceIdentities.Should()
            .BeEquivalentTo(ticket.SourceIdentities ?? []);
        consumption.ProducerReadSet.SourceUuids.Should().BeEquivalentTo(ticket.SourceUuids ?? []);
        consumption.ProducerReadSet.MappingRegistry.Should().Be(ticket.MappingRegistry);
        consumption
            .ProducerReadSet.ImportedVenues.Should()
            .BeEquivalentTo(ticket.ImportedVenues ?? []);
        consumption
            .ProducerReadSet.CandidateUniverses.Should()
            .BeEquivalentTo(ticket.CandidateUniverses ?? []);
        consumption
            .ProducerReadSet.OwnerObservations.Should()
            .BeEquivalentTo(ticket.OwnerObservations ?? []);
        consumption
            .ProducerReadSet.Dependencies.Should()
            .BeEquivalentTo(
                ticket.Dependencies.Append(
                    new NormalizationSubjectDependency(
                        ticket.SubjectUuid,
                        ticket.DesiredGeneration,
                        ticket.EditorialRevision,
                        ticket.WorkUuid
                    )
                )
            );
    }

    [Test]
    public async Task UnrelatedGlobalCandidateChangeDoesNotRevokeCompletedAcceptedAssociation()
    {
        var account = await Dictionary(false);
        var before = await Read(account.AttemptUuid);
        var input = await fixture.Capture("Independent global work", "compositions");
        var uuid = Guid.NewGuid();
        var ticket = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("compositions", "composition", uuid),
            "consumption-fixture-v1",
            [input.Resource],
            []
        );
        (
            await music.PublishAsync(
                ticket.WorkUuid,
                new()
                {
                    Compositions =
                    [
                        new()
                        {
                            Uuid = uuid,
                            DisplayTitle = "Independent global work",
                            Evidence = [input.Reference],
                        },
                    ],
                }
            )
        ).Status.Should().Be(NormalizationPublicationStatus.Published);

        var after = await Read(account.AttemptUuid);

        after.AcknowledgmentTicket.Should().BeEquivalentTo(before.AcknowledgmentTicket);
        after.Decisions.Should().BeEquivalentTo(before.Decisions);
        after.Targets.Should().BeEquivalentTo(before.Targets);
    }

    [Test]
    public async Task ChangedOriginalInputHeadRejectsConsumptionWithoutChangingTheReceipt()
    {
        var account = await Dictionary(false);
        var original = await Read(account.AttemptUuid);
        var reference = original.InputReferences.Single();
        var dependency = original.ProducerReadSet.Inputs.Single();
        await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                reference.Resource,
                JsonSerializer.SerializeToUtf8Bytes(new { changed = true }),
                "application/json",
                NormalizationImportedCatalogService.RepertoireCaptureVersion,
                dependency.Generation,
                DateTimeOffset.UtcNow
            )
        );

        await FluentActions
            .Invoking(() => Read(account.AttemptUuid))
            .Should()
            .ThrowAsync<NormalizationConflictException>();
        var saved = await store.GetAsync(account.AttemptUuid);
        saved!.Result!.Disposition.Should().Be(RepertoireResolutionDisposition.Completed);
        saved
            .Stages.Single(value => value.Kind == RepertoireResolutionStageKind.AcknowledgeSource)
            .Receipt!.WorkUuid.Should()
            .Be(original.AcknowledgmentTicket.WorkUuid);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task NewerUnpublishedProducerConfigurationIsStaleDespiteTheOldPublishedPointer(
        bool setlist
    )
    {
        var completed = await Resolve(await PublishedTitle(setlist));
        var original = await Read(completed.AttemptUuid);
        var ticket = setlist ? original.Setlist!.Ticket : original.Recording!.Ticket;
        var subject = (await fixture.Work.GetSubjectAsync(ticket.SubjectUuid))!;
        await AdmitChangedConfiguration(subject.Subject, ticket);

        await FluentActions
            .Invoking(() => Read(completed.AttemptUuid))
            .Should()
            .ThrowAsync<NormalizationConflictException>();
        (await fixture.Work.GetSubjectAsync(ticket.SubjectUuid))!
            .PublishedWorkUuid.Should()
            .Be(ticket.WorkUuid);
    }

    [Test]
    public async Task NewerUnpublishedAcknowledgmentHasTypedPendingCoordinates()
    {
        var completed = await Dictionary(false);
        var original = await Read(completed.AttemptUuid);
        var ticket = original.AcknowledgmentTicket;
        var subject = new NormalizationSubjectKey(
            "songs",
            "repertoire_observation",
            original.Attempt.Registration.RegistrationUuid
        );
        await AdmitChangedConfiguration(subject, ticket);

        var failure = await FluentActions
            .Invoking(() => Read(completed.AttemptUuid))
            .Should()
            .ThrowAsync<CompletedRepertoireConsumptionPendingException>();

        failure.Which.AttemptUuid.Should().Be(completed.AttemptUuid);
        failure.Which.OwnerSubjectUuid.Should().Be(ticket.SubjectUuid);
    }

    [Test]
    public async Task CurrentAcceptedRevisionUsesHistoricalCitationWithoutRequiringItsRawHead()
    {
        var completed = await Dictionary(false);
        var before = await Read(completed.AttemptUuid);
        var target = before.Targets.Single();
        var input = await fixture.Capture("Reviewed retained title", "songs");
        var owner = before.AcceptedReadSet.OwnerObservations.Single();
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            owner.SubjectUuid,
            owner.EditorialRevision,
            "replace",
            new()
            {
                RepertoireSongs =
                [
                    target.Accepted.Value with
                    {
                        DisplayTitle = "Reviewed retained title",
                        Evidence = [input.Reference],
                        Basis = "reviewed",
                    },
                ],
            },
            "Independent accepted title with immutable citation",
            "consumption-fixture"
        );
        await fixture.Refresh(input, "Later independent bytes");

        var current = await Read(completed.AttemptUuid);

        current.Targets.Single().Accepted.Revision.Should().BeGreaterThan(target.Accepted.Revision);
        current.Targets.Single().Accepted.Value.Evidence.Should().Contain(input.Reference);
        current
            .AcceptedReadSet.OwnerObservations.Single()
            .EditorialRevision.Should()
            .BeGreaterThan(owner.EditorialRevision);
        current.ProducerReadSet.Should().BeEquivalentTo(before.ProducerReadSet);
    }

    [Test]
    public async Task CallerRepeatableReadKeepsOneSnapshotAcrossAcceptedRevisionChanges()
    {
        var completed = await Dictionary(false);
        await fixture.Db.WithWriteConnection(async connection =>
        {
            var c = (NpgsqlConnection)connection;
            await using var t = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead);
            await c.ExecuteAsync("SET TRANSACTION READ ONLY", transaction: t);
            var before = await store.ReadCompletedConsumptionAsync(
                c,
                t,
                completed.AttemptUuid,
                default
            );
            var target = before.Targets.Single();
            var owner = before.AcceptedReadSet.OwnerObservations.Single();
            await music.ApplyDecisionAsync(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                "replace",
                new()
                {
                    RepertoireSongs =
                    [
                        target.Accepted.Value with
                        {
                            Aliases = ["Reviewed alias"],
                            Basis = "reviewed",
                        },
                    ],
                },
                "Concurrent accepted revision",
                "consumption-fixture"
            );

            var same = await store.ReadCompletedConsumptionAsync(
                c,
                t,
                completed.AttemptUuid,
                default
            );

            same.Targets.Should().BeEquivalentTo(before.Targets);
            same.AcceptedReadSet.Should().BeEquivalentTo(before.AcceptedReadSet);
            await t.RollbackAsync();
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PublicationReauthenticationUsesItsExistingTransactionAndRejectsChangedAuthority(
        bool changed
    )
    {
        var completed = await Dictionary(false);
        var prepared = await Read(completed.AttemptUuid);
        if (changed)
        {
            var target = prepared.Targets.Single();
            var owner = prepared.AcceptedReadSet.OwnerObservations.Single();
            await music.ApplyDecisionAsync(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                "replace",
                new()
                {
                    RepertoireSongs =
                    [
                        target.Accepted.Value with
                        {
                            Aliases = ["New reviewed wording"],
                            Basis = "reviewed",
                        },
                    ],
                },
                "Change accepted authority after consumer preparation",
                "consumption-fixture"
            );
        }

        await fixture.Db.WithWriteConnection(async connection =>
        {
            var c = (NpgsqlConnection)connection;
            await using var t = await c.BeginTransactionAsync();
            Func<Task> reauthenticate = () =>
                store.RequireCompletedConsumptionAsync(c, t, prepared, default);
            if (changed)
            {
                await reauthenticate.Should().ThrowAsync<NormalizationConflictException>();
            }
            else
            {
                await reauthenticate.Should().NotThrowAsync();
            }
            await t.RollbackAsync();
        });
    }

    [Test]
    public async Task StoredAssociationFollowsCurrentSameArtistRedirectWithoutRewritingItsDecision()
    {
        var completed = await Dictionary(false);
        var before = await Read(completed.AttemptUuid);
        var original = before.Targets.Single();
        var artist = before.Source.ArtistUuid!.Value;
        var owner = before.AcceptedReadSet.OwnerObservations.Single();
        var survivorUuid = Guid.NewGuid();
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            owner.SubjectUuid,
            owner.EditorialRevision,
            "accept",
            new()
            {
                RepertoireSongs =
                [
                    new()
                    {
                        Uuid = survivorUuid,
                        ArtistUuid = artist,
                        DisplayTitle = original.Accepted.Value.DisplayTitle,
                        Evidence = original.Accepted.Value.Evidence,
                    },
                ],
            },
            "Independently accepted survivor",
            "consumption-fixture"
        );
        var assignment = (await music.GetImportedSongsAsync(original.CanonicalUuid)).Single();
        var identities = await fixture.Db.WithWriteConnection(async c =>
            (
                await c.QueryAsync<ExternalAssertion>(
                    """
                    SELECT r.payload::text AS Payload,r.revision AS Revision FROM normalization.music_external_identities x
                    JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                    WHERE x.repertoire_song_uuid=@uuid AND NOT x.retracted
                    """,
                    new { uuid = original.CanonicalUuid }
                )
            ).ToArray()
        );
        var currentOwner = (await fixture.Work.GetSubjectAsync(owner.SubjectUuid))!;
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            owner.SubjectUuid,
            currentOwner.EditorialRevision,
            "replace",
            new()
            {
                ImportedSongs =
                [
                    assignment.Value with
                    {
                        ExpectedRevision = assignment.Revision,
                        RepertoireSongUuid = survivorUuid,
                        Basis = "reviewed",
                    },
                ],
                ExternalIdentities = identities
                    .Select(value =>
                        JsonSerializer.Deserialize<MusicExternalIdentityChange>(value.Payload)! with
                        {
                            ExpectedRevision = value.Revision,
                            Retracted = true,
                            Basis = "reviewed",
                        }
                    )
                    .ToArray(),
            },
            "Move relationships before merging accepted identities",
            "consumption-fixture"
        );
        currentOwner = (await fixture.Work.GetSubjectAsync(owner.SubjectUuid))!;
        var survivor = (await music.GetRepertoireAsync(survivorUuid))!;
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            owner.SubjectUuid,
            currentOwner.EditorialRevision,
            "merge",
            new()
            {
                Merges =
                [
                    new(
                        "songs",
                        original.CanonicalUuid,
                        survivorUuid,
                        original.Accepted.Revision,
                        survivor.Revision
                    ),
                ],
            },
            "Reviewed equivalent repertoire identities",
            "consumption-fixture"
        );

        var current = await Read(completed.AttemptUuid);

        current.Decisions.Should().BeEquivalentTo(before.Decisions);
        current.Targets.Single().RequestedUuid.Should().Be(original.RequestedUuid);
        current.Targets.Single().CanonicalUuid.Should().Be(survivorUuid);
        current
            .Targets.Single()
            .IdentityPath.Select(value => value.Uuid)
            .Should()
            .Equal(original.CanonicalUuid, survivorUuid);
    }

    [TestCase(4, 3, false)]
    [TestCase(400, 250, true)]
    public async Task SharedRedirectSuffixReusesAcceptedPayloadAndBoundsAggregatePaths(
        int roots,
        int depth,
        bool exceedsBudget
    )
    {
        var completed = await SharedRedirectAccount(roots, depth);
        if (exceedsBudget)
        {
            // Only 650 distinct headers are present. The rejected allocation would return
            // 400 copies of a 251-entry path, exceeding the aggregate 100,000-entry limit.
            await FluentActions
                .Invoking(() => Read(completed.AttemptUuid))
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("*paths exceed 100,000 entries*");
            return;
        }

        var current = await Read(completed.AttemptUuid);

        current.Targets.Should().HaveCount(roots);
        current.Targets.Select(value => value.CanonicalUuid).Distinct().Should().ContainSingle();
        current.Targets.Should().OnlyContain(value => value.IdentityPath.Count == depth + 1);
        var accepted = current.Targets[0].Accepted.Value;
        current
            .Targets.Should()
            .OnlyContain(
                value => ReferenceEquals(value.Accepted.Value, accepted),
                "the bounded canonical payload and evidence are parsed once even when many roots converge"
            );
    }

    [Test]
    public async Task SharedCanonicalPayloadRefusesItsEncodedPerRootExpansionBeforeReturningTheSnapshot()
    {
        var completed = await SharedRedirectAccount(400, 1);
        var before = await Read(completed.AttemptUuid);
        var canonical = before.Targets[0].Accepted;
        var owner = before.AcceptedReadSet.OwnerObservations.Single();
        // Each alias fits ordinary name lengths. One accepted payload is about 1 MiB;
        // its 400 embedded copies would exceed the aggregate 256 MiB snapshot budget.
        var aliases = Enumerable
            .Range(0, 5500)
            .Select(index => index + new string('a', 190))
            .ToArray();
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            owner.SubjectUuid,
            owner.EditorialRevision,
            "replace",
            new()
            {
                RepertoireSongs = [canonical.Value with { Aliases = aliases, Basis = "reviewed" }],
            },
            "Accepted aliases retained once by a shared canonical identity",
            "consumption-fixture"
        );

        await FluentActions
            .Invoking(() => Read(completed.AttemptUuid))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*bounded snapshot budget*");
    }

    private async Task<RepertoireResolutionAttempt> SharedRedirectAccount(int rootCount, int depth)
    {
        var (request, artist) = await DictionaryRequest(false, rootCount);
        var completed = await Resolve(request);
        var before = await Read(completed.AttemptUuid);
        var owner = before.AcceptedReadSet.OwnerObservations.Single();
        var chain = Enumerable.Range(0, depth).Select(_ => Guid.NewGuid()).ToArray();
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            owner.SubjectUuid,
            owner.EditorialRevision,
            "accept",
            new()
            {
                RepertoireSongs = chain
                    .Select(uuid => new RepertoireSongChange
                    {
                        Uuid = uuid,
                        ArtistUuid = artist,
                        DisplayTitle = before.Targets[0].Accepted.Value.DisplayTitle,
                        Evidence = before.Targets[0].Accepted.Value.Evidence,
                    })
                    .ToArray(),
            },
            "Accepted equivalent identities for a shared redirect suffix",
            "consumption-fixture"
        );
        var roots = before.Targets.Select(value => value.CanonicalUuid).ToArray();
        var assignments = await fixture.Db.WithWriteConnection(async c =>
            (
                await c.QueryAsync<ExternalAssertion>(
                    """
                    SELECT r.payload::text AS Payload,r.revision AS Revision FROM normalization.imported_song_assignments x
                    JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                    WHERE x.repertoire_song_uuid=ANY(@roots) AND NOT x.retracted
                    """,
                    new { roots }
                )
            ).ToArray()
        );
        var identities = await fixture.Db.WithWriteConnection(async c =>
            (
                await c.QueryAsync<ExternalAssertion>(
                    """
                    SELECT r.payload::text AS Payload,r.revision AS Revision FROM normalization.music_external_identities x
                    JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                    WHERE x.repertoire_song_uuid=ANY(@roots) AND NOT x.retracted
                    """,
                    new { roots }
                )
            ).ToArray()
        );
        var merges = before
            .Targets.Select(value => new MusicMerge(
                "songs",
                value.CanonicalUuid,
                chain[0],
                value.Accepted.Revision,
                1
            ))
            .Concat(
                Enumerable
                    .Range(0, depth - 1)
                    .Select(index => new MusicMerge("songs", chain[index], chain[index + 1], 1, 1))
            )
            .ToArray();
        var currentOwner = (await fixture.Work.GetSubjectAsync(owner.SubjectUuid))!;
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            owner.SubjectUuid,
            currentOwner.EditorialRevision,
            "merge",
            new()
            {
                ImportedSongs = assignments
                    .Select(value =>
                        JsonSerializer.Deserialize<ImportedSongAssignmentChange>(
                            value.Payload
                        )! with
                        {
                            ExpectedRevision = value.Revision,
                            RepertoireSongUuid = chain[^1],
                            Basis = "reviewed",
                        }
                    )
                    .ToArray(),
                ExternalIdentities = identities
                    .Select(value =>
                        JsonSerializer.Deserialize<MusicExternalIdentityChange>(value.Payload)! with
                        {
                            ExpectedRevision = value.Revision,
                            Retracted = true,
                            Basis = "reviewed",
                        }
                    )
                    .ToArray(),
                Merges = merges,
            },
            "Explicitly move relationships and merge roots before extending their shared suffix",
            "consumption-fixture"
        );
        return completed;
    }

    private Task<CompletedRepertoireConsumption> Read(Guid uuid) =>
        fixture.Db.WithWriteConnection(async connection =>
        {
            var c = (NpgsqlConnection)connection;
            await using var t = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead);
            await c.ExecuteAsync("SET TRANSACTION READ ONLY", transaction: t);
            var result = await store.ReadCompletedConsumptionAsync(c, t, uuid, default);
            await t.RollbackAsync();
            return result;
        });

    private async Task AdmitChangedConfiguration(
        NormalizationSubjectKey subject,
        NormalizationWorkTicket original
    )
    {
        var extraResource = new NormalizationResourceKey(
            "test",
            "consumption",
            Guid.NewGuid().ToString("D")
        );
        var extra = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                extraResource,
                JsonSerializer.SerializeToUtf8Bytes(
                    new { text = "Unpublished configuration input" }
                ),
                "application/json",
                "test",
                0,
                DateTimeOffset.UtcNow
            )
        );
        // These owners already have contributor-managed input sets. Add a new contributor
        // through that contract so admission advances the generation without dropping peers.
        var admitted = await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            subject,
            original.PolicyVersion,
            new(extraResource, 0, [extraResource], []),
            original.DesiredGeneration,
            expectedInputRevisions: [new(extra.Revision.Uuid, extraResource)],
            expectedOwnerObservations: original.OwnerObservations
        );
        admitted.DesiredGeneration.Should().BeGreaterThan(original.DesiredGeneration);
        admitted.PolicyVersion.Should().Be(original.PolicyVersion);
    }

    private NormalizationRepertoireResolutionProcessor Processor()
    {
        var imported = new NormalizationImportedCatalogService(fixture.Db, evidence);
        var sources = new NormalizationRepertoireSourceService(
            fixture.Db,
            evidence,
            new(),
            imported,
            new(fixture.Db),
            new(fixture.Db),
            new(fixture.Db)
        );
        return new(
            fixture.Db,
            sources,
            new(fixture.Db),
            new(fixture.Db, fixture.Work, allowFixturePolicies: true),
            store,
            fixture.Work,
            music
        );
    }

    private async Task<RepertoireResolutionAttempt> Resolve(RepertoireResolutionRequest request)
    {
        var processor = Processor();
        var uuid = Guid.NewGuid();
        for (var step = 0; step < 100; step++)
        {
            var attempt = await processor.ResolveAsync(uuid, request);
            if (attempt.Result is { Disposition: not RepertoireResolutionDisposition.Pending })
            {
                attempt
                    .Result.Disposition.Should()
                    .Be(
                        RepertoireResolutionDisposition.Completed,
                        JsonSerializer.Serialize(attempt.Result)
                    );
                return attempt;
            }
        }
        throw new AssertionException("The fixture repertoire account did not complete.");
    }

    private async Task<RepertoireResolutionAttempt> Dictionary(bool empty)
    {
        var (request, _) = await DictionaryRequest(empty);
        return await Resolve(request);
    }

    private async Task<(RepertoireResolutionRequest Request, Guid ArtistUuid)> DictionaryRequest(
        bool empty,
        int songCount = 1
    )
    {
        var catalog = await fixture.Source();
        var artist = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>("SELECT uuid FROM artists WHERE id=@ArtistId", catalog)
        );
        if (!empty)
        {
            var songs = Enumerable
                .Range(0, songCount)
                .Select(_ => Guid.NewGuid())
                .Select(uuid => new
                {
                    catalog.ArtistId,
                    title = "T" + Guid.NewGuid().ToString("N"),
                    slug = uuid.ToString("D"),
                    key = uuid.ToString("D"),
                    uuid,
                })
                .ToArray();
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    """
                    INSERT INTO setlist_songs(artist_id,name,slug,upstream_identifier,uuid,updated_at)
                    VALUES(@ArtistId,@title,@slug,@key,@uuid,now())
                    """,
                    songs
                )
            );
        }
        var capture = (
            await new NormalizationImportedCatalogService(
                fixture.Db,
                evidence
            ).RefreshArtistRepertoireAsync(artist)
        )!;
        return (
            new(new ImportedRepertoireResolutionSource(capture.Capture.Fetch.FetchUuid)),
            artist
        );
    }

    private async Task<RepertoireResolutionRequest> PublishedTitle(bool setlist)
    {
        var catalog = await fixture.Source();
        var title = "T" + Guid.NewGuid().ToString("N");
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE source_tracks SET title=@title WHERE uuid=@TrackUuid",
                new { title, catalog.TrackUuid }
            )
        );
        var uuid = Guid.NewGuid();
        if (!setlist)
        {
            var processor = new NormalizationRecordingProcessor(
                new(fixture.Db, evidence),
                evidence,
                new(fixture.Db),
                new(),
                new(new(), new()),
                fixture.Work,
                new(fixture.Db),
                new(fixture.Db)
            );
            (await processor.ProcessAsync(uuid, catalog.SourceUuid))
                .Disposition.Should()
                .Be(NormalizationRecordingProcessingDisposition.Published);
            return new(new RecordingRepertoireResolutionSource(uuid));
        }
        // No artist mapping is invented. The published source is a complete unbound account,
        // and consumption must retain its negative repertoire decision and original registry clock.
        var artistKey = Guid.NewGuid().ToString("D");
        var accountKey = Guid.NewGuid().ToString("N");
        var resource = new NormalizationResourceKey(
            "setlist.fm",
            "setlist-page",
            artistKey + ":page=1"
        );
        var capture = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        page = 1,
                        itemsPerPage = 20,
                        total = 1,
                        setlist = new[]
                        {
                            new
                            {
                                id = accountKey,
                                eventDate = "17-10-2026",
                                artist = new
                                {
                                    mbid = artistKey,
                                    name = "Unbound fixture performer",
                                },
                                sets = new
                                {
                                    set = new[] { new { song = new[] { new { name = title } } } },
                                },
                            },
                        },
                    }
                ),
                "application/json",
                "setlist-fm-http-v1",
                0,
                DateTimeOffset.UtcNow
            )
        );
        var setlists = new NormalizationSetlistProcessor(
            new(fixture.Db),
            evidence,
            new(fixture.Db),
            new(),
            new(),
            fixture.Work
        );
        (
            await setlists.ProcessAsync(
                uuid,
                new(capture.Revision.Uuid, resource),
                new("setlist.fm", "setlist", accountKey)
            )
        )
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Published);
        return new(new SetlistRepertoireResolutionSource(uuid));
    }

    private Task<SnapshotCounts> Counts() =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<SnapshotCounts>(
                """
                SELECT (SELECT count(*) FROM normalization.work_subjects) AS Owners,
                    (SELECT count(*) FROM normalization.work_runs) AS Runs,
                    (SELECT count(*) FROM normalization.music_revisions) AS Revisions,
                    (SELECT count(*) FROM normalization.repertoire_resolution_associations) AS Decisions
                """
            )
        );

    private sealed record ExternalAssertion(string Payload, long Revision);

    private sealed record SnapshotCounts(long Owners, long Runs, long Revisions, long Decisions);
}
