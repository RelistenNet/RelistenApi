using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

[TestFixture]
public sealed class TestRepertoireConsumerCorrespondenceEvaluator
{
    private const string Processor = "consumer-correspondence-fixture-v1";
    private static readonly Guid Track = Guid.Parse("d6d9683f-6cea-4f61-bbc7-7f0d134ece70");
    private static readonly Guid Song = Guid.Parse("9730953b-9030-4bda-bc06-ea4eb7529570");
    private readonly RepertoireConsumerCorrespondenceEvaluator evaluator = new();

    [Test]
    public void CheckedSingleFileUsesAcceptedSongAndFileScopeWithoutTitleOrOrdinalMatching()
    {
        var context = SingleFile();
        var result = evaluator.Evaluate(context, Policy()).Single();

        result.Disposition.Should().Be(MusicUsageDecisionDisposition.Authorized);
        result.Rule.Should().Be(MusicMaterializationRule.CheckedSingleFileClaimContinuity);
        result
            .Targets.Should()
            .ContainSingle()
            .Which.Uuid.Should()
            .Be(context.AcceptedTargets.Single().Uuid);
        result.Features.Should().Contain(MusicUsageFeature.SameCheckedFile);
    }

    [TestCase("file")]
    [TestCase("track")]
    [TestCase("song")]
    [TestCase("reviewed")]
    [TestCase("rival")]
    [TestCase("block")]
    [TestCase("incomplete")]
    [TestCase("version")]
    public void SingleFileCannotBypassContradictoryFacts(string variant)
    {
        var context = SingleFile();
        context = variant switch
        {
            "file" => context with
            {
                After = context.After with
                {
                    Container = context.After.Container with
                    {
                        FileIdentity = Identity(
                            "file",
                            "another-file",
                            context.After.Claims[0].Claim.InputRevisionUuid
                        ),
                    },
                },
            },
            "track" => context with
            {
                After = context.After with
                {
                    Container = context.After.Container with { TrackUuid = Guid.NewGuid() },
                },
            },
            "song" => context with
            {
                After = context.After with
                {
                    Claims = [context.After.Claims[0] with { RepertoireSongUuid = Guid.NewGuid() }],
                },
            },
            "reviewed" => context with
            {
                AcceptedTargets = [context.AcceptedTargets[0] with { Basis = "reviewed" }],
            },
            "rival" => context with
            {
                AcceptedTargets =
                [
                    context.AcceptedTargets[0],
                    context.AcceptedTargets[0] with
                    {
                        Uuid = Guid.NewGuid(),
                        Claim = null,
                    },
                ],
            },
            "block" => context with
            {
                AuthorizationHeads =
                [
                    new(
                        Guid.NewGuid(),
                        MusicMaterializationOperation.Correspondence,
                        context.After.Claims[0].Claim,
                        context.AcceptedTargets[0].Uuid,
                        SetlistAuthorizationAction.Block
                    ),
                ],
            },
            "incomplete" => context with
            {
                After = context.After with
                {
                    Issues = [new(CandidateFactsIssueKind.CandidateLimit)],
                },
            },
            _ => context with
            {
                After = context.After with { ProcessorVersion = "unsupported-version" },
            },
        };

        evaluator
            .Evaluate(context, Policy())
            .Should()
            .NotContain(value => value.Disposition == MusicUsageDecisionDisposition.Authorized);
    }

    [Test]
    public void InactivePolicyLeavesSupportedSingleFileCorrespondenceUnresolved()
    {
        var decision = evaluator.Evaluate(SingleFile(), null).Single();

        decision.Disposition.Should().Be(MusicUsageDecisionDisposition.Unresolved);
        decision.Reason.Should().Be("inactive_or_inapplicable_music_usage_policy");
    }

