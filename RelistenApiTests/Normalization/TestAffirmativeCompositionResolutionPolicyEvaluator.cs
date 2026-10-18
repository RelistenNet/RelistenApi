using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

/// <summary>Public rule behavior only. These synthetic policies are neither independently qualified nor registered.</summary>
[TestFixture]
public sealed class TestAffirmativeCompositionResolutionPolicyEvaluator
{
    private readonly CompositionResolutionPolicyEvaluator evaluator = new();

    [Test]
    public void AcceptedPerformerAndExplicitWorkPathProposeWithoutClaimingExternalCompletenessOrAuthors()
    {
        var facts = Facts();
        facts = facts with
        {
            CoverageIssues =
            [
                "SearchPageLimit:0",
                "HydrationLimit:Work:unretained",
                "SearchCountDrift:0",
            ],
        };
        var result = Evaluate(facts);

        result.Action.Should().Be(CompositionCorrespondenceAction.ProposeAcceptance);
        result.ExternalWorkMbid.Should().Be(facts.Candidates.Single().ExternalWorkMbid);
        result.Reason.Should().Be("affirmative_accepted_performer_recording_work_correspondence");
        result.Candidates.Single().Evidence.Should().Contain(facts.SourceTitle.Evidence);
        result
            .Candidates.Single()
            .Features.Should()
            .Contain("accepted_catalog_artist_matches_recording_performer_mbid");
        facts.ExternalUniverseComplete.Should().BeFalse();
        facts.Candidates.Single().Work.CompleteAuthorship.Should().BeNull();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void MatchingRecordingToAnUnhydratedRivalCannotDisappearFromTheCandidatePool(
        bool rivalFirst
    )
    {
        var facts = Facts();
        var rival = Recording(facts.Artist.ExternalArtistMbid!.Value, Guid.NewGuid());
        var inventory = facts.RetainedRecordingInventory!;
        var rows = inventory
            .Recordings.Append(new(rival.Observation, rival.Decoded.Issues))
            .ToArray();
        var inputs = inventory.ExpectedInputs.Append(rival.Reference).ToArray();
        facts = facts with
        {
            RetainedRecordingInventory = inventory with
            {
                Recordings = rivalFirst ? rows.Reverse().ToArray() : rows,
                ExpectedInputs = rivalFirst ? inputs.Reverse().ToArray() : inputs,
            },
        };

        var result = Evaluate(facts);
        result.Action.Should().Be(CompositionCorrespondenceAction.NeedsReview);
        result
            .Candidates.Single()
            .Contradictions.Should()
            .Contain("retained_matching_recording_targets_another_work");
        result
            .Candidates.Single()
            .Evidence.Should()
            .Contain(rival.Observation.Relationships!.Single().Evidence);
        facts.Candidates.Should().ContainSingle(); // No hydrated rival candidate is available.
    }

    [Test]
    public void ASecondHydratedWorkAndAMedleyRemainCompeting()
    {
        var facts = Facts();
        var rivalId = Guid.NewGuid();
        var rival = Recording(facts.Artist.ExternalArtistMbid!.Value, rivalId);
        var rivalWork = Work(rivalId);
        facts = facts with
        {
            Candidates =
            [
                facts.Candidates.Single(),
                new(rivalId, rivalWork, [rival.Observation], []),
            ],
            RetainedRecordingInventory = facts.RetainedRecordingInventory! with
            {
                Recordings = facts
                    .RetainedRecordingInventory.Recordings.Append(
                        new(rival.Observation, rival.Decoded.Issues)
                    )
                    .ToArray(),
                ExpectedInputs = facts
                    .RetainedRecordingInventory.ExpectedInputs.Append(rival.Reference)
                    .ToArray(),
            },
        };
        Evaluate(facts).Action.Should().Be(CompositionCorrespondenceAction.NeedsReview);

        var medley = Recording(
            facts.Artist.ExternalArtistMbid.Value,
            facts.Candidates[0].ExternalWorkMbid,
            rivalId
        );
        var single = facts.Candidates[0] with { Recordings = [medley.Observation] };
        var medleyFacts = facts with
        {
            Candidates = [single],
            RetainedRecordingInventory = Inventory(medley),
        };
        Evaluate(medleyFacts)
            .Candidates.Single()
            .Contradictions.Should()
            .Contain("retained_matching_recording_targets_another_work");
    }

    [TestCase(true)]
    [TestCase(false)]
    public void OtherPerformersOrOtherTitlesDoNotInventACompetingWork(bool otherPerformer)
    {
        var facts = Facts();
        var unrelated = Recording(
            otherPerformer ? Guid.NewGuid() : facts.Artist.ExternalArtistMbid!.Value,
            Guid.NewGuid(),
            title: otherPerformer ? "Song" : "Different Song"
        );
        var inventory = facts.RetainedRecordingInventory!;
        facts = facts with
        {
            RetainedRecordingInventory = inventory with
            {
                Recordings = inventory
                    .Recordings.Append(new(unrelated.Observation, unrelated.Decoded.Issues))
                    .ToArray(),
                ExpectedInputs = inventory.ExpectedInputs.Append(unrelated.Reference).ToArray(),
            },
        };
        Evaluate(facts).Action.Should().Be(CompositionCorrespondenceAction.ProposeAcceptance);
    }

    [Test]
    public void SameTitleWithoutTheAcceptedPerformerCannotQualify()
    {
        var facts = Facts();
        facts = facts with { Artist = facts.Artist with { ExternalArtistMbid = Guid.NewGuid() } };
        Evaluate(facts).Action.Should().Be(CompositionCorrespondenceAction.NeedsReview);
        facts = facts with { Artist = facts.Artist with { ExternalArtistMbid = null } };
        Evaluate(facts).Action.Should().Be(CompositionCorrespondenceAction.NeedsReview);
    }

    [TestCase("absent")]
    [TestCase("incomplete")]
    [TestCase("missing_entry")]
    [TestCase("duplicate_reference")]
    [TestCase("wrong_input_uuid")]
    [TestCase("wrong_resource")]
    [TestCase("wrong_recording_identity")]
    [TestCase("diagnostic")]
    public void MissingOrMismatchedRetainedRecordingCoverageCannotBeDeclaredComplete(string fault)
    {
        var facts = Facts();
        var inventory = facts.RetainedRecordingInventory!;
        var row = inventory.Recordings.Single();
        var reference = inventory.ExpectedInputs.Single();
        inventory = fault switch
        {
            "incomplete" => inventory with { Complete = false },
            "missing_entry" => inventory with { Recordings = [] },
            "duplicate_reference" => inventory with { ExpectedInputs = [reference, reference] },
            "wrong_input_uuid" => inventory with
            {
                ExpectedInputs = [reference with { InputRevisionUuid = Guid.NewGuid() }],
            },
            "wrong_resource" => inventory with
            {
                ExpectedInputs =
                [
                    reference with
                    {
                        Resource = new(
                            "musicbrainz.org",
                            "recording",
                            Guid.NewGuid().ToString("D")
                        ),
                    },
                ],
            },
            "wrong_recording_identity" => inventory with
            {
                Recordings =
                [
                    row with
                    {
                        Observation = row.Observation with
                        {
                            Recording = row.Observation.Recording with
                            {
                                Identity = row.Observation.Recording.Identity with
                                {
                                    Identity = new(
                                        "musicbrainz.org",
                                        "recording",
                                        Guid.NewGuid().ToString("D")
                                    ),
                                },
                            },
                        },
                    },
                ],
            },
            "diagnostic" => inventory with
            {
                Recordings =
                [
                    row with
                    {
                        Issues =
                        [
                            new("invalid_provider_identity", row.Observation.Origin.Evidence),
                        ],
                    },
                ],
            },
            _ => inventory,
        };
        facts = facts with { RetainedRecordingInventory = fault == "absent" ? null : inventory };
        var result = Evaluate(facts);
        result.Action.Should().Be(CompositionCorrespondenceAction.NeedsReview);
        result.Reason.Should().Be("incomplete_or_inconsistent_retained_recording_inventory");
    }

    [Test]
    public void CandidateCannotInjectACleanRecordingOutsideTheRetainedInventory()
    {
        var facts = Facts();
        var forged = Recording(
            facts.Artist.ExternalArtistMbid!.Value,
            facts.Candidates.Single().ExternalWorkMbid
        );
        facts = facts with
        {
            Candidates = [facts.Candidates.Single() with { Recordings = [forged.Observation] }],
        };
        Evaluate(facts)
            .Reason.Should()
            .Be("incomplete_or_inconsistent_retained_recording_inventory");
    }

    [Test]
    public void MalformedMatchingRivalRelationshipAndMismatchedHydratedWorkNeedReview()
    {
        var facts = Facts();
        var candidate = facts.Candidates.Single();
        var recording = facts.RetainedRecordingInventory!.Recordings.Single().Observation;
        var relation = recording.Relationships!.Single();
        var malformed = recording with
        {
            Relationships = [relation, relation with { Target = null }],
        };
        facts = facts with
        {
            Candidates = [candidate with { Recordings = [malformed] }],
            RetainedRecordingInventory = facts.RetainedRecordingInventory with
            {
                Recordings = [new(malformed, [])],
            },
        };
        Evaluate(facts)
            .Candidates.Single()
            .Contradictions.Should()
            .Contain("matching_recording_has_malformed_work_relationship");

        facts = Facts();
        candidate = facts.Candidates.Single();
        facts = facts with
        {
            Candidates =
            [
                candidate with
                {
                    Work = candidate.Work with
                    {
                        Origin = candidate.Work.Origin with
                        {
                            Item = new("musicbrainz.org", "work", Guid.NewGuid().ToString("D")),
                        },
                    },
                },
            ],
        };
        Evaluate(facts)
            .Candidates.Single()
            .Contradictions.Should()
            .Contain("hydrated_work_provenance_mismatch");
    }

    [TestCase("type")]
    [TestCase("direction")]
    [TestCase("target-type")]
    public void MissingRelationshipClassificationCannotHideARivalInActualDecodedBytes(
        string missingField
    )
    {
        var facts = Facts();
        var candidate = facts.Candidates.Single();
        var partial = Recording(
            facts.Artist.ExternalArtistMbid!.Value,
            candidate.ExternalWorkMbid,
            Guid.NewGuid(),
            missingRivalField: missingField
        );
        // Optional missing classification fields produce no decoder issue. The rule must
        // inspect the retained member before deciding which relationships corroborate it.
        partial.Decoded.Issues.Should().BeEmpty();
        facts = facts with
        {
            Candidates = [candidate with { Recordings = [partial.Observation] }],
            RetainedRecordingInventory = Inventory(partial),
        };
        Evaluate(facts)
            .Candidates.Single()
            .Contradictions.Should()
            .Contain("matching_recording_has_incomplete_work_relationship");
    }

    [TestCase("credit")]
    [TestCase("credit_identity")]
    [TestCase("relation")]
    [TestCase("relation_type")]
    [TestCase("relation_target")]
    public void NestedAssertionsCannotBorrowEvidenceFromAnotherRetainedInput(string fault)
    {
        var facts = Facts();
        var inventory = facts.RetainedRecordingInventory!;
        var row = inventory.Recordings.Single();
        var observation = row.Observation;
        var credit = observation.ArtistCredits!.Single();
        var relation = observation.Relationships!.Single();
        var wrongInput = Guid.NewGuid();
        if (fault == "credit")
        {
            credit = credit with
            {
                Evidence = credit.Evidence with { InputRevisionUuid = wrongInput },
            };
        }
        if (fault == "credit_identity")
        {
            credit = credit with
            {
                Artist = credit.Artist! with
                {
                    Identity = credit.Artist!.Identity with
                    {
                        Evidence = credit.Artist.Identity.Evidence with
                        {
                            InputRevisionUuid = wrongInput,
                        },
                    },
                },
            };
        }
        if (fault == "relation")
        {
            relation = relation with
            {
                Evidence = relation.Evidence with { InputRevisionUuid = wrongInput },
            };
        }
        if (fault == "relation_type")
        {
            relation = relation with
            {
                Type = relation.Type! with
                {
                    Evidence = relation.Type!.Evidence with { InputRevisionUuid = wrongInput },
                },
            };
        }
        if (fault == "relation_target")
        {
            relation = relation with
            {
                Target = relation.Target! with
                {
                    Identity = relation.Target!.Identity with
                    {
                        Evidence = relation.Target.Identity.Evidence with
                        {
                            InputRevisionUuid = wrongInput,
                        },
                    },
                },
            };
        }
        observation = observation with { ArtistCredits = [credit], Relationships = [relation] };
        facts = facts with
        {
            Candidates = [facts.Candidates.Single() with { Recordings = [observation] }],
            RetainedRecordingInventory = inventory with
            {
                Recordings = [row with { Observation = observation }],
            },
        };
        Evaluate(facts)
            .Reason.Should()
            .Be("incomplete_or_inconsistent_retained_recording_inventory");
    }

    [TestCase("input")]
    [TestCase("resource")]
    [TestCase("version")]
    public void InventoryBuilderChecksTheDecodedInputHeaderAgainstTheReceipt(string fault)
    {
        var recording = Recording(Guid.NewGuid(), Guid.NewGuid());
        var decoded = fault switch
        {
            "input" => recording.Decoded with { InputRevisionUuid = Guid.NewGuid() },
            "resource" => recording.Decoded with
            {
                Resource = new("musicbrainz.org", "recording", Guid.NewGuid().ToString("D")),
            },
            _ => recording.Decoded with { DecoderVersion = "unsupported" },
        };
        var inventory = CompositionRetainedRecordingInventoryBuilder.Build(
            [recording.Lookup],
            new Dictionary<Guid, ProviderDecodeResult> { [recording.Lookup.CommandUuid] = decoded }
        );
        inventory.Complete.Should().BeFalse();
        inventory
            .Issues.Should()
            .Contain(issue =>
                issue.StartsWith("RecordingDecodeIdentityMismatch:", StringComparison.Ordinal)
            );
    }

    [Test]
    public void InventoryBuilderRetainsARivalWithoutAWorkLookupAndRefusesMissingDecodedInput()
    {
        var artist = Guid.NewGuid();
        var first = Recording(artist, Guid.NewGuid());
        var rival = Recording(artist, Guid.NewGuid());
        var inputs = new Dictionary<Guid, ProviderDecodeResult>
        {
            [first.Lookup.CommandUuid] = first.Decoded,
            [rival.Lookup.CommandUuid] = rival.Decoded,
        };
        var complete = CompositionRetainedRecordingInventoryBuilder.Build(
            [first.Lookup, rival.Lookup],
            inputs
        );
        complete.Complete.Should().BeTrue();
        complete.Recordings.Should().HaveCount(2);
        complete
            .Recordings[1]
            .Observation.Relationships!.Single()
            .Target!.Identity.Identity.Should()
            .NotBe(first.Observation.Relationships!.Single().Target!.Identity.Identity);
        inputs.Remove(rival.Lookup.CommandUuid);
        var incomplete = CompositionRetainedRecordingInventoryBuilder.Build(
            [first.Lookup, rival.Lookup],
            inputs
        );
        incomplete.Complete.Should().BeFalse();
        incomplete.ExpectedInputs.Should().HaveCount(2);
        incomplete
            .Issues.Should()
            .Contain(issue =>
                issue.StartsWith("RecordingInputUnavailable:", StringComparison.Ordinal)
            );
    }

    [Test]
    public void UnacquiredLookupDoesNotClaimAbsenceOrMakeARetainedReadsetIncomplete()
    {
        var first = Recording(Guid.NewGuid(), Guid.NewGuid());
        var failed = first.Lookup with
        {
            CommandUuid = Guid.NewGuid(),
            Request = first.Lookup.Request with { Mbid = Guid.NewGuid() },
            Acquisition = first.Lookup.Acquisition! with
            {
                State = MusicBrainzAcquisitionState.Failed,
                Result = new(MusicBrainzAcquisitionDisposition.Failed, Reason: "http_503"),
            },
        };
        var inventory = CompositionRetainedRecordingInventoryBuilder.Build(
            [first.Lookup, failed],
            new Dictionary<Guid, ProviderDecodeResult>
            {
                [first.Lookup.CommandUuid] = first.Decoded,
            }
        );
        inventory.Complete.Should().BeTrue();
        inventory.ExpectedInputs.Should().ContainSingle();
        inventory.Recordings.Should().ContainSingle();
    }

    [Test]
    public void FixtureRuleCannotBeRelabeledAndMissingInventoryCannotBorrowFixtureApproval()
    {
        var policy = BehaviorPolicy(fixture: true);
        CompositionResolutionPolicyEvaluator.Supports(policy.Artifact).Should().BeTrue();
        CompositionResolutionPolicyEvaluator
            .Supports(policy.Artifact with { FixtureOnly = false })
            .Should()
            .BeFalse();
        var relabeled = policy with
        {
            Artifact = policy.Artifact with
            {
                RuleId = CompositionResolutionPolicyEvaluator.AffirmativeRuleId,
                FixtureOnly = false,
            },
        };
        evaluator
            .Evaluate(Facts(), relabeled)
            .Reason.Should()
            .Be("unsupported_policy_or_fact_versions");
        Evaluate(Facts() with { RetainedRecordingInventory = null })
            .Action.Should()
            .Be(CompositionCorrespondenceAction.NeedsReview);
    }

    [Test]
    public void OmittedInventoryPreservesLegacyFactsJsonAndFixtureHashes()
    {
        var facts = Facts() with { RetainedRecordingInventory = null };
        var serialized = JsonSerializer.Serialize(facts);
        using var document = JsonDocument.Parse(serialized);
        document
            .RootElement.TryGetProperty(
                nameof(CompositionCorrespondenceFacts.RetainedRecordingInventory),
                out _
            )
            .Should()
            .BeFalse();
        JsonSerializer
            .Serialize(JsonSerializer.Deserialize<CompositionCorrespondenceFacts>(serialized))
            .Should()
            .Be(serialized);
        var policy = BehaviorPolicy(fixture: true);
        var legacyArtifact = policy.Artifact with
        {
            Uuid = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Version = "fixture-v1",
            DecoderVersion = "provider-observations-v7",
        };
        CompositionResolutionPolicyService
            .ComputeArtifactHash(legacyArtifact)
            .Should()
            .Be("9d78d87bd461acfdd277c94919b7f8463548b2dc6511aa59639456241eadcdc7");
        evaluator
            .Evaluate(facts, policy)
            .Reason.Should()
            .Be("fixture_corroborated_recording_work_correspondence");
    }

    private CompositionCorrespondenceDecision Evaluate(CompositionCorrespondenceFacts facts) =>
        evaluator.Evaluate(facts, BehaviorPolicy());

    private static RegisteredCompositionAcceptancePolicy BehaviorPolicy(bool fixture = false)
    {
        var artifact = new CompositionAcceptancePolicyArtifact(
            Guid.NewGuid(),
            "unregistered-behavior-test-only",
            "",
            CompositionResolutionPolicyEvaluator.ProcessorVersion,
            CompositionResolutionPolicyEvaluator.FactsVersion,
            MusicBrainzCandidateQueryBuilder.Version,
            NormalizationProviderDecoder.Version,
            fixture
                ? CompositionResolutionPolicyEvaluator.FixtureRuleId
                : CompositionResolutionPolicyEvaluator.AffirmativeRuleId,
            fixture
        );
        // Zero cases and absent attestations deliberately fail registry qualification.
        // This test value can exercise the pure rule but cannot be registered or activated.
        var receipt = new CompositionAcceptanceEvaluationReceipt(
            Guid.NewGuid(),
            artifact.Uuid,
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            new(0, 0),
            fixture
        );
        return new(artifact, receipt);
    }

    private static CompositionCorrespondenceFacts Facts()
    {
        var artist = Guid.NewGuid();
        var work = Guid.NewGuid();
        var recording = Recording(artist, work);
        return new(
            Guid.NewGuid(),
            new("Song", new(Guid.NewGuid(), "/title")),
            new(Guid.NewGuid(), "Performer", artist),
            [new(work, Work(work), [recording.Observation], [])],
            true,
            false,
            [],
            CompositionResolutionPolicyEvaluator.ProcessorVersion,
            CompositionResolutionPolicyEvaluator.FactsVersion,
            MusicBrainzCandidateQueryBuilder.Version,
            NormalizationProviderDecoder.Version,
            RetainedRecordingInventory: Inventory(recording)
        );
    }

    private static CompositionRetainedRecordingInventory Inventory(RecordingFixture recording) =>
        CompositionRetainedRecordingInventoryBuilder.Build(
            [recording.Lookup],
            new Dictionary<Guid, ProviderDecodeResult>
            {
                [recording.Lookup.CommandUuid] = recording.Decoded,
            }
        );

    private static MusicBrainzWorkObservation Work(Guid id) =>
        Decode(
            "work",
            id,
            new
            {
                id,
                title = "Song",
                relations = Array.Empty<object>(),
            }
        )
            .Decoded.MusicBrainzWorks!.Single();

    private static RecordingFixture Recording(
        Guid artist,
        Guid work,
        Guid? otherWork = null,
        string title = "Song",
        string? missingRivalField = null
    )
    {
        object Relation(Guid target) =>
            new Dictionary<string, object>
            {
                ["type"] = "performance",
                ["direction"] = "forward",
                ["target-type"] = "work",
                ["work"] = new { id = target, title = "Song" },
            };
        var relations = new List<object> { Relation(work) };
        if (otherWork.HasValue)
        {
            var rival = (Dictionary<string, object>)Relation(otherWork.Value);
            if (missingRivalField != null)
            {
                rival.Remove(missingRivalField);
            }
            relations.Add(rival);
        }
        var id = Guid.NewGuid();
        var input = Decode(
            "recording",
            id,
            new Dictionary<string, object>
            {
                ["id"] = id,
                ["title"] = title,
                ["artist-credit"] = new[]
                {
                    new { name = "Performer", artist = new { id = artist, name = "Performer" } },
                },
                ["relations"] = relations,
            }
        );
        return new(input.Input, input.Decoded);
    }

    private static (NormalizationInputRevision Input, ProviderDecodeResult Decoded) Decode(
        string kind,
        Guid id,
        object body
    )
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        var input = new NormalizationInputRevision
        {
            Uuid = Guid.NewGuid(),
            Resource = new("musicbrainz.org", kind, id.ToString("D")),
            Payload = bytes,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            MediaType = "application/json",
            CaptureVersion =
                kind == "recording"
                    ? "musicbrainz-recording-work-json-v1"
                    : "musicbrainz-work-json-v1",
        };
        return (input, new NormalizationProviderDecoder().Decode(input));
    }

    private sealed record RecordingFixture(
        NormalizationInputRevision Input,
        ProviderDecodeResult Decoded
    )
    {
        internal MusicBrainzRecordingObservation Observation =>
            Decoded.MusicBrainzRecordings!.Single();
        internal NormalizationBindingInputReference Reference => new(Input.Uuid, Input.Resource);
        internal CompositionHydrationLookup Lookup
        {
            get
            {
                var request = new MusicBrainzLookupRequest(
                    MusicBrainzLookupKind.Recording,
                    Guid.Parse(Input.Resource.ProviderKey),
                    MusicBrainzCacheMode.ReuseFresh,
                    TimeSpan.FromDays(7)
                );
                return new(
                    Input.Uuid,
                    request,
                    new(
                        Input.Uuid,
                        request,
                        "synthetic-completed-test",
                        MusicBrainzAcquisitionState.Completed,
                        null,
                        new(MusicBrainzAcquisitionDisposition.Captured, Reference),
                        []
                    )
                );
            }
        }
    }
}
