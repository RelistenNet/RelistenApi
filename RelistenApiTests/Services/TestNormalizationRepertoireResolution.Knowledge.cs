using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationRepertoireResolution
{
    [Test]
    public async Task SavedExpertAliasReusesReviewedIdentityForLaterIndependentImportedMembers()
    {
        using var knowledge = new SongKnowledgeFixture(fixture);
        var artist = await Artist();
        var target = await AcceptedSong(artist, UniqueTitle());
        var reviewed = await music.GetRepertoireAsync(target);
        var alias = UniqueTitle();
        var command = await knowledge.Save(
            artist.Uuid,
            alias,
            new("songs", "canonical", "reviewed-song", target)
        );
        var firstImported = await ImportedSong(artist, alias);
        var firstRequest = await Dictionary(artist);
        var legacy = await LegacySongs(artist);
        var processor = Processor();
        var firstUuid = Guid.NewGuid();

        var first = await Drain(() => processor.ResolveAsync(firstUuid, firstRequest));

        first
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(first));
        (await Decisions(first))
            .Should()
            .ContainSingle()
            .Which.RepertoireSongUuid.Should()
            .Be(target);
        (await LegacySongs(artist)).Should().Be(legacy);
        var page = await FullStage(first, RepertoireResolutionStageKind.DecisionPage);
        page.Decisions.Single()
            .ExpertKnowledge!.AppliedRules.Should()
            .ContainSingle(rule => rule.RuleUuid == command.RuleUuid && rule.Revision == 1);
        page.Context.ExpertKnowledge!.Rules.Should()
            .ContainSingle(rule => rule.RuleUuid == command.RuleUuid);

        var secondImported = await ImportedSong(artist, alias);
        var laterRequest = await Dictionary(artist);
        var laterUuid = Guid.NewGuid();
        var later = await Drain(() => processor.ResolveAsync(laterUuid, laterRequest));

        later
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(later));
        (await Decisions(later))
            .Should()
            .OnlyContain(decision => decision.RepertoireSongUuid == target);
        (await reads.GetSongAsync(target))!
            .imported_song_uuids.Should()
            .BeEquivalentTo([firstImported, secondImported]);
        (await reads.ListRepertoireAsync(artist.Uuid.ToString())).items.Should().ContainSingle();
        (await music.GetRepertoireAsync(target)).Should().BeEquivalentTo(reviewed);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RevisedOrWithdrawnExpertRuleCannotPublishItsSavedPageAndRetryUsesCurrentKnowledge(
        bool withdraw
    )
    {
        using var knowledge = new SongKnowledgeFixture(fixture);
        var artist = await Artist();
        var original = await AcceptedSong(artist, UniqueTitle());
        var replacement = await AcceptedSong(artist, UniqueTitle());
        var alias = UniqueTitle();
        var command = await knowledge.Save(
            artist.Uuid,
            alias,
            new("songs", "canonical", "reviewed-song", original)
        );
        await ImportedSong(artist, alias);
        var request = await Dictionary(artist);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        await UntilStage(
            processor,
            attemptUuid,
            request,
            RepertoireResolutionStageKind.AcceptedWrite,
            admitted: false
        );

        await knowledge.Service.SaveAsync(
            command with
            {
                OperationUuid = Guid.NewGuid(),
                ExpectedRevision = 1,
                Action = withdraw ? "retract" : "accept",
                Definition = withdraw
                    ? null
                    : command.Definition! with
                    {
                        Target = command.Definition.Target with { CanonicalUuid = replacement },
                    },
                Reason = withdraw
                    ? "Withdraw this fixture alias"
                    : "Correct this fixture's song target",
            },
            Operator
        );
        var stale = await Drain(() => processor.ResolveAsync(attemptUuid, request));

        stale.Result!.Disposition.Should().Be(RepertoireResolutionDisposition.Stale, Audit(stale));
        (await music.GetImportedSongsAsync(original)).Should().BeEmpty();
        var retryUuid = Guid.NewGuid();
        var retry = await Drain(() =>
            processor.RetryAsync(retryUuid, stale.Registration.RegistrationUuid)
        );
        retry
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(retry));
        var selected = (await Decisions(retry)).Should().ContainSingle().Subject.RepertoireSongUuid;
        selected.Should().NotBe(original);
        if (withdraw)
        {
            selected.Should().NotBe(replacement);
        }
        else
        {
            selected.Should().Be(replacement);
        }
        (await knowledge.Service.GetHistoryAsync(command.RuleUuid))
            .Select(entry => entry.Revision)
            .Should()
            .Equal(1, 2);
    }

    [Test]
    public async Task ExpertCorrectionMovesAutomaticCorrespondencesWithoutChangingImportedOrCanonicalIds()
    {
        using var knowledge = new SongKnowledgeFixture(fixture);
        var artist = await Artist();
        var original = await AcceptedSong(artist, UniqueTitle());
        var replacement = await AcceptedSong(artist, UniqueTitle());
        var alias = UniqueTitle();
        var command = await knowledge.Save(
            artist.Uuid,
            alias,
            new("songs", "canonical", "reviewed-song", original)
        );
        var imported = await ImportedSong(artist, alias);
        var request = await Dictionary(artist);
        var processor = Processor();
        var firstUuid = Guid.NewGuid();
        var first = await Drain(() => processor.ResolveAsync(firstUuid, request));
        first
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(first));
        var legacy = await LegacySongs(artist);
        await knowledge.Service.SaveAsync(
            command with
            {
                OperationUuid = Guid.NewGuid(),
                ExpectedRevision = 1,
                Definition = command.Definition! with
                {
                    Target = command.Definition.Target with { CanonicalUuid = replacement },
                },
                Reason = "Correct an automatic fixture correspondence",
            },
            Operator
        );
        var retryUuid = Guid.NewGuid();

        var corrected = await Drain(() =>
            processor.RetryAsync(retryUuid, first.Registration.RegistrationUuid)
        );

        corrected
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(corrected));
        (await Decisions(corrected))
            .Should()
            .ContainSingle()
            .Which.RepertoireSongUuid.Should()
            .Be(replacement);
        (await reads.GetSongAsync(replacement))!.imported_song_uuids.Should().Contain(imported);
        (await music.GetImportedSongsAsync(original)).Should().BeEmpty();
        (await LegacySongs(artist)).Should().Be(legacy);
        (await reads.ListRepertoireAsync(artist.Uuid.ToString())).items.Should().HaveCount(2);
    }

    [Test]
    public async Task ConflictingExpertRulesRemainReviewableThroughSourceAcknowledgment()
    {
        using var knowledge = new SongKnowledgeFixture(fixture);
        var artist = await Artist();
        var first = await AcceptedSong(artist, UniqueTitle());
        var second = await AcceptedSong(artist, UniqueTitle());
        var alias = UniqueTitle();
        await knowledge.Save(artist.Uuid, alias, new("songs", "canonical", "first", first));
        await knowledge.Save(artist.Uuid, alias, new("songs", "canonical", "second", second));
        await ImportedSong(artist, alias);
        var request = await Dictionary(artist);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Drain(() => processor.ResolveAsync(attemptUuid, request));

        resolved
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(resolved));
        (await Decisions(resolved))
            .Should()
            .ContainSingle()
            .Which.Reason.Should()
            .Be("expert_knowledge_conflict");
        await AssertSourceSettled(resolved);
        (await reads.ListRepertoireAsync(artist.Uuid.ToString())).items.Should().HaveCount(2);
    }

    [Test]
    public async Task ExpertRejectionExcludesItsNamedCandidateWhileOtherEvaluatedMatchesRemainUsable()
    {
        using var knowledge = new SongKnowledgeFixture(fixture);
        var artist = await Artist();
        var title = UniqueTitle();
        var rejected = await AcceptedSong(artist, title);
        var retained = await AcceptedSong(artist, title);
        await knowledge.Save(
            artist.Uuid,
            title,
            new("songs", "canonical", "rejected", rejected),
            action: "reject"
        );
        await ImportedSong(artist, title);
        var request = await Dictionary(artist);
        await ActivatePolicy(titleReuse: true);
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
            .Be(retained);
        (await music.GetImportedSongsAsync(rejected)).Should().BeEmpty();
        (await reads.ListRepertoireAsync(artist.Uuid.ToString())).items.Should().HaveCount(2);
    }

    [Test]
    public async Task FrozenTargetKeyDoesNotAcquireCanonicalMeaningFromItsUuidSpelling()
    {
        using var knowledge = new SongKnowledgeFixture(fixture);
        var artist = await Artist();
        var existing = await AcceptedSong(artist, UniqueTitle());
        var alias = UniqueTitle();
        await knowledge.Save(
            artist.Uuid,
            alias,
            new("songs", "repertoire", existing.ToString("D"))
        );
        await ImportedSong(artist, alias);
        var request = await Dictionary(artist);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Drain(() => processor.ResolveAsync(attemptUuid, request));

        resolved
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(resolved));
        (await Decisions(resolved))
            .Should()
            .ContainSingle()
            .Which.Reason.Should()
            .Be("expert_target_unbound_or_ambiguous");
        (await music.GetImportedSongsAsync(existing)).Should().BeEmpty();
        (await reads.ListRepertoireAsync(artist.Uuid.ToString())).items.Should().ContainSingle();
    }

    [TestCase("imported_song")]
    [TestCase("provider_song")]
    [TestCase("frozen_reader")]
    public async Task ExpertTargetCanUseAuthenticatedImportedOrProviderCorrespondence(
        string targetKind
    )
    {
        using var knowledge = new SongKnowledgeFixture(fixture);
        var artist = await Artist();
        var imported = await ImportedSong(artist, UniqueTitle());
        var originalRequest = await Dictionary(artist);
        var processor = Processor();
        var firstUuid = Guid.NewGuid();
        var first = await Drain(() => processor.ResolveAsync(firstUuid, originalRequest));
        var target = (await Decisions(first)).Single().RepertoireSongUuid!.Value;
        var identity = first.Preparation!.Source.Claims.Single().Identities.Single().Identity;
        var reference = targetKind switch
        {
            "provider_song" => new ExpertKnowledgeTarget(
                "songs",
                "provider_song",
                JsonSerializer.Serialize(
                    new[] { identity.Provider, identity.ResourceType, identity.ProviderKey }
                )
            ),
            "frozen_reader" => new ExpertKnowledgeTarget(
                "songs",
                "repertoire",
                JsonSerializer.Serialize(
                    new[] { "retained-capture", "repertoire:original-reader-group" }
                ),
                ImportedSongUuid: imported
            ),
            _ => new ExpertKnowledgeTarget("songs", "imported_song", imported.ToString("D")),
        };
        var alias = UniqueTitle();
        await knowledge.Save(artist.Uuid, alias, reference);
        var laterImported = await ImportedSong(artist, alias);
        var request = await Dictionary(artist);
        var laterUuid = Guid.NewGuid();

        var later = await Drain(() => processor.ResolveAsync(laterUuid, request));

        later
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(later));
        (await Decisions(later))
            .Should()
            .OnlyContain(decision => decision.RepertoireSongUuid == target);
        (await reads.GetSongAsync(target))!
            .imported_song_uuids.Should()
            .BeEquivalentTo([imported, laterImported]);
        (await reads.ListRepertoireAsync(artist.Uuid.ToString())).items.Should().ContainSingle();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RuntimeRefusesImportedAttachmentThatIsMissingOrBelongsToAnotherArtist(
        bool foreignArtist
    )
    {
        using var knowledge = new SongKnowledgeFixture(fixture);
        var artist = await Artist();
        var imported = foreignArtist
            ? await ImportedSong(await Artist(), UniqueTitle())
            : Guid.NewGuid();
        var target = new ExpertKnowledgeTarget(
            "songs",
            "repertoire",
            "[\"retained-capture\",\"repertoire:original-reader-group\"]",
            ImportedSongUuid: imported
        );

        var alias = UniqueTitle();
        // An isolated review ledger can retain a reference without owning catalog rows.
        // The actual catalog must authenticate the reference before accepting correspondence.
        await knowledge.Save(artist.Uuid, alias, target);
        await ImportedSong(artist, alias);
        var request = await Dictionary(artist);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Drain(() => processor.ResolveAsync(attemptUuid, request));

        resolved
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(resolved));
        (await Decisions(resolved))
            .Should()
            .ContainSingle()
            .Which.Reason.Should()
            .Be("expert_target_unbound_or_ambiguous");
        (await reads.ListRepertoireAsync(artist.Uuid.ToString())).items.Should().BeEmpty();
    }

    [Test]
    public async Task OtherArtistsCannotConsumeAnExpertProviderTargetsCandidateBudget()
    {
        using var knowledge = new SongKnowledgeFixture(fixture);
        var input = await fixture.Capture("Provider song IDs are scoped to their artist", "songs");
        var provider = "artist-scoped-" + Guid.NewGuid().ToString("N");
        var targets = new List<(CatalogArtist Artist, Guid Song)>();
        for (var index = 0; index < 102; index++)
        {
            var artist = await Artist();
            var ticket = await fixture.Work.BeginWorkAsync(
                Guid.NewGuid(),
                ArtistOwner(artist.Uuid),
                "provider-scope-fixture-v1",
                [input.Resource],
                []
            );
            var uuid = Guid.NewGuid();
            await music.PublishAsync(
                ticket.WorkUuid,
                new()
                {
                    RepertoireSongs =
                    [
                        new()
                        {
                            Uuid = uuid,
                            ArtistUuid = artist.Uuid,
                            DisplayTitle = UniqueTitle(),
                            Evidence = [input.Reference],
                        },
                    ],
                    ExternalIdentities =
                    [
                        new()
                        {
                            Uuid = Guid.NewGuid(),
                            RepertoireSongUuid = uuid,
                            Provider = provider,
                            ResourceType = "song",
                            ProviderKey = "local-song-id",
                            Evidence = [input.Reference],
                        },
                    ],
                }
            );
            targets.Add((artist, uuid));
        }
        // Choose an admitted target outside an unscoped bounded pool. The fixture therefore
        // does not depend on PostgreSQL's unspecified row order when demonstrating the bug.
        var unscoped = (
            await fixture.Db.WithWriteConnection(c =>
                c.QueryAsync<Guid>(
                    """
                    SELECT DISTINCT repertoire_song_uuid FROM normalization.music_external_identities
                    WHERE provider=@provider AND resource_type='song' AND provider_key='local-song-id'
                        AND repertoire_song_uuid IS NOT NULL AND NOT retracted LIMIT 101
                    """,
                    new { provider }
                )
            )
        ).ToHashSet();
        var selected = targets.First(target => !unscoped.Contains(target.Song));
        var alias = UniqueTitle();
        await knowledge.Save(
            selected.Artist.Uuid,
            alias,
            new(
                "songs",
                "provider_song",
                JsonSerializer.Serialize(new[] { provider, "song", "local-song-id" })
            )
        );
        await ImportedSong(selected.Artist, alias);
        var request = await Dictionary(selected.Artist);
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
            .Be(selected.Song);
    }

    private sealed class SongKnowledgeFixture : IDisposable
    {
        private readonly string directory = Directory
            .CreateTempSubdirectory("repertoire-knowledge-")
            .FullName;
        public NormalizationExpertKnowledgeService Service { get; }

        public SongKnowledgeFixture(NormalizationDomainFixture fixture)
        {
            Service = new(fixture.Db, fixture.Work, directory);
        }

        public async Task<ExpertKnowledgeSaveCommand> Save(
            Guid artist,
            string literal,
            ExpertKnowledgeTarget target,
            string action = "accept"
        )
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    artist,
                    literal,
                    target,
                }
            );
            var path = Path.Combine(directory, Guid.NewGuid() + ".json");
            await File.WriteAllBytesAsync(path, bytes);
            var command = new ExpertKnowledgeSaveCommand(
                Guid.NewGuid(),
                Guid.NewGuid(),
                0,
                action,
                new(
                    "songs",
                    new("repertoire", Literal: literal, ArtistUuid: artist),
                    target,
                    [new(path, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)))],
                    "Synthetic retained source and reviewed target for this behavioral fixture"
                ),
                "Reviewed fixture correspondence"
            );
            await Service.SaveAsync(command, Operator);
            return command;
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