    [Test]
    public void NativeSetAnchorsSupportUnkeyedSetReuseAndWithdrawalWithoutInventingPassageIds()
    {
        var context = NativeSetImage();
        var reused = evaluator
            .Evaluate(context, Policy())
            .Single(value => value.CurrentClaim == context.After.Claims[1].Claim);
        reused.Disposition.Should().Be(MusicUsageDecisionDisposition.Authorized);
        reused.Rule.Should().Be(MusicMaterializationRule.QualifiedUsageAnchors);

        context = context with
        {
            After = context.After with
            {
                Claims = [context.After.Claims[0], context.After.Claims[2]],
            },
        };
        var removed = evaluator
            .Evaluate(context, Policy())
            .Single(value => value.Operation == MusicMaterializationOperation.Withdrawal);
        removed.Disposition.Should().Be(MusicUsageDecisionDisposition.Authorized);
        removed.Rule.Should().Be(MusicMaterializationRule.BracketedWithdrawal);
        removed.PreviousClaim.Should().Be(context.Before.Claims[1].Claim);
    }

    [Test]
    public void QualifiedNativeSetAndUniqueAcceptedSongSupportAnUnkeyedPassage()
    {
        var context = QualifiedSetSong();
        var decision = evaluator.Evaluate(context, Policy()).Single();

        decision.Disposition.Should().Be(MusicUsageDecisionDisposition.Authorized);
        decision.Rule.Should().Be(MusicMaterializationRule.UniqueBoundSongWithinQualifiedSet);
        decision.Features.Should().Contain(MusicUsageFeature.SameQualifiedSet);
        context.Before.Claims.Single().UsageIdentity.Should().BeNull();
        context.After.Claims.Single().UsageIdentity.Should().BeNull();
    }

    [TestCase("prior-rival")]
    [TestCase("new-rival")]
    [TestCase("missing-pool")]
    [TestCase("missing-set")]
    [TestCase("moved-set")]
    public void QualifiedSetRefusesIncompleteOrCompetingAccountContexts(string variant)
    {
        var context = QualifiedSetSong();
        MusicUsageClaimFact Rival(MusicUsageSourceImage image) =>
            image.Claims[0] with
            {
                Claim = image.Claims[0].Claim with { SetIndex = 1, ProposalUuid = Guid.NewGuid() },
                Position = 1,
            };
        context = variant switch
        {
            "prior-rival" => context with
            {
                Before = context.Before with { CompetingClaims = [Rival(context.Before)] },
            },
            "new-rival" => context with
            {
                After = context.After with { CompetingClaims = [Rival(context.After)] },
            },
            "missing-pool" => context with
            {
                After = context.After with { CompetingClaims = null },
            },
            "missing-set" => context with
            {
                Before = context.Before with
                {
                    Container = context.Before.Container with { SetIdentity = null },
                },
                After = context.After with
                {
                    Container = context.After.Container with { SetIdentity = null },
                },
            },
            _ => context with
            {
                After = context.After with
                {
                    Container = context.After.Container with
                    {
                        SetIdentity = Identity(
                            "show_set",
                            "other-set",
                            context.After.Claims[0].Claim.InputRevisionUuid
                        ),
                    },
                },
            },
        };

        evaluator
            .Evaluate(context, Policy())
            .Should()
            .NotContain(value => value.Disposition == MusicUsageDecisionDisposition.Authorized);
    }

    [Test]
    public void DuplicateNativeAnchorRefusesReuseAndWithdrawalRegardlessOfEnumerationOrder()
    {
        var context = NativeSetImage();
        var duplicate = context.After.Claims[0] with
        {
            Claim = context.After.Claims[0].Claim with { SetIndex = 4 },
            Position = 4,
        };
        context = context with
        {
            After = context.After with
            {
                Claims = context.After.Claims.Append(duplicate).ToArray(),
            },
        };
        var policy = Policy();
        var forward = evaluator.Evaluate(context, policy);
        var reversed = evaluator.Evaluate(
            context with
            {
                Before = context.Before with { Claims = context.Before.Claims.Reverse().ToArray() },
                After = context.After with { Claims = context.After.Claims.Reverse().ToArray() },
                AcceptedTargets = context.AcceptedTargets.Reverse().ToArray(),
            },
            policy
        );

        forward.Should().BeEquivalentTo(reversed, options => options.WithStrictOrdering());
        forward
            .Should()
            .NotContain(value => value.Disposition == MusicUsageDecisionDisposition.Authorized);
    }

