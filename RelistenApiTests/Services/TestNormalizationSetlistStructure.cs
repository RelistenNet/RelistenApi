using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

/// <summary>Accepted set topology retains exact proposal accounts and explicit identity correspondence.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationSetlistStructure
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationSetlistProcessor processor = null!;
    private NormalizedMusicService music = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        var bindings = new NormalizationCatalogBindingService(fixture.Db);
        processor = new(new(fixture.Db), evidence, bindings, new(), new(), fixture.Work);
        music = new(fixture.Db, fixture.Work);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task CompleteStructureRetainsEmptyRepeatedSetsTapeAndTheEntireOriginalAccount()
    {
        var context = await Context();
        var structure = Structure(context);
        var media = await Playback(context);
        var ticket = await Admit(context, structure);

        var result = await music.PublishSetlistStructureAsync(ticket.WorkUuid, structure);

        result.Status.Should().Be(NormalizationPublicationStatus.Published);
        var accepted = (await music.GetSetlistStructureAsync(structure.Edition.Uuid))!;
        accepted.Value.Should().BeEquivalentTo(structure);
        accepted.Revision.Should().Be(1);
        accepted.Value.Sets.Select(set => set.Position).Should().Equal(1, 2, 3);
        accepted.Value.Sets.Select(set => set.Label).Should().Equal("First", null, "First");
        accepted.Value.Sets[1].EncoreNumber.Should().Be(1);
        accepted.Value.Sets.Select(set => set.Uuid).Should().OnlyHaveUniqueItems();
        accepted
            .Value.Occurrences.Select(occurrence => occurrence.OriginalText)
            .Should()
            .Equal("Song", "Song", "Reprise");
        accepted
            .Value.Occurrences.Select(occurrence => occurrence.SetUuid)
            .Should()
            .Equal(structure.Sets[0].Uuid, structure.Sets[0].Uuid, structure.Sets[2].Uuid);
        accepted
            .Value.Occurrences.Should()
            .OnlyContain(occurrence => occurrence.RepertoireSongUuid == null);
        var source = accepted.Value.Sources.Single().Account;
        source.Should().BeEquivalentTo(Proposal(context));
        source.Passages[0].Passage.Tape!.Value.Should().BeTrue();
        source
            .Passages[0]
            .Passage.Annotations.Single()
            .Text.Value.Should()
            .Be("Original tease wording");
        source
            .Passages[0]
            .Passage.PerformerAnnotations.Select(annotation => annotation.Kind)
            .Should()
            .Equal("cover_artist", "guest_artist");
        (await Playback(context)).Should().Be(media);
        (await music.PublishSetlistStructureAsync(ticket.WorkUuid, structure))
            .Replayed.Should()
            .BeTrue();
        (await music.GetSetlistStructureAsync(structure.Edition.Uuid))
            .Should()
            .BeEquivalentTo(accepted);
    }

    [TestCase("unknown_set")]
    [TestCase("unordered")]
    public async Task UnknownSetMembershipAndOrderingRemainUnknown(string representation)
    {
        var context = await Context(representation);
        var structure = Structure(context);
        var ticket = await Admit(context, structure);

        (await music.PublishSetlistStructureAsync(ticket.WorkUuid, structure))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);

        var accepted = (await music.GetSetlistStructureAsync(structure.Edition.Uuid))!.Value;
        accepted.Sets.Should().BeEmpty();
        accepted.Edition.Ordered.Should().Be(representation != "unordered");
        if (representation == "unknown_set")
        {
            accepted.Occurrences.Should().ContainSingle().Which.SetUuid.Should().BeNull();
            accepted.Occurrences.Single().Position.Should().Be(1);
        }
        else
        {
            accepted.Occurrences.Should().BeEmpty();
            accepted.Sources.Single().Account.Account.Ordered.Should().BeFalse();
        }
    }

    [Test]
    public async Task AcceptedStructureKeepsFootnotesAndQualifiedSetIdentitiesFromTheExactDetailAccount()
    {
        var context = await Context("phantasy");
        var structure = Structure(context);
        var ticket = await Admit(context, structure);

        (await music.PublishSetlistStructureAsync(ticket.WorkUuid, structure))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);

        var accepted = (await music.GetSetlistStructureAsync(structure.Edition.Uuid))!.Value;
        accepted.Sets.Should().OnlyContain(set => set.ProviderIdentity != null);
        var source = accepted.Sources.Single().Account;
        source.Should().BeEquivalentTo(Proposal(context));
        source
            .Passages[0]
            .Passage.FootnoteNumbers.Select(number => number.Value)
            .Should()
            .Equal(2, 99);
        source.Passages[0].Passage.Annotations.Single().Text.Value.Should().Be("Guest guitarist");
        source.Passages[0].Passage.Segue!.Value.Should().BeTrue();
        accepted.Occurrences[0].TransitionAfter.Should().Be("segue");
    }

    [TestCase("account")]
    [TestCase("input")]
    [TestCase("unpublished_attempt")]
    [TestCase("account_index")]
    [TestCase("forged_proposal")]
    [TestCase("missing_set")]
    [TestCase("missing_passage")]
    [TestCase("invented_target")]
    [TestCase("duplicate_passage")]
    public async Task AlteredSourcesAndIncompleteCorrespondenceCannotCreateAcceptedStructure(
        string fault
    )
    {
        var context = await Context();
        var original = Structure(context);
        var structure = Corrupt(original, fault);
        var ticket = await Admit(context, structure);
        Func<Task> publish = () => music.PublishSetlistStructureAsync(ticket.WorkUuid, structure);

        await publish.Should().ThrowAsync<ArgumentException>();

        (await music.GetSetlistStructureAsync(original.Edition.Uuid)).Should().BeNull();
        (await music.GetEditionAsync(original.Edition.Uuid)).Should().BeNull();
        (await music.GetOccurrencesAsync(original.Edition.Uuid)).Should().BeEmpty();
        (await fixture.Work.GetSubjectAsync(ticket.SubjectUuid))!
            .PublishedWorkUuid.Should()
            .BeNull();
    }

    [Test]
    public async Task ExplicitlyUnresolvedSourcePassagesRetainTheirClaimsWithoutInventingOccurrences()
    {
        var context = await Context();
        var original = Structure(context);
        var proposal = Proposal(context).Passages[0];
        var structure = original with
        {
            Occurrences = original.Occurrences.Skip(1).ToArray(),
            PassageCorrespondence = original.PassageCorrespondence.Skip(1).ToArray(),
            UnresolvedPassages =
            [
                new(0, proposal.ProposalUuid, "Unresolved passage interpretation"),
            ],
        };
        var ticket = await Admit(context, structure);

        (await music.PublishSetlistStructureAsync(ticket.WorkUuid, structure))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);

        var accepted = (await music.GetSetlistStructureAsync(structure.Edition.Uuid))!.Value;
        accepted.Sources.Single().Account.Passages.Should().HaveCount(3);
        accepted.Occurrences.Should().HaveCount(2);
        accepted
            .UnresolvedPassages.Should()
            .ContainSingle()
            .Which.ProposalUuid.Should()
            .Be(proposal.ProposalUuid);
        (await music.GetOccurrencesAsync(structure.Edition.Uuid)).Should().HaveCount(2);
    }

    [Test]
    public async Task ReorderingRevisedUnkeyedPassagesRequiresReviewAndKeepsTheExplicitOccurrenceIds()
    {
        var original = await Context();
        var first = Structure(original);
        var initial = await Admit(original, first);
        await music.PublishSetlistStructureAsync(initial.WorkUuid, first);
        var revised = await Revise(original, reverseFirstPair: true);
        var reordered = Structure(revised, first, reverseFirstPair: true);
        var automatic = await Admit(revised, reordered);
        Func<Task> inferCorrespondence = () =>
            music.PublishSetlistStructureAsync(automatic.WorkUuid, reordered);

        await inferCorrespondence.Should().ThrowAsync<ArgumentException>();
        (await music.GetSetlistStructureAsync(first.Edition.Uuid))!.Revision.Should().Be(1);

        var reviewed = await music.ApplySetlistStructureDecisionAsync(
            Guid.NewGuid(),
            automatic.SubjectUuid,
            0,
            reordered,
            "Retained evidence confirms the repeated passages were reordered",
            "test-operator"
        );

        reviewed.Replayed.Should().BeFalse();
        var accepted = (await music.GetSetlistStructureAsync(first.Edition.Uuid))!;
        accepted.Revision.Should().Be(2);
        accepted
            .Value.Occurrences.Select(occurrence => occurrence.Uuid)
            .Should()
            .Equal(first.Occurrences[1].Uuid, first.Occurrences[0].Uuid, first.Occurrences[2].Uuid);
        accepted
            .Value.Occurrences.Select(occurrence => occurrence.Position)
            .Should()
            .Equal(1, 2, 3);
        accepted
            .Value.Sets.Select(set => set.Uuid)
            .Should()
            .Equal(first.Sets.Select(set => set.Uuid));
        accepted.Value.Sources.Single().InputRevisionUuid.Should().Be(revised.Input.Uuid);
    }

    [Test]
    public async Task RemovedSetsAndPassagesRequireTombstonesAndRetainTheirHistory()
    {
        var original = await Context();
        var first = Structure(original);
        var initial = await Admit(original, first);
        await music.PublishSetlistStructureAsync(initial.WorkUuid, first);
        var revised = await Revise(original, removeLastSet: true);
        var incomplete = Structure(revised, first);
        var owner = (await fixture.Work.GetSubjectAsync(initial.SubjectUuid))!;
        var operationUuid = Guid.NewGuid();
        Func<Task> implicitDeletion = () =>
            music.ApplySetlistStructureDecisionAsync(
                operationUuid,
                owner.SubjectUuid,
                0,
                incomplete,
                "Remove the superseded final set",
                "test-operator"
            );

        await implicitDeletion.Should().ThrowAsync<ArgumentException>();
        (await music.GetSetlistStructureAsync(first.Edition.Uuid))!.Revision.Should().Be(1);
        (await fixture.Work.GetSubjectAsync(owner.SubjectUuid))!.EditorialRevision.Should().Be(0);

        var complete = incomplete with
        {
            Sets = [.. incomplete.Sets, first.Sets[2] with { Retracted = true }],
            Occurrences =
            [
                .. incomplete.Occurrences,
                first.Occurrences[2] with
                {
                    ExpectedRevision = 1,
                    Retracted = true,
                },
            ],
        };
        await music.ApplySetlistStructureDecisionAsync(
            operationUuid,
            owner.SubjectUuid,
            0,
            complete,
            "Explicitly retract superseded set and passage",
            "test-operator"
        );

        var accepted = (await music.GetSetlistStructureAsync(first.Edition.Uuid))!;
        accepted.Revision.Should().Be(2);
        accepted
            .Value.Sets.Should()
            .ContainSingle(set => set.Uuid == first.Sets[2].Uuid && set.Retracted);
        accepted
            .Value.Occurrences.Should()
            .ContainSingle(occurrence =>
                occurrence.Uuid == first.Occurrences[2].Uuid && occurrence.Retracted
            );
        (await music.GetOccurrencesAsync(first.Edition.Uuid)).Should().HaveCount(2);
        var history = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<OccurrenceHistory>(
                """
                SELECT o.retracted AS Retracted,r.revision AS Revision,
                    (SELECT count(*) FROM normalization.music_revisions WHERE entity_uuid=o.uuid) AS History
                FROM normalization.song_occurrences o JOIN normalization.music_revisions r ON r.uuid=o.revision_uuid WHERE o.uuid=@Uuid
                """,
                first.Occurrences[2]
            )
        );
        history.Should().Be(new OccurrenceHistory(true, 2, 2));
    }

    [TestCase("input")]
    [TestCase("catalog")]
    public async Task ChangesAfterAdmissionPublishNothingAndKeepTheOriginalProposalBatch(
        string changed
    )
    {
        var context = await Context();
        var structure = Structure(context);
        var ticket = await Admit(context, structure);
        if (changed == "input")
        {
            await Capture(
                context.Input.Resource,
                context.Input.CaptureVersion,
                context.Input.MediaType,
                Encoding.UTF8.GetString(context.Input.Payload)
            );
        }
        else
        {
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    "UPDATE artists SET name='Changed after admission' WHERE id=@ArtistId",
                    context.Source
                )
            );
        }

        var result = await music.PublishSetlistStructureAsync(ticket.WorkUuid, structure);

        result.Status.Should().Be(NormalizationPublicationStatus.Stale);
        (await music.GetSetlistStructureAsync(structure.Edition.Uuid)).Should().BeNull();
        (await processor.GetAsync(context.Batch.RegistrationUuid))!
            .AttemptUuid.Should()
            .Be(context.Batch.AttemptUuid);
    }

    [Test]
    public async Task ARepertoireOwnerCannotAcceptSetlistStructure()
    {
        var context = await Context();
        var structure = Structure(context);
        var ticket = await Admit(
            context,
            structure,
            owner: new("songs", "artist_repertoire", context.ArtistUuid)
        );
        Func<Task> wrongOwner = () =>
            music.PublishSetlistStructureAsync(ticket.WorkUuid, structure);

        await wrongOwner.Should().ThrowAsync<ArgumentException>();

        (await music.GetSetlistStructureAsync(structure.Edition.Uuid)).Should().BeNull();
    }

    [Test]
    public async Task AcceptanceRequiresTheCausalProposalOwnerDependency()
    {
        var context = await Context();
        var structure = Structure(context);
        var ticket = await Admit(context, structure, includeProposalOwner: false);
        Func<Task> unconsumed = () =>
            music.PublishSetlistStructureAsync(ticket.WorkUuid, structure);

        await unconsumed.Should().ThrowAsync<NormalizationConflictException>();

        (await music.GetSetlistStructureAsync(structure.Edition.Uuid)).Should().BeNull();
    }

    [Test]
    public async Task SameByteRecaptureCannotFreshenTheOriginalProposalsInputGeneration()
    {
        var context = await Context();
        var structure = Structure(context);
        var revision = await Capture(
            context.Input.Resource,
            context.Input.CaptureVersion,
            context.Input.MediaType,
            Encoding.UTF8.GetString(context.Input.Payload)
        );
        revision.Uuid.Should().Be(context.Input.Uuid);
        var ticket = await Admit(context, structure);
        ticket
            .Inputs.Single()
            .Generation.Should()
            .BeGreaterThan(context.Batch.Ticket.Inputs.Single().Generation);
        ticket.Dependencies.Single().PublishedWorkUuid.Should().Be(context.Batch.AttemptUuid);
        Func<Task> freshenedProof = () =>
            music.PublishSetlistStructureAsync(ticket.WorkUuid, structure);

        await freshenedProof.Should().ThrowAsync<NormalizationConflictException>();

        (await music.GetSetlistStructureAsync(structure.Edition.Uuid)).Should().BeNull();
        (await music.GetEditionAsync(structure.Edition.Uuid)).Should().BeNull();
    }

    [TestCase("edition")]
    [TestCase("occurrence")]
    public async Task LegacyPublicationCannotBypassAnAcceptedStructure(string changed)
    {
        var context = await Context();
        var structure = Structure(context);
        var first = await Admit(context, structure);
        await music.PublishSetlistStructureAsync(first.WorkUuid, structure);
        var next = await Admit(context, structure);
        var bypass =
            changed == "edition"
                ? new MusicPublication
                {
                    Editions =
                    [
                        structure.Edition with
                        {
                            ExpectedRevision = 1,
                            LiteralDate = "2001-01-01",
                        },
                    ],
                }
                : new MusicPublication
                {
                    Occurrences =
                    [
                        structure.Occurrences[0] with
                        {
                            ExpectedRevision = 1,
                            OriginalText = "Changed behind structure",
                        },
                    ],
                };
        Func<Task> publish = () => music.PublishAsync(next.WorkUuid, bypass);

        await publish.Should().ThrowAsync<ArgumentException>();

        (await music.GetSetlistStructureAsync(structure.Edition.Uuid))!.Revision.Should().Be(1);
        (await music.GetOccurrencesAsync(structure.Edition.Uuid))
            .Select(occurrence => occurrence.Value.OriginalText)
            .Should()
            .Equal("Song", "Song", "Reprise");
    }

    [TestCase("date")]
    [TestCase("transition")]
    [TestCase("set_label")]
    public async Task UnsupportedAutomaticMetadataRequiresAnExplicitReviewedInterpretation(
        string changed
    )
    {
        var context = await Context();
        var original = Structure(context);
        var structure = changed switch
        {
            "date" => original with
            {
                Edition = original.Edition with { LiteralDate = "1900-01-01" },
            },
            "transition" => original with
            {
                Occurrences =
                [
                    original.Occurrences[0] with
                    {
                        TransitionAfter = "segue",
                    },
                    .. original.Occurrences.Skip(1),
                ],
            },
            "set_label" => original with
            {
                Occurrences =
                [
                    original.Occurrences[0] with
                    {
                        SetLabel = "Invented set label",
                    },
                    .. original.Occurrences.Skip(1),
                ],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(changed)),
        };
        var ticket = await Admit(context, structure);
        Func<Task> unsupported = () =>
            music.PublishSetlistStructureAsync(ticket.WorkUuid, structure);

        await unsupported.Should().ThrowAsync<ArgumentException>();
        (await music.GetSetlistStructureAsync(structure.Edition.Uuid)).Should().BeNull();

        await music.ApplySetlistStructureDecisionAsync(
            Guid.NewGuid(),
            ticket.SubjectUuid,
            0,
            structure,
            "Reviewed interpretation differs from the source metadata",
            "test-operator"
        );

        var accepted = (await music.GetSetlistStructureAsync(structure.Edition.Uuid))!.Value;
        accepted.Edition.LiteralDate.Should().Be(structure.Edition.LiteralDate);
        accepted
            .Occurrences[0]
            .TransitionAfter.Should()
            .Be(structure.Occurrences[0].TransitionAfter);
        accepted.Occurrences[0].SetLabel.Should().Be(structure.Occurrences[0].SetLabel);
        accepted.Sources.Single().Account.Should().BeEquivalentTo(Proposal(context));
    }

    [Test]
    public async Task GenericPublicationFreezesNestedCallerCollectionsBeforeItsFirstAwait()
    {
        var context = await Context();
        var original = Structure(context);
        var occurrences = original.Occurrences.ToList();
        var proposal = original.Sources.Single().Account;
        var proposalPassages = proposal.Passages.ToList();
        var rawPassages = proposal.Account.Passages.ToList();
        var rawSets = proposal.Account.Sets.ToList();
        var source = original.Sources.Single() with
        {
            Account = proposal with
            {
                Passages = proposalPassages,
                Account = proposal.Account with { Passages = rawPassages, Sets = rawSets },
            },
        };
        var callerStructure = original with { Occurrences = occurrences, Sources = [source] };
        var publication = new MusicPublication
        {
            Editions = [callerStructure.Edition],
            Occurrences = occurrences,
            SetlistStructure = callerStructure,
        };
        var expected = JsonSerializer.Deserialize<SetlistStructureChange>(
            JsonSerializer.Serialize(callerStructure)
        )!;
        var ticket = await Admit(context, callerStructure);
        await using var blocker = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key,21))",
            new { key = occurrences[0].Uuid.ToString() },
            transaction
        );
        var publishing = music.PublishAsync(ticket.WorkUuid, publication);
        try
        {
            await RequireBlockedBy(blocker.ProcessID);
            occurrences[0] = occurrences[0] with
            {
                OriginalText = "Mutated after publication started",
            };
            proposalPassages[0] = proposalPassages[0] with
            {
                Passage = proposalPassages[0].Passage with
                {
                    Title = proposalPassages[0].Passage.Title with { Value = "Mutated proposal" },
                },
            };
            rawPassages[0] = rawPassages[0] with
            {
                Title = rawPassages[0].Title with { Value = "Mutated raw account" },
            };
            rawSets[0] = rawSets[0] with
            {
                Label = rawSets[0].Label! with { Value = "Mutated raw set" },
            };
        }
        finally
        {
            await transaction.RollbackAsync();
            await publishing;
        }

        (await publishing).Status.Should().Be(NormalizationPublicationStatus.Published);
        (await music.GetSetlistStructureAsync(original.Edition.Uuid))!
            .Value.Should()
            .BeEquivalentTo(expected);
        (await music.GetOccurrencesAsync(original.Edition.Uuid))
            .First()
            .Value.OriginalText.Should()
            .Be("Song");
    }

    [Test]
    public async Task QualifiedEditionAccountCannotChangeToAnotherPublishedAccountAtTheSameInputCoordinates()
    {
        var initial = await Context();
        var page = JsonNode.Parse(initial.Input.Payload)!.AsObject();
        var accounts = page["setlist"]!.AsArray();
        var other = accounts[0]!.DeepClone();
        var otherKey = "other-account-" + Guid.NewGuid().ToString("N");
        other["id"] = otherKey;
        accounts.Add(other);
        page["total"] = 2;
        var input = await Capture(
            initial.Input.Resource,
            initial.Input.CaptureVersion,
            initial.Input.MediaType,
            page.ToJsonString()
        );
        var firstAttempt = await processor.ProcessAsync(
            Guid.NewGuid(),
            new(input.Uuid, input.Resource),
            initial.AccountIdentity
        );
        var firstContext = initial with
        {
            Input = input,
            Batch = (await processor.GetAsync(firstAttempt.Registration.RegistrationUuid))!,
        };
        var first = Structure(firstContext);
        var ticket = await Admit(firstContext, first);
        await music.PublishSetlistStructureAsync(ticket.WorkUuid, first);
        var otherIdentity = initial.AccountIdentity with { ProviderKey = otherKey };
        var otherAttempt = await processor.ProcessAsync(
            Guid.NewGuid(),
            new(input.Uuid, input.Resource),
            otherIdentity
        );
        var otherContext = initial with
        {
            Input = input,
            AccountIdentity = otherIdentity,
            Batch = (await processor.GetAsync(otherAttempt.Registration.RegistrationUuid))!,
        };
        var redirected = Structure(otherContext, first);
        redirected
            .Sources.Single()
            .InputRevisionUuid.Should()
            .Be(first.Sources.Single().InputRevisionUuid);
        redirected.Sources.Single().AccountIndex.Should().Be(first.Sources.Single().AccountIndex);
        Func<Task> changeAccount = () =>
            music.ApplySetlistStructureDecisionAsync(
                Guid.NewGuid(),
                ticket.SubjectUuid,
                0,
                redirected,
                "Attempt to reuse the edition for another provider account",
                "test-operator"
            );

        await changeAccount.Should().ThrowAsync<ArgumentException>();

        var accepted = (await music.GetSetlistStructureAsync(first.Edition.Uuid))!;
        accepted.Revision.Should().Be(1);
        accepted.Value.Edition.ProviderKey.Should().Be(initial.AccountIdentity.ProviderKey);
        accepted
            .Value.Sources.Single()
            .Account.Account.Origin.Item.Should()
            .Be(initial.AccountIdentity);
        (await fixture.Work.GetSubjectAsync(ticket.SubjectUuid))!.EditorialRevision.Should().Be(0);
    }

    [TestCase("different_set")]
    [TestCase("retired_parent")]
    public async Task ReviewedComponentsRequireAnActiveRootParentAndConsistentKnownSetMembership(
        string fault
    )
    {
        var context = await Context();
        var original = Structure(context);
        var root = original.Occurrences[0];
        var component = root with
        {
            Uuid = Guid.NewGuid(),
            Position = null,
            ParentOccurrenceUuid = root.Uuid,
            ComponentPosition = 1,
            SetUuid = fault == "different_set" ? original.Sets[1].Uuid : root.SetUuid,
        };
        var structure = original with
        {
            Occurrences =
            [
                .. original.Occurrences.Select(occurrence =>
                    occurrence.Uuid == root.Uuid && fault == "retired_parent"
                        ? occurrence with
                        {
                            Retracted = true,
                        }
                        : occurrence
                ),
                component,
            ],
            PassageCorrespondence =
            [
                original.PassageCorrespondence[0] with
                {
                    OccurrenceUuids = [root.Uuid, component.Uuid],
                },
                .. original.PassageCorrespondence.Skip(1),
            ],
        };
        var ticket = await Admit(context, structure);
        var operationUuid = Guid.NewGuid();
        Func<Task> invalidComponent = () =>
            music.ApplySetlistStructureDecisionAsync(
                operationUuid,
                ticket.SubjectUuid,
                0,
                structure,
                "Split the retained passage into explicit components",
                "test-operator"
            );

        await invalidComponent.Should().ThrowAsync<ArgumentException>().WithMessage("*component*");
        (await music.GetSetlistStructureAsync(structure.Edition.Uuid)).Should().BeNull();
        (await music.GetOccurrencesAsync(structure.Edition.Uuid)).Should().BeEmpty();

        var corrected = structure with
        {
            Occurrences = [.. original.Occurrences, component with { SetUuid = root.SetUuid }],
        };
        await music.ApplySetlistStructureDecisionAsync(
            operationUuid,
            ticket.SubjectUuid,
            0,
            corrected,
            "Reviewed component belongs to its active root and set",
            "test-operator"
        );

        var accepted = (await music.GetSetlistStructureAsync(structure.Edition.Uuid))!.Value;
        var child = accepted.Occurrences.Single(occurrence => occurrence.Uuid == component.Uuid);
        child.ParentOccurrenceUuid.Should().Be(root.Uuid);
        child.ComponentPosition.Should().Be(1);
        child.Position.Should().BeNull();
        child.SetUuid.Should().Be(root.SetUuid);
        accepted
            .Occurrences.Single(occurrence => occurrence.Uuid == root.Uuid)
            .Retracted.Should()
            .BeFalse();
        accepted.PassageCorrespondence[0].OccurrenceUuids.Should().Equal(root.Uuid, component.Uuid);
    }

    [Test]
    public async Task APublishedHistoricalProcessingVersionCannotEstablishCurrentStructureProof()
    {
        var context = await Context();
        var store = new NormalizationSetlistProposalService(fixture.Db);
        const string historicalVersion = "setlist-processing-historical-v0";
        var attempt = await store.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            new(new(context.Input.Uuid, context.Input.Resource), context.AccountIdentity),
            historicalVersion
        );
        var owner = new NormalizationSubjectKey(
            "extractions",
            "setlist",
            attempt.Registration.RegistrationUuid
        );
        var membership = await fixture.Work.GetInputContributionsAsync(owner);
        var preparedInputs = context
            .Batch.Preparation.Inputs.Select(input =>
                input with
                {
                    ExpectedContributionRevision = membership
                        .Contributions.Single(state => state.Contributor == input.Contributor)
                        .Revision,
                }
            )
            .ToArray();
        var preparation = new NormalizationSetlistPreparation(
            membership.Owner!.DesiredGeneration,
            membership
                .Contributions.Select(state => new NormalizationInputContributionRevision(
                    state.Contributor,
                    state.Revision
                ))
                .ToArray(),
            preparedInputs
        );
        await store.SavePreparationAsync(attempt.AttemptUuid, preparation);
        var originalTicket = await fixture.Work.BeginContributedBatchAsync(
            attempt.AttemptUuid,
            owner,
            historicalVersion,
            NormalizationSetlistReadSet.Contributions(preparation),
            preparation.ExpectedConfigurationGeneration,
            expectedContributionRevisions: preparation.ExpectedContributions
        );
        var historical = new NormalizationSetlistProposalBatch(
            attempt.AttemptUuid,
            attempt.Registration.RegistrationUuid,
            historicalVersion,
            preparation,
            originalTicket,
            context.Batch.Extractions
        );
        await store.SaveExtractionAsync(historical);
        (
            await fixture.Work.TryPublishAsync(
                originalTicket.WorkUuid,
                (connection, transaction, ct) =>
                    store.WritePublishedAsync(connection, transaction, historical, ct)
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        (await store.GetAttemptAsync(attempt.AttemptUuid))!
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Published);

        var historicalContext = context with { Batch = historical };
        var structure = Structure(historicalContext);
        var ticket = await Admit(historicalContext, structure);
        Func<Task> unsupportedProof = () =>
            music.PublishSetlistStructureAsync(ticket.WorkUuid, structure);

        await unsupportedProof.Should().ThrowAsync<NotSupportedException>();

        (await music.GetSetlistStructureAsync(structure.Edition.Uuid)).Should().BeNull();
        (await music.GetEditionAsync(structure.Edition.Uuid)).Should().BeNull();
        (await music.GetOccurrencesAsync(structure.Edition.Uuid)).Should().BeEmpty();
    }

    [Test]
    public async Task ReviewedWithdrawalRetractsRootsComponentsAndSetsAndReplaysTheExactDecision()
    {
        var context = await Context();
        var original = Structure(context);
        var root = original.Occurrences[0];
        var component = root with
        {
            Uuid = Guid.NewGuid(),
            Position = null,
            ParentOccurrenceUuid = root.Uuid,
            ComponentPosition = 1,
        };
        var structure = original with
        {
            Occurrences = [.. original.Occurrences, component],
            PassageCorrespondence =
            [
                original.PassageCorrespondence[0] with
                {
                    OccurrenceUuids = [root.Uuid, component.Uuid],
                },
                .. original.PassageCorrespondence.Skip(1),
            ],
        };
        var ticket = await Admit(context, structure);
        await music.ApplySetlistStructureDecisionAsync(
            Guid.NewGuid(),
            ticket.SubjectUuid,
            0,
            structure,
            "Reviewed source passage includes this component",
            "test-operator"
        );
        var withdrawn = structure with
        {
            ExpectedRevision = 1,
            Retracted = true,
            Edition = structure.Edition with { ExpectedRevision = 1, Retracted = true },
            Sets = structure.Sets.Select(set => set with { Retracted = true }).ToArray(),
            Occurrences = structure
                .Occurrences.Select(occurrence =>
                    occurrence with
                    {
                        ExpectedRevision = 1,
                        Retracted = true,
                    }
                )
                .ToArray(),
        };
        var operationUuid = Guid.NewGuid();
        const string reason = "Withdraw the complete edition, including its component history";

        var result = await music.ApplySetlistStructureDecisionAsync(
            operationUuid,
            ticket.SubjectUuid,
            1,
            withdrawn,
            reason,
            "test-operator"
        );
        var replay = await music.ApplySetlistStructureDecisionAsync(
            operationUuid,
            ticket.SubjectUuid,
            1,
            withdrawn,
            reason,
            "test-operator"
        );

        result.Replayed.Should().BeFalse();
        replay.Replayed.Should().BeTrue();
        (await music.GetSetlistStructureAsync(structure.Edition.Uuid)).Should().BeNull();
        (await music.GetEditionAsync(structure.Edition.Uuid)).Should().BeNull();
        (await music.GetOccurrencesAsync(structure.Edition.Uuid)).Should().BeEmpty();
        var retained = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<WithdrawalHistory>(
                """
                SELECT r.revision AS Revision,s.retracted AS Retracted,
                    (SELECT count(*) FROM normalization.music_revisions WHERE entity_uuid=s.uuid) AS History,
                    (SELECT count(*) FROM normalization.setlist_sets WHERE edition_uuid=s.edition_uuid AND NOT retracted) AS ActiveSets,
                    (SELECT count(*) FROM normalization.song_occurrences WHERE edition_uuid=s.edition_uuid AND NOT retracted) AS ActiveOccurrences
                FROM normalization.setlist_structures s JOIN normalization.music_revisions r ON r.uuid=s.revision_uuid WHERE s.uuid=@Uuid
                """,
                structure
            )
        );
        retained.Should().Be(new WithdrawalHistory(2, true, 2, 0, 0));
        (await fixture.Work.GetSubjectAsync(ticket.SubjectUuid))!.EditorialRevision.Should().Be(2);

        var reaffirmed = withdrawn with
        {
            ExpectedRevision = 2,
            Edition = withdrawn.Edition with { ExpectedRevision = 2 },
            Occurrences = withdrawn
                .Occurrences.Select(occurrence => occurrence with { ExpectedRevision = 2 })
                .ToArray(),
        };
        var repeatedReview = await music.ApplySetlistStructureDecisionAsync(
            Guid.NewGuid(),
            ticket.SubjectUuid,
            2,
            reaffirmed,
            "Confirm the edition and its inactive component remain withdrawn",
            "test-operator"
        );
        repeatedReview.Replayed.Should().BeFalse();
        (await fixture.Work.GetSubjectAsync(ticket.SubjectUuid))!.EditorialRevision.Should().Be(3);
        (await music.GetEditionAsync(structure.Edition.Uuid)).Should().BeNull();
        (await music.GetOccurrencesAsync(structure.Edition.Uuid)).Should().BeEmpty();
    }

    [TestCase("set")]
    [TestCase("occurrence")]
    public async Task AnExactSourceClaimCannotAutomaticallyReplaceItsPreviouslyAcceptedTarget(
        string target
    )
    {
        var context = await Context();
        var first = Structure(context);
        await music.PublishSetlistStructureAsync((await Admit(context, first)).WorkUuid, first);
        var unchanged = Structure(context, first);
        var replacement =
            target == "set"
                ? ReallocateSet(unchanged, 0)
                : unchanged with
                {
                    Occurrences =
                    [
                        unchanged.Occurrences[0] with
                        {
                            Uuid = Guid.NewGuid(),
                            ExpectedRevision = 0,
                        },
                        .. unchanged.Occurrences.Skip(1),
                        unchanged.Occurrences[0] with
                        {
                            Retracted = true,
                        },
                    ],
                };
        if (target == "occurrence")
        {
            replacement = replacement with
            {
                PassageCorrespondence =
                [
                    replacement.PassageCorrespondence[0] with
                    {
                        OccurrenceUuids = [replacement.Occurrences[0].Uuid],
                    },
                    .. replacement.PassageCorrespondence.Skip(1),
                ],
            };
        }
        var ticket = await Admit(context, replacement);
        Func<Task> reallocate = () =>
            music.PublishSetlistStructureAsync(ticket.WorkUuid, replacement);

        await reallocate
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("*stable target UUID*");
        (await music.GetSetlistStructureAsync(first.Edition.Uuid))!.Revision.Should().Be(1);

        (await music.PublishSetlistStructureAsync(ticket.WorkUuid, unchanged))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        var accepted = (await music.GetSetlistStructureAsync(first.Edition.Uuid))!.Value;
        accepted.Sets.Select(set => set.Uuid).Should().Equal(first.Sets.Select(set => set.Uuid));
        accepted
            .Occurrences.Select(occurrence => occurrence.Uuid)
            .Should()
            .Equal(first.Occurrences.Select(occurrence => occurrence.Uuid));
    }

    [Test]
    public async Task AQualifiedProviderSetIdentityKeepsItsAcceptedTargetAcrossInputRevisions()
    {
        var context = await Context("phantasy");
        var first = KeepPassagesUnresolved(Structure(context));
        await music.PublishSetlistStructureAsync((await Admit(context, first)).WorkUuid, first);
        var payload = JsonNode.Parse(context.Input.Payload)!;
        payload["data"]!["prenotes"] = "New retained editorial notes";
        var input = await Capture(
            context.Input.Resource,
            context.Input.CaptureVersion,
            context.Input.MediaType,
            payload.ToJsonString()
        );
        input.Uuid.Should().NotBe(context.Input.Uuid);
        var attempt = await processor.ProcessAsync(
            Guid.NewGuid(),
            new(input.Uuid, input.Resource),
            context.AccountIdentity
        );
        var revised = context with
        {
            Input = input,
            Batch = (await processor.GetAsync(attempt.Registration.RegistrationUuid))!,
        };
        var retained = KeepPassagesUnresolved(Structure(revised, first));
        retained
            .Sets[1]
            .ProviderIdentity.Should()
            .Be(first.Sets[1].ProviderIdentity)
            .And.NotBeNull();
        Proposal(revised).Account.Sets[1].Ordinal.Should().Be(2);
        Proposal(revised).Passages.Should().NotContain(passage => passage.Passage.SetOrdinal == 2);
        var replacement = ReallocateSet(retained, 1);
        var ticket = await Admit(revised, replacement);
        Func<Task> reallocate = () =>
            music.PublishSetlistStructureAsync(ticket.WorkUuid, replacement);

        await reallocate
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("*stable target UUID*");
        (await music.GetSetlistStructureAsync(first.Edition.Uuid))!.Revision.Should().Be(1);

        (await music.PublishSetlistStructureAsync(ticket.WorkUuid, retained))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        var accepted = (await music.GetSetlistStructureAsync(first.Edition.Uuid))!.Value;
        accepted.Sets.Select(set => set.Uuid).Should().Equal(first.Sets.Select(set => set.Uuid));
        accepted.Sources.Single().InputRevisionUuid.Should().Be(input.Uuid);
        accepted.Occurrences.Should().BeEmpty();
        accepted.UnresolvedPassages.Should().HaveCount(2);
    }

    [TestCase("false", "unknown")]
    [TestCase("", "unknown")]
    [TestCase("maybe", "unknown")]
    [TestCase(">", "segue")]
    [TestCase("->", "segue")]
    [TestCase("→", "segue")]
    [TestCase("->>", "segue")]
    public async Task RawStringTransitionMarkersRetainTheirWordingAndRequireRecognizedSegueSyntax(
        string marker,
        string expected
    )
    {
        var context = await Context("phantasy", marker);
        var original = Structure(context);
        var structure = original with
        {
            Occurrences =
            [
                original.Occurrences[0] with
                {
                    TransitionAfter = expected,
                },
                .. original.Occurrences.Skip(1),
            ],
        };
        var ticket = await Admit(context, structure);
        if (expected == "unknown")
        {
            var invented = structure with
            {
                Occurrences =
                [
                    structure.Occurrences[0] with
                    {
                        TransitionAfter = "segue",
                    },
                    .. structure.Occurrences.Skip(1),
                ],
            };
            Func<Task> inferSegue = () =>
                music.PublishSetlistStructureAsync(ticket.WorkUuid, invented);
            await inferSegue.Should().ThrowAsync<ArgumentException>();
        }

        (await music.PublishSetlistStructureAsync(ticket.WorkUuid, structure))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);

        var accepted = (await music.GetSetlistStructureAsync(structure.Edition.Uuid))!.Value;
        accepted.Occurrences[0].TransitionAfter.Should().Be(expected);
        accepted
            .Sources.Single()
            .Account.Passages[0]
            .Passage.TransitionMarker!.Value.Should()
            .Be(marker);
    }

    [Test]
    public async Task ReviewedWithdrawalPreservesAnExistingBindingToARetractedRepertoireSong()
    {
        var context = await Context();
        var song = RepertoireSong(context);
        var repertoire = await PublishSongs(context, song);
        var original = Structure(context);
        var structure = original with
        {
            Occurrences =
            [
                original.Occurrences[0] with
                {
                    RepertoireSongUuid = song.Uuid,
                },
                .. original.Occurrences.Skip(1),
            ],
        };
        var ticket = await Admit(
            context,
            structure,
            additionalDependencies: [repertoire.SubjectUuid]
        );
        await music.PublishSetlistStructureAsync(ticket.WorkUuid, structure);
        await PublishSongs(context, song with { ExpectedRevision = 1, Retracted = true });
        (await music.GetRepertoireAsync(song.Uuid)).Should().BeNull();

        await music.ApplySetlistStructureDecisionAsync(
            Guid.NewGuid(),
            ticket.SubjectUuid,
            0,
            Withdrawn(structure, 1),
            "Withdraw the occurrence while retaining its historical song binding",
            "test-operator"
        );

        (await music.GetSetlistStructureAsync(structure.Edition.Uuid)).Should().BeNull();
        (await music.GetOccurrencesAsync(structure.Edition.Uuid)).Should().BeEmpty();
        (await StoredSongBinding(structure.Occurrences[0].Uuid)).Should().Be(song.Uuid);
    }

    [Test]
    public async Task ReaffirmedTombstonesPreserveTheirOriginalBindingAfterTheSongWasMerged()
    {
        var context = await Context();
        var originalSong = RepertoireSong(context);
        var survivor = RepertoireSong(context);
        var repertoire = await PublishSongs(context, originalSong, survivor);
        var original = Structure(context);
        var structure = original with
        {
            Occurrences =
            [
                original.Occurrences[0] with
                {
                    RepertoireSongUuid = originalSong.Uuid,
                },
                .. original.Occurrences.Skip(1),
            ],
        };
        var ticket = await Admit(
            context,
            structure,
            additionalDependencies: [repertoire.SubjectUuid]
        );
        await music.PublishSetlistStructureAsync(ticket.WorkUuid, structure);
        await music.ApplySetlistStructureDecisionAsync(
            Guid.NewGuid(),
            ticket.SubjectUuid,
            0,
            Withdrawn(structure, 1),
            "Withdraw the accepted song occurrence before merging its repertoire identity",
            "test-operator"
        );
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            repertoire.SubjectUuid,
            0,
            "merge",
            new MusicPublication
            {
                Merges = [new("songs", originalSong.Uuid, survivor.Uuid, 1, 1)],
            },
            "The retired repertoire song is the same documented song",
            "test-operator"
        );
        (await music.GetRepertoireAsync(originalSong.Uuid))!.Value.Uuid.Should().Be(survivor.Uuid);

        await music.ApplySetlistStructureDecisionAsync(
            Guid.NewGuid(),
            ticket.SubjectUuid,
            1,
            Withdrawn(structure, 2),
            "Confirm the unchanged historical occurrence remains withdrawn",
            "test-operator"
        );

        (await music.GetOccurrencesAsync(structure.Edition.Uuid)).Should().BeEmpty();
        (await StoredSongBinding(structure.Occurrences[0].Uuid)).Should().Be(originalSong.Uuid);
        (await fixture.Work.GetSubjectAsync(ticket.SubjectUuid))!.EditorialRevision.Should().Be(2);
    }

    private static RepertoireSongChange RepertoireSong(ContextState context) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            ArtistUuid = context.ArtistUuid,
            DisplayTitle = "Song",
            Evidence = [Proposal(context).Passages[0].Passage.Title.Evidence],
        };

    private async Task<NormalizationWorkTicket> PublishSongs(
        ContextState context,
        params RepertoireSongChange[] songs
    )
    {
        var subject = new NormalizationSubjectKey("songs", "artist_repertoire", context.ArtistUuid);
        var current = await fixture.Work.GetSubjectAsync(subject);
        var ticket = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            "structure-repertoire-test-v1",
            [context.Input.Resource],
            [],
            current?.DesiredGeneration
        );
        await music.PublishAsync(ticket.WorkUuid, new MusicPublication { RepertoireSongs = songs });
        return ticket;
    }

    private static SetlistStructureChange Withdrawn(
        SetlistStructureChange structure,
        long expectedRevision
    ) =>
        structure with
        {
            ExpectedRevision = expectedRevision,
            Retracted = true,
            Edition = structure.Edition with
            {
                ExpectedRevision = expectedRevision,
                Retracted = true,
            },
            Sets = structure.Sets.Select(set => set with { Retracted = true }).ToArray(),
            Occurrences = structure
                .Occurrences.Select(occurrence =>
                    occurrence with
                    {
                        ExpectedRevision = expectedRevision,
                        Retracted = true,
                    }
                )
                .ToArray(),
        };

    private Task<Guid> StoredSongBinding(Guid uuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<Guid>(
                "SELECT repertoire_song_uuid FROM normalization.song_occurrences WHERE uuid=@uuid AND retracted",
                new { uuid }
            )
        );

    private static SetlistStructureChange KeepPassagesUnresolved(
        SetlistStructureChange structure
    ) =>
        structure with
        {
            Occurrences = [],
            PassageCorrespondence = [],
            UnresolvedPassages = structure
                .Sources.SelectMany(
                    (source, index) =>
                        source.Account.Passages.Select(passage => new SetlistUnresolvedPassage(
                            index,
                            passage.ProposalUuid,
                            "Retained passage has no accepted occurrence correspondence"
                        ))
                )
                .ToArray(),
        };

    private static SetlistStructureChange ReallocateSet(SetlistStructureChange structure, int index)
    {
        var original = structure.Sets[index];
        var replacementUuid = Guid.NewGuid();
        return structure with
        {
            Sets =
            [
                .. structure.Sets.Select(set =>
                    set.Uuid == original.Uuid ? set with { Uuid = replacementUuid } : set
                ),
                original with
                {
                    Retracted = true,
                },
            ],
            Occurrences = structure
                .Occurrences.Select(occurrence =>
                    occurrence.SetUuid == original.Uuid
                        ? occurrence with
                        {
                            SetUuid = replacementUuid,
                        }
                        : occurrence
                )
                .ToArray(),
            SetCorrespondence = structure
                .SetCorrespondence.Select(map =>
                    map.SetUuid == original.Uuid ? map with { SetUuid = replacementUuid } : map
                )
                .ToArray(),
        };
    }

    private async Task RequireBlockedBy(int pid)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (
                await fixture.Db.WithWriteConnection(c =>
                    c.ExecuteScalarAsync<bool>(
                        "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE @pid=ANY(pg_blocking_pids(pid)))",
                        new { pid }
                    )
                )
            )
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail("The publication did not reach the expected entity lock wait.");
    }

    private async Task<ContextState> Context(
        string representation = "setlist_fm",
        string? transitionMarker = null
    )
    {
        var source = await fixture.Source();
        var artistUuid = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<Guid>("SELECT uuid FROM artists WHERE id=@ArtistId", source)
        );
        var artistKey = Guid.NewGuid().ToString("D");
        var accountKey = "structure-" + Guid.NewGuid().ToString("N");
        NormalizationInputRevision input;
        if (representation == "setlist_fm")
        {
            await Map(source, "setlist.fm", artistKey);
            input = await Page(artistKey, accountKey);
        }
        else if (representation == "phantasy")
        {
            var showId = Random.Shared.Next(1, int.MaxValue);
            var bandId = Random.Shared.Next(1, int.MaxValue);
            artistKey = bandId.ToString();
            await Map(source, "phantasytour.com", artistKey);
            var detail = new
            {
                data = new
                {
                    id = showId,
                    dateTime = "2000-01-01T20:00:00-05:00",
                    band = new { id = bandId, name = "Band" },
                    footnotes = new[] { new { id = 2, note = "Guest guitarist" } },
                    sets = new object[]
                    {
                        new
                        {
                            id = 1,
                            name = "First",
                            songs = new object[]
                            {
                                new
                                {
                                    id = 9,
                                    name = "Song",
                                    segue = transitionMarker == null
                                        ? (object)true
                                        : transitionMarker,
                                    footnotes = new[] { 2, 99 },
                                },
                                new
                                {
                                    id = 9,
                                    name = "Song",
                                    segue = false,
                                },
                            },
                        },
                        new { id = 2, songs = Array.Empty<object>() },
                    },
                },
            };
            input = await Capture(
                new("phantasytour.com", "show", showId.ToString()),
                "phantasy-tour-http-v1",
                "application/json",
                JsonSerializer.Serialize(detail)
            );
        }
        else if (representation == "unknown_set")
        {
            input = await Capture(
                new("phish.net", "ratings", accountKey),
                "phish-net-http-v1",
                "text/html",
                $$"""
                <div class="setlist-container"><div class="setlist-date"><a href="/setlists/{{accountKey}}.html">Phish</a></div>
                <div class="setlist-body"><a class="setlist-song" href="/song/song">Song</a></div></div>
                """
            );
        }
        else
        {
            input = await Capture(
                new("phish.net", "shows", accountKey),
                "phish-net-http-v1",
                "application/json",
                JsonSerializer.Serialize(
                    new
                    {
                        data = new[]
                        {
                            new
                            {
                                showid = Random.Shared.Next(1, int.MaxValue),
                                artistid = 1,
                                artist_name = "Phish",
                                showdate = "2000-01-01",
                            },
                        },
                    }
                )
            );
        }
        var account = new NormalizationProviderDecoder().Decode(input).Setlists.Single();
        var attempt = await processor.ProcessAsync(
            Guid.NewGuid(),
            new(input.Uuid, input.Resource),
            account.Origin.Item,
            representation is "setlist_fm" or "phantasy" ? null : artistUuid
        );
        attempt
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Published);
        var batch = (await processor.GetAsync(attempt.Registration.RegistrationUuid))!;
        return new(source, artistUuid, artistKey, account.Origin.Item, input, batch);
    }

    private async Task<ContextState> Revise(
        ContextState previous,
        bool reverseFirstPair = false,
        bool removeLastSet = false
    )
    {
        var input = await Page(
            previous.ArtistKey,
            previous.AccountIdentity.ProviderKey,
            reverseFirstPair,
            removeLastSet
        );
        var attempt = await processor.ProcessAsync(
            Guid.NewGuid(),
            new(input.Uuid, input.Resource),
            previous.AccountIdentity
        );
        attempt
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Published);
        return previous with
        {
            Input = input,
            Batch = (await processor.GetAsync(attempt.Registration.RegistrationUuid))!,
        };
    }

    private static NormalizationSetlistAccountProposal Proposal(ContextState context) =>
        context.Batch.Extractions.Single().Accounts.Single();

    private static SetlistStructureChange Structure(
        ContextState context,
        SetlistStructureChange? previous = null,
        bool reverseFirstPair = false
    )
    {
        var proposal = Proposal(context);
        var account = proposal.Account;
        var editionUuid = previous?.Edition.Uuid ?? Guid.NewGuid();
        var sets = account
            .Sets.Select(
                (set, index) =>
                    new SetlistStructureSet
                    {
                        Uuid = previous?.Sets[index].Uuid ?? Guid.NewGuid(),
                        Position = account.Ordered ? set.Ordinal : null,
                        Label = set.Label?.Value,
                        EncoreNumber = set.EncoreNumber?.Value,
                        ProviderIdentity = set.Identity?.Identity,
                        Evidence = [set.Evidence],
                    }
            )
            .ToArray();
        var occurrences = proposal
            .Passages.Select(
                (passage, index) =>
                {
                    var originalIndex = reverseFirstPair && index < 2 ? 1 - index : index;
                    var previousOccurrence = previous?.Occurrences.ElementAtOrDefault(
                        originalIndex
                    );
                    return new SongOccurrenceChange
                    {
                        Uuid = previousOccurrence?.Uuid ?? Guid.NewGuid(),
                        ExpectedRevision = previousOccurrence == null ? 0 : 1,
                        EditionUuid = editionUuid,
                        Position = account.Ordered ? passage.Passage.Ordinal : null,
                        SetUuid = passage.Passage.SetOrdinal is { } ordinal
                            ? sets.Single(set => set.Position == ordinal).Uuid
                            : null,
                        SetLabel = passage.Passage.SetLabel?.Value,
                        Kind = "unknown",
                        OriginalText = passage.Passage.Title.Value,
                        TransitionAfter =
                            passage.Passage.Segue?.Value == true
                            || passage.Passage.TransitionMarker != null
                                ? "segue"
                                : "unknown",
                        Evidence = [passage.Passage.Title.Evidence],
                    };
                }
            )
            .ToArray();
        var origin = account.Origin;
        return new()
        {
            Uuid = previous?.Uuid ?? Guid.NewGuid(),
            ExpectedRevision = previous == null ? 0 : 1,
            Edition = new()
            {
                Uuid = editionUuid,
                ExpectedRevision = previous == null ? 0 : 1,
                ArtistUuid = context.ArtistUuid,
                LiteralDate = account.LiteralDate?.Value ?? "",
                Ordered = account.Ordered,
                Provider = origin.Item.Provider,
                ResourceType = origin.Item.ResourceType,
                ProviderKey = origin.Item.ProviderKey,
                Evidence = [origin.Evidence],
            },
            Sets = sets,
            Occurrences = occurrences,
            Sources = [new(context.Batch.AttemptUuid, context.Input.Uuid, 0, proposal)],
            SetCorrespondence = sets.Select(
                    (set, index) => new SetlistSetCorrespondence(0, index, set.Uuid)
                )
                .ToArray(),
            PassageCorrespondence = proposal
                .Passages.Select(
                    (passage, index) =>
                        new SetlistPassageCorrespondence(
                            0,
                            passage.ProposalUuid,
                            [occurrences[index].Uuid]
                        )
                )
                .ToArray(),
            Evidence = [origin.Evidence],
        };
    }

    private async Task<NormalizationWorkTicket> Admit(
        ContextState context,
        SetlistStructureChange structure,
        NormalizationSubjectKey? owner = null,
        bool includeProposalOwner = true,
        IReadOnlyList<Guid>? additionalDependencies = null
    )
    {
        owner ??= new("songs", "setlist", structure.Edition.Uuid);
        var state = await fixture.Work.GetSubjectAsync(owner);
        var dependencies = new List<Guid>(additionalDependencies ?? []);
        if (includeProposalOwner)
        {
            dependencies.Add(context.Batch.Ticket.SubjectUuid);
        }
        return await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            owner,
            "setlist-structure-test-v1",
            context.Batch.Preparation.Inputs.Select(input => input.Input.Resource).ToArray(),
            dependencies,
            state?.DesiredGeneration,
            catalogInputs: context.Batch.Ticket.CatalogInputs,
            mappingRegistry: context.Batch.Ticket.MappingRegistry,
            expectedInputRevisions: context
                .Batch.Preparation.Inputs.Select(input => input.Input)
                .ToArray()
        );
    }

    private static SetlistStructureChange Corrupt(SetlistStructureChange structure, string fault)
    {
        var source = structure.Sources.Single();
        return fault switch
        {
            "account" => structure with
            {
                Sources =
                [
                    source with
                    {
                        Account = source.Account with
                        {
                            Account = source.Account.Account with
                            {
                                LiteralDate = source.Account.Account.LiteralDate! with
                                {
                                    Value = "Invented date",
                                },
                            },
                        },
                    },
                ],
            },
            "input" => structure with
            {
                Sources = [source with { InputRevisionUuid = Guid.NewGuid() }],
            },
            "unpublished_attempt" => structure with
            {
                Sources = [source with { ProposalAttemptUuid = Guid.NewGuid() }],
            },
            "account_index" => structure with { Sources = [source with { AccountIndex = 99 }] },
            "forged_proposal" => structure with
            {
                Sources =
                [
                    source with
                    {
                        Account = source.Account with
                        {
                            Passages =
                            [
                                source.Account.Passages[0] with
                                {
                                    ProposalUuid = Guid.NewGuid(),
                                },
                                .. source.Account.Passages.Skip(1),
                            ],
                        },
                    },
                ],
            },
            "missing_set" => structure with
            {
                SetCorrespondence = structure.SetCorrespondence.Skip(1).ToArray(),
            },
            "missing_passage" => structure with
            {
                PassageCorrespondence = structure.PassageCorrespondence.Skip(1).ToArray(),
            },
            "invented_target" => structure with
            {
                PassageCorrespondence =
                [
                    structure.PassageCorrespondence[0] with
                    {
                        OccurrenceUuids = [Guid.NewGuid()],
                    },
                    .. structure.PassageCorrespondence.Skip(1),
                ],
            },
            "duplicate_passage" => structure with
            {
                PassageCorrespondence =
                [
                    .. structure.PassageCorrespondence,
                    structure.PassageCorrespondence[0],
                ],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };
    }

    private Task Map(CatalogSource source, string providerName, string artistKey) =>
        fixture.Db.WithWriteConnection(async c =>
        {
            var provider =
                await c.QuerySingleOrDefaultAsync<int?>(
                    "SELECT id FROM upstream_sources WHERE name=@providerName ORDER BY id LIMIT 1",
                    new { providerName }
                )
                ?? await c.QuerySingleAsync<int>(
                    "INSERT INTO upstream_sources(id,name,url) VALUES((SELECT GREATEST(1000,COALESCE(max(id),0)+1) FROM upstream_sources),@providerName,'https://provider.invalid') RETURNING id",
                    new { providerName }
                );
            if (providerName == "setlist.fm")
            {
                await c.ExecuteAsync(
                    "UPDATE artists SET musicbrainz_id=@artistKey WHERE id=@ArtistId",
                    new { source.ArtistId, artistKey }
                );
            }
            await c.ExecuteAsync(
                "INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier) VALUES(@ArtistId,@provider,@artistKey)",
                new
                {
                    source.ArtistId,
                    provider,
                    artistKey,
                }
            );
        });

    private Task<NormalizationInputRevision> Page(
        string artistKey,
        string accountKey,
        bool reverseFirstPair = false,
        bool removeLastSet = false
    )
    {
        object[] songs =
        [
            new
            {
                name = "Song",
                tape = true,
                info = "Original tease wording",
                cover = new { mbid = "cover-id", name = "Cover performer" },
                with = new { mbid = "guest-id", name = "Guest performer" },
            },
            new { name = "Song" },
        ];
        if (reverseFirstPair)
        {
            Array.Reverse(songs);
        }
        var sets = new List<object>
        {
            new { name = "First", song = songs },
            new { encore = 1, song = Array.Empty<object>() },
        };
        if (!removeLastSet)
        {
            sets.Add(new { name = "First", song = new[] { new { name = "Reprise" } } });
        }
        var payload = JsonSerializer.Serialize(
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
                        eventDate = "01-01-2000",
                        artist = new { mbid = artistKey, name = "Fixture artist" },
                        sets = new { set = sets },
                        info = "Original account notes",
                    },
                },
            }
        );
        return Capture(
            new("setlist.fm", "setlist-page", artistKey + ":page=1"),
            "setlist-fm-http-v1",
            "application/json",
            payload
        );
    }

    private async Task<NormalizationInputRevision> Capture(
        NormalizationResourceKey resource,
        string version,
        string mediaType,
        string payload
    )
    {
        var current = await evidence.GetCurrentInputAsync(resource);
        return (
            await evidence.CaptureInputAsync(
                new(
                    Guid.NewGuid(),
                    resource,
                    Encoding.UTF8.GetBytes(payload),
                    mediaType,
                    version,
                    current?.Generation ?? 0,
                    DateTimeOffset.UtcNow
                )
            )
        ).Revision;
    }

    private Task<string?> Playback(ContextState context) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<string>(
                "SELECT mp3_url FROM source_tracks WHERE uuid=@TrackUuid",
                context.Source
            )
        );

    private sealed record ContextState(
        CatalogSource Source,
        Guid ArtistUuid,
        string ArtistKey,
        NormalizationResourceKey AccountIdentity,
        NormalizationInputRevision Input,
        NormalizationSetlistProposalBatch Batch
    );

    private sealed record OccurrenceHistory(bool Retracted, long Revision, long History);

    private sealed record WithdrawalHistory(
        long Revision,
        bool Retracted,
        long History,
        long ActiveSets,
        long ActiveOccurrences
    );
}
