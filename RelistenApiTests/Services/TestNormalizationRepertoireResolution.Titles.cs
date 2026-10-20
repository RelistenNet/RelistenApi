using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationRepertoireResolution
{
    [Test]
    public async Task EvaluatedTitleRulePublishesOneSongForSamePageImportedMembersAndReplays()
    {
        var artist = await Artist();
        var title = UniqueTitle();
        var first = await ImportedSong(artist, title);
        var second = await ImportedSong(artist, title.ToUpperInvariant().Replace(" ", "--"));
        var request = await Dictionary(artist);
        var original = await LegacySongs(artist);
        await ActivatePolicy(titleReuse: true);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Drain(() => processor.ResolveAsync(attemptUuid, request));

        resolved
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(resolved));
        var decisions = await Decisions(resolved);
        decisions.Should().HaveCount(2);
        var target = decisions
            .Select(value => value.RepertoireSongUuid)
            .Distinct()
            .Should()
            .ContainSingle()
            .Subject!.Value;
        (await reads.ListRepertoireAsync(artist.Uuid.ToString())).items.Should().ContainSingle();
        (await reads.GetSongAsync(target))!
            .imported_song_uuids.Should()
            .BeEquivalentTo([first, second]);
        (await LegacySongs(artist)).Should().Be(original);

        var replay = await processor.ResolveAsync(attemptUuid, request);
        replay.Result!.Replayed.Should().BeTrue();
        (await Decisions(replay)).Should().BeEquivalentTo(decisions);
        var nextUuid = Guid.NewGuid();
        var next = await Drain(() => processor.ResolveAsync(nextUuid, request));
        next.Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(next));
        (await Decisions(next)).Should().OnlyContain(value => value.RepertoireSongUuid == target);
        (await reads.ListRepertoireAsync(artist.Uuid.ToString())).items.Should().ContainSingle();
    }

    [Test]
    public async Task LaterImportedTitleReusesAcceptedSongAndKeepsReviewedMetadata()
    {
        var artist = await Artist();
        var title = UniqueTitle();
        var target = await AcceptedSong(artist, title);
        var accepted = await music.GetRepertoireAsync(target);
        var imported = await ImportedSong(artist, title.ToUpperInvariant().Replace(" ", "--"));
        var request = await Dictionary(artist);
        await ActivatePolicy(titleReuse: true);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Drain(() => processor.ResolveAsync(attemptUuid, request));

        resolved
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(resolved));
        var decision = (await Decisions(resolved)).Should().ContainSingle().Subject;
        decision.RepertoireSongUuid.Should().Be(target);
        decision.Reason.Should().Be("same_artist_normalized_title");
        (await music.GetRepertoireAsync(target)).Should().BeEquivalentTo(accepted);
        (await reads.GetSongAsync(target))!
            .imported_song_uuids.Should()
            .ContainSingle()
            .Which.Should()
            .Be(imported);

        var laterUuid = Guid.NewGuid();
        var later = await Drain(() => processor.ResolveAsync(laterUuid, request));
        later
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(later));
        (await Decisions(later))
            .Should()
            .ContainSingle()
            .Which.RepertoireSongUuid.Should()
            .Be(target);
        (await music.GetRepertoireAsync(target)).Should().BeEquivalentTo(accepted);
    }

    [Test]
    public async Task ArtistTitleReuseRequiresTheEvaluatedRuleRatherThanAnyActivePolicy()
    {
        var artist = await Artist();
        var title = UniqueTitle();
        var existing = await AcceptedSong(artist, title);
        await ImportedSong(artist, title);
        var request = await Dictionary(artist);
        await ActivatePolicy();
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Drain(() => processor.ResolveAsync(attemptUuid, request));

        resolved
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(resolved));
        (await Decisions(resolved))
            .Should()
            .ContainSingle()
            .Which.RepertoireSongUuid.Should()
            .NotBe(existing);
        (await reads.ListRepertoireAsync(artist.Uuid.ToString())).items.Should().HaveCount(2);
    }

    [Test]
    public async Task WholeArtistAliasComponentPreventsAnUnseenRivalFromBeingIgnored()
    {
        var artist = await Artist();
        var foo = UniqueTitle();
        var bar = UniqueTitle();
        await AcceptedSong(artist, foo, [bar]);
        await AcceptedSong(artist, bar);
        await ImportedSong(artist, foo);
        var request = await Dictionary(artist);
        await ActivatePolicy(titleReuse: true);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Drain(() => processor.ResolveAsync(attemptUuid, request));

        resolved
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(resolved));
        var decision = (await Decisions(resolved)).Should().ContainSingle().Subject;
        decision.Disposition.Should().Be(RepertoireResolutionClaimDisposition.Review);
        decision.Reason.Should().Be("existing_same_title_identities_require_merge");
        decision.RepertoireSongUuid.Should().BeNull();
        (await reads.ListRepertoireAsync(artist.Uuid.ToString())).items.Should().HaveCount(2);
    }

    [Test]
    public async Task DifferentKnownWorksPreventSameTitleReuse()
    {
        var artist = await Artist();
        var title = UniqueTitle();
        var first = await AcceptedSong(artist, title);
        var second = await AcceptedSong(artist, title);
        await AddReviewedWork(artist.Uuid, first, title);
        await AddReviewedWork(artist.Uuid, second, title);
        await ImportedSong(artist, title);
        var request = await Dictionary(artist);
        await ActivatePolicy(titleReuse: true);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Drain(() => processor.ResolveAsync(attemptUuid, request));

        resolved
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(resolved));
        var decision = (await Decisions(resolved)).Should().ContainSingle().Subject;
        decision.Disposition.Should().Be(RepertoireResolutionClaimDisposition.Review);
        decision.Reason.Should().Be("qualified_work_conflict");
        (await reads.ListRepertoireAsync(artist.Uuid.ToString())).items.Should().HaveCount(2);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RecordingAndSetlistTitleClaimsReuseTheSongWithoutChangingPlayback(
        bool setlist
    )
    {
        var title = UniqueTitle();
        var source = await PublishedTitle(title, setlist);
        var target = await AcceptedSong(new(source.Catalog.ArtistId, source.ArtistUuid), title);
        var playback = await Playback(source.Catalog);
        await ActivatePolicy(titleReuse: true);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Drain(() => processor.ResolveAsync(attemptUuid, source.Request));

        resolved
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(resolved));
        var decision = (await Decisions(resolved)).Should().ContainSingle().Subject;
        decision.RepertoireSongUuid.Should().Be(target);
        decision.Reason.Should().Be("same_artist_normalized_title");
        (await reads.ListRepertoireAsync(source.ArtistUuid.ToString()))
            .items.Should()
            .ContainSingle();
        (await Playback(source.Catalog)).Should().Be(playback);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SharedSongRetainsEverySupportedAliasForAnIndependentLaterImport(
        bool existingUnreviewed
    )
    {
        var artist = await Artist();
        var title = UniqueTitle();
        var bar = UniqueTitle();
        var baz = UniqueTitle();
        Guid? existingUuid = null;
        if (existingUnreviewed)
        {
            var input = await fixture.Capture(title, "songs");
            var initialTicket = await fixture.Work.BeginWorkAsync(
                Guid.NewGuid(),
                ArtistOwner(artist.Uuid),
                "title-alias-fixture-v1",
                [input.Resource],
                []
            );
            existingUuid = Guid.NewGuid();
            await music.PublishAsync(
                initialTicket.WorkUuid,
                new()
                {
                    RepertoireSongs =
                    [
                        new()
                        {
                            Uuid = existingUuid.Value,
                            ArtistUuid = artist.Uuid,
                            DisplayTitle = title,
                            Basis = "title_derived",
                            Evidence = [input.Reference],
                        },
                    ],
                }
            );
        }
        await ImportedSong(artist, title);
        await ImportedSong(artist, title);
        var request = await Dictionary(artist);
        await ActivatePolicy(titleReuse: true);
        var processor = Processor();
        var prepared = await UntilStage(
            processor,
            Guid.NewGuid(),
            request,
            RepertoireResolutionStageKind.DecisionPage,
            admitted: false
        );
        var page = prepared.Stages.Single(stage =>
            stage.Kind == RepertoireResolutionStageKind.DecisionPage
        );
        var barInput = await fixture.Capture(bar, "songs");
        var bazInput = await fixture.Capture(baz, "songs");
        var aliases = new[]
        {
            new ObservedValue<string>(bar, barInput.Reference),
            new ObservedValue<string>(baz, bazInput.Reference),
        };
        // The current adapters have no direct alias-array source. Exercise the typed page
        // contract with real retained alias evidence, then publish its actual assembled rows.
        var context = page.Context with
        {
            Claims = page
                .Context.Claims.Select(
                    (claim, index) =>
                        claim with
                        {
                            Claim = claim.Claim with { Aliases = [aliases[index]] },
                        }
                )
                .ToArray(),
        };
        var decisions = new RepertoireResolutionPolicyEvaluator().DecidePage(
            context.Claims,
            context.Policy.Active
        );
        var publication = NormalizationRepertoireResolutionProcessor.BuildPublication(
            context,
            decisions
        );
        var evidence = new NormalizationEvidenceService(fixture.Db);
        // Generic publication consumes the retained evidence inherited from an accepted song.
        // The processor's specialized publisher authenticates it through saved candidate facts.
        var inputRevisions = context
            .ReadSet.Inputs.Select(input => input.RevisionUuid)
            .Concat(
                publication
                    .RepertoireSongs.SelectMany(song => song.Evidence)
                    .Select(citation => citation.InputRevisionUuid)
            )
            .Distinct();
        var inputs = await Task.WhenAll(
            inputRevisions.Select(revision => evidence.GetInputAsync(revision))
        );
        var owner = await fixture.Work.GetSubjectAsync(ArtistOwner(artist.Uuid));
        var ticket = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            ArtistOwner(artist.Uuid),
            "title-alias-publication-fixture-v1",
            inputs
                .Select(input => input!.Resource)
                .Concat([barInput.Resource, bazInput.Resource])
                .Distinct()
                .ToArray(),
            [],
            expectedConfigurationGeneration: owner?.DesiredGeneration,
            catalogInputs: context.ReadSet.CatalogInputs
        );

        await music.PublishAsync(ticket.WorkUuid, publication);

        var target = publication.RepertoireSongs.Should().ContainSingle().Subject.Uuid;
        var accepted = (await music.GetRepertoireAsync(target))!;
        accepted.Value.DisplayTitle.Should().Be(title);
        if (existingUuid.HasValue)
        {
            target.Should().Be(existingUuid.Value);
            accepted.Value.Basis.Should().Be("title_derived");
        }
        accepted.Value.Aliases.Should().Contain([bar, baz]);
        accepted.Value.Evidence.Should().Contain([barInput.Reference, bazInput.Reference]);
        await ImportedSong(artist, baz);
        var laterRequest = await Dictionary(artist);
        var laterUuid = Guid.NewGuid();
        var later = await Drain(() => processor.ResolveAsync(laterUuid, laterRequest));
        later
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(later));
        (await Decisions(later))
            .Should()
            .OnlyContain(decision => decision.RepertoireSongUuid == target);
        (await reads.ListRepertoireAsync(artist.Uuid.ToString())).items.Should().ContainSingle();
    }

    [Test]
    public async Task SamePageAliasBridgeRetainsReviewThroughCompletionAndReplay()
    {
        var artist = await Artist();
        var foo = UniqueTitle();
        var baz = UniqueTitle();
        await AcceptedSong(artist, foo);
        await AcceptedSong(artist, baz);
        await ImportedSong(artist, foo);
        await ImportedSong(artist, baz);
        var request = await Dictionary(artist);
        await ActivatePolicy(titleReuse: true);
        var processor = Processor();
        var aliasTitle = UniqueTitle();
        var alias = await fixture.Capture(aliasTitle, "songs");
        var original = new RepertoireResolutionPreparation(
            await Sources().ReadAsync(request),
            await policies.ReadStateAsync()
        );
        // A future adapter can emit supported aliases through this existing typed contract.
        // Freeze that fixture once before the processor starts its normal durable page phases.
        var source = original.Source with
        {
            Claims = original
                .Source.Claims.Select(claim =>
                    claim with
                    {
                        Aliases = [new(aliasTitle, alias.Reference)],
                    }
                )
                .ToArray(),
            ReadSet = original.Source.ReadSet with
            {
                Inputs = original
                    .Source.ReadSet.Inputs.Concat(alias.Ticket.Inputs)
                    .Distinct()
                    .ToArray(),
            },
        };
        var attemptUuid = Guid.NewGuid();
        var store = new NormalizationRepertoireResolutionStore(fixture.Db);
        var created = await store.GetOrCreateAsync(
            attemptUuid,
            request,
            NormalizationRepertoireResolutionProcessor.Version,
            source.Lineage
        );
        await store.SavePreparationAsync(created, original with { Source = source });
        var decided = await UntilStage(
            processor,
            attemptUuid,
            request,
            RepertoireResolutionStageKind.DecisionPage,
            admitted: false
        );
        decided
            .Stages.Single(stage => stage.Kind == RepertoireResolutionStageKind.DecisionPage)
            .Decisions.Should()
            .OnlyContain(decision =>
                decision.Disposition == RepertoireResolutionClaimDisposition.Review
                && decision.Reason == "existing_same_title_identities_require_merge"
            );

        var completed = await Drain(() => processor.ResolveAsync(attemptUuid, request));

        completed
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(completed));
        (await Decisions(completed))
            .Should()
            .HaveCount(2)
            .And.OnlyContain(decision =>
                decision.Disposition == RepertoireResolutionClaimDisposition.Review
                && decision.Reason == "existing_same_title_identities_require_merge"
            );
        await AssertSourceSettled(completed);
        var replay = await processor.ResolveAsync(attemptUuid, request);
        replay.Result!.Replayed.Should().BeTrue();
        (await Decisions(replay)).Should().BeEquivalentTo(await Decisions(completed));
        (await reads.ListRepertoireAsync(artist.Uuid.ToString())).items.Should().HaveCount(2);
    }

    private async Task AddReviewedWork(Guid artistUuid, Guid songUuid, string title)
    {
        var input = await fixture.Capture(title, "compositions");
        var compositionUuid = Guid.NewGuid();
        var ticket = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("compositions", "composition", compositionUuid),
            "fixture-v1",
            [input.Resource],
            []
        );
        await music.PublishAsync(
            ticket.WorkUuid,
            new()
            {
                Compositions =
                [
                    new()
                    {
                        Uuid = compositionUuid,
                        DisplayTitle = title,
                        Evidence = [input.Reference],
                    },
                ],
            }
        );
        var owner = (await fixture.Work.GetSubjectAsync(ArtistOwner(artistUuid)))!;
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            owner.SubjectUuid,
            owner.EditorialRevision,
            "accept",
            new()
            {
                CompositionLinks =
                [
                    new()
                    {
                        Uuid = Guid.NewGuid(),
                        RepertoireSongUuid = songUuid,
                        CompositionUuid = compositionUuid,
                        MatchReason = "reviewed",
                        Evidence = [input.Reference],
                    },
                ],
            },
            "Reviewed distinct compositions for the fixture's same-title songs",
            Operator
        );
    }
}