    [Test]
    public void MatchingSetPositionsWithoutQualifiedUsageAnchorsProvideNoAuthority()
    {
        var context = NativeSetImage();
        context = context with
        {
            Before = context.Before with
            {
                Claims = context
                    .Before.Claims.Select(value => value with { UsageIdentity = null })
                    .ToArray(),
            },
            After = context.After with
            {
                Claims = context
                    .After.Claims.Select(value => value with { UsageIdentity = null })
                    .ToArray(),
            },
        };

        evaluator
            .Evaluate(context, Policy())
            .Should()
            .NotContain(value => value.Disposition == MusicUsageDecisionDisposition.Authorized);
    }

    [Test]
    public void SongIdentifiersCannotActAsNativeSetAnchors()
    {
        var context = NativeSetImage();
        MusicUsageSourceImage Replace(MusicUsageSourceImage image) =>
            image with
            {
                Claims = image
                    .Claims.Select(value =>
                        value.UsageIdentity == null
                            ? value
                            : value with
                            {
                                UsageIdentity = value.UsageIdentity with
                                {
                                    Identity = value.UsageIdentity.Identity with
                                    {
                                        ResourceType = "song",
                                    },
                                },
                            }
                    )
                    .ToArray(),
            };
        context = context with { Before = Replace(context.Before), After = Replace(context.After) };

        evaluator
            .Evaluate(context, Policy())
            .Should()
            .NotContain(value => value.Disposition == MusicUsageDecisionDisposition.Authorized);
    }

    [Test]
    public void RecordingWithdrawalRequiresEstablishedCurrentNeighborContentCorrespondence()
    {
        var context = RecordingWithdrawal();
        var decision = evaluator
            .Evaluate(context, Policy())
            .Single(value => value.Operation == MusicMaterializationOperation.Withdrawal);
        decision.Disposition.Should().Be(MusicUsageDecisionDisposition.Authorized);
        decision.Features.Should().Contain(MusicUsageFeature.SameAcceptedContentAnchors);

        evaluator
            .Evaluate(context with { KnownCorrespondences = null }, Policy())
            .Where(value => value.Operation == MusicMaterializationOperation.Withdrawal)
            .Should()
            .NotContain(value => value.Disposition == MusicUsageDecisionDisposition.Authorized);
    }

    [Test]
    public void RecordingNeighborSongIdentityAloneCannotReplaceAnAcceptedAnchorDecision()
    {
        var context = RecordingWithdrawal();
        context = context with
        {
            KnownCorrespondences = context
                .KnownCorrespondences!.Where(value =>
                    value.Claim.ProcessingAttemptUuid
                    == context.Before.Claims[0].Claim.ProcessingAttemptUuid
                )
                .ToArray(),
        };

        evaluator
            .Evaluate(context, Policy())
            .Should()
            .NotContain(value => value.Disposition == MusicUsageDecisionDisposition.Authorized);
    }

    [TestCase(SetlistAuthorizationAction.Block, false)]
    [TestCase(SetlistAuthorizationAction.Block, true)]
    [TestCase(SetlistAuthorizationAction.Revoke, false)]
    [TestCase(SetlistAuthorizationAction.Revoke, true)]
    public void RecordingWithdrawalCannotUseABlockedOrRevokedNeighbor(
        SetlistAuthorizationAction action,
        bool byTarget
    )
    {
        var context = RecordingWithdrawal();
        context = context with
        {
            AuthorizationHeads =
            [
                new(
                    Guid.NewGuid(),
                    MusicMaterializationOperation.Correspondence,
                    byTarget ? null : context.After.Claims[0].Claim,
                    byTarget ? context.AcceptedTargets[0].Uuid : null,
                    action
                ),
            ],
        };

        evaluator
            .Evaluate(context, Policy())
            .Single(value => value.Operation == MusicMaterializationOperation.Withdrawal)
            .Disposition.Should()
            .Be(MusicUsageDecisionDisposition.Review);
    }

    [TestCase(SetlistAuthorizationAction.Block, false)]
    [TestCase(SetlistAuthorizationAction.Block, true)]
    [TestCase(SetlistAuthorizationAction.Revoke, false)]
    [TestCase(SetlistAuthorizationAction.Revoke, true)]
    public void NativeUsageWithdrawalCannotBypassACurrentAnchorMappingBlock(
        SetlistAuthorizationAction action,
        bool byTarget
    )
    {
        var context = NativeSetImage();
        context = context with
        {
            After = context.After with
            {
                Claims = [context.After.Claims[0], context.After.Claims[2]],
            },
            AuthorizationHeads =
            [
                new(
                    Guid.NewGuid(),
                    MusicMaterializationOperation.Correspondence,
                    byTarget ? null : context.After.Claims[0].Claim,
                    byTarget ? context.AcceptedTargets[0].Uuid : null,
                    action
                ),
            ],
        };

        evaluator
            .Evaluate(context, Policy())
            .Single(value => value.Operation == MusicMaterializationOperation.Withdrawal)
            .Disposition.Should()
            .Be(MusicUsageDecisionDisposition.Review);
    }

    [Test]
    public void MultisegmentFileDoesNotInferCorrespondenceFromSongAndPosition()
    {
        var context = RecordingWithdrawal();

        evaluator
            .Evaluate(context, Policy())
            .Where(value => value.Operation == MusicMaterializationOperation.Correspondence)
            .Should()
            .NotContain(value => value.Disposition == MusicUsageDecisionDisposition.Authorized);
    }

    [Test]
    public void RealEvaluatedArtifactRemainsApplicableWithoutCreatingOrActivatingOne()
    {
        RepertoireConsumerCorrespondenceEvaluator
            .Supports(Policy().Policy.Artifact with { FixtureOnly = false })
            .Should()
            .BeTrue();
    }

    private static MusicUsageCorrespondenceContext SingleFile()
    {
        var before = FileImage([Claim(0, "before", Song)]);
        var after = FileImage([Claim(7, "after", Song)]);
        return new(before, after, [Target(before.Claims[0])], [], EmptyRead());
    }

    private static MusicUsageCorrespondenceContext QualifiedSetSong()
    {
        MusicUsageSourceImage Image(string version)
        {
            var fact = Claim(version == "before" ? 0 : 7, version, Song);
            fact = fact with
            {
                Claim = fact.Claim with
                {
                    TrackUuid = null,
                    SegmentPosition = null,
                    AccountIndex = 0,
                    SetIndex = 0,
                },
            };
            return new(
                MusicUsageKind.Occurrence,
                new(
                    new("fixture", "setlist", "stable-account"),
                    new("fixture", "show", "stable-show"),
                    SetIdentity: Identity("show_set", "native-set", fact.Claim.InputRevisionUuid)
                ),
                Processor,
                RepertoireConsumerCorrespondenceEvaluator.SourceFactsVersion,
                [fact],
                [],
                []
            );
        }
        var before = Image("before");
        return new(before, Image("after"), [Target(before.Claims[0])], [], EmptyRead());
    }

    private static MusicUsageCorrespondenceContext NativeSetImage()
    {
        MusicUsageSourceImage Image(string version)
        {
            var claims = Enumerable
                .Range(0, 3)
                .Select(position => new MusicUsageClaimFact(
                    Reference(position, version) with
                    {
                        TrackUuid = null,
                        SegmentPosition = null,
                        ProposalUuid = null,
                        AccountIndex = 0,
                        SetIndex = position,
                    },
                    position,
                    "set",
                    null,
                    position == 1
                        ? null
                        : Identity(
                            "show_set",
                            "native-set-" + position,
                            Reference(position, version).InputRevisionUuid
                        ),
                    [Evidence(Reference(position, version).InputRevisionUuid)]
                ))
                .ToArray();
            return new(
                MusicUsageKind.Set,
                new(
                    new("fixture", "setlist", "stable-account"),
                    new("fixture", "show", "stable-show")
                ),
                Processor,
                RepertoireConsumerCorrespondenceEvaluator.SourceFactsVersion,
                claims,
                []
            );
        }
        var before = Image("before");
        return new(before, Image("after"), before.Claims.Select(Target).ToArray(), [], EmptyRead());
    }

    private static MusicUsageCorrespondenceContext RecordingWithdrawal()
    {
        var before = FileImage([
            Claim(0, "before", Guid.NewGuid()),
            Claim(1, "before", Song),
            Claim(2, "before", Guid.NewGuid()),
        ]);
        var after = FileImage([
            Claim(0, "after", before.Claims[0].RepertoireSongUuid!.Value),
            Claim(1, "after", before.Claims[2].RepertoireSongUuid!.Value),
        ]);
        var targets = before.Claims.Select(Target).ToArray();
        MusicUsageKnownCorrespondence Known(
            MusicUsageClaimFact fact,
            MusicUsageAcceptedTarget target
        ) => new(fact.Claim, target.Uuid, target.Revision, new(Guid.NewGuid(), Guid.NewGuid()));
        return new(
            before,
            after,
            targets,
            [],
            EmptyRead(),
            [
                Known(before.Claims[0], targets[0]),
                Known(before.Claims[2], targets[2]),
                Known(after.Claims[0], targets[0]),
                Known(after.Claims[1], targets[2]),
            ]
        );
    }

    private static MusicUsageSourceImage FileImage(IReadOnlyList<MusicUsageClaimFact> claims) =>
        new(
            MusicUsageKind.TrackContent,
            new(
                new("fixture", "recording", "stable-recording"),
                new("fixture", "file", "stable-file"),
                Track,
                Identity("file", "stable-file", claims[0].Claim.InputRevisionUuid)
            ),
            Processor,
            RepertoireConsumerCorrespondenceEvaluator.SourceFactsVersion,
            claims,
            []
        );

    private static MusicUsageClaimFact Claim(int position, string version, Guid song) =>
        new(
            Reference(position, version),
            position,
            "song",
            song,
            null,
            [Evidence(Reference(position, version).InputRevisionUuid)]
        );

    private static MusicUsageClaimReference Reference(int position, string version) =>
        new(
            version == "before"
                ? Guid.Parse("ba914748-23e8-4279-9b2d-e03a5808a9a0")
                : Guid.Parse("cf8a187d-9663-43ac-b580-a0240ec48925"),
            version == "before"
                ? Guid.Parse("26d896c8-2316-4a03-b8e3-971472e401d3")
                : Guid.Parse("69786045-729f-42d2-9b02-91a8689e1893"),
            ProposalUuid: Guid.Parse("889e4298-583e-4c2f-96b6-9d4401e0f2e9"),
            TrackUuid: Track,
            SegmentPosition: position
        );

    private static MusicUsageAcceptedTarget Target(MusicUsageClaimFact claim) =>
        new(
            Guid.NewGuid(),
            1,
            claim.Claim,
            claim.Position,
            claim.ContentKind,
            claim.RepertoireSongUuid,
            "provider_assertion",
            false,
            claim.Evidence
        );

    private static NormalizedEvidenceReference Evidence(Guid input) =>
        new(input, "/retained/source", Quote: "source assertion");

    private static ObservedProviderIdentity Identity(string type, string key, Guid input) =>
        new(new("fixture", type, key), Evidence(input));

    private static RepertoireResolutionReadSet EmptyRead() =>
        new([], [], [], [], null, [], [], [], []);

    private static ActiveMusicMaterializationPolicy Policy()
    {
        var artifact = new MusicMaterializationPolicyArtifact(
            Guid.NewGuid(),
            "fixture-v1",
            "",
            Processor,
            Processor,
            RepertoireConsumerCorrespondenceEvaluator.SourceFactsVersion,
            RepertoireConsumerCorrespondenceEvaluator.Version,
            Enum.GetValues<MusicMaterializationRule>(),
            FixtureOnly: true
        );
        artifact = artifact with
        {
            ContentHash = RepertoireConsumerPolicyService.ComputeArtifactHash(artifact),
        };
        var receipt = new MusicMaterializationEvaluationReceipt(
            Guid.NewGuid(),
            artifact.Uuid,
            artifact.ContentHash,
            "",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            "independent-fixture-evaluation-v1",
            "independent-fixture-evaluator",
            "fixture-reviewer",
            new(12, 0),
            new(8, 0),
            FixtureOnly: true
        );
        receipt = receipt with
        {
            ReceiptHash = RepertoireConsumerPolicyService.ComputeEvaluationHash(receipt),
        };
        return new(new(artifact, receipt), new(Guid.NewGuid(), 1, 0, Guid.NewGuid()));
    }
}
