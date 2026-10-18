using System.Collections;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>
/// Serialization is a contract boundary: repeated storage may shrink, while public values,
/// original clocks, optional historical fields and replay equality retain their exact meaning.
/// The separate integration fixture exercises real extraction, admission and publication.
/// </summary>
[TestFixture]
public sealed class TestNormalizationPeopleCheckpointCodec
{
    [Test]
    public void RichFactsQueriesAndOriginalOwnershipRoundtripThroughEveryCheckpointRoot()
    {
        var preparation = Preparation();
        var batch = Batch(preparation);
        var plan = new PeopleAcceptancePlan(batch, null, [], NegativeReason: "inactive");
        var stage = Stage(preparation);

        Same(
            preparation,
            PeopleCheckpointCodec.ReadPreparation(
                PeopleCheckpointCodec.WritePreparation(preparation)
            )
        );
        Same(batch, PeopleCheckpointCodec.ReadBatch(PeopleCheckpointCodec.WriteBatch(batch)));
        Same(plan, PeopleCheckpointCodec.ReadPlan(PeopleCheckpointCodec.WritePlan(plan)));
        Same(stage, PeopleCheckpointCodec.ReadStage(PeopleCheckpointCodec.WriteStage(stage)));

        var restored = PeopleCheckpointCodec.ReadPreparation(
            PeopleCheckpointCodec.WritePreparation(preparation)
        );
        var claims = restored.Inputs.Single().Claims;
        claims[0].Candidates!.Facts.Single().Hit.NameSimilarity.Should().Be(1);
        claims[1].Candidates!.Facts.Single().Hit.NameSimilarity.Should().Be(0.5);
        claims[0]
            .Candidates!.Facts.Single()
            .ActorEvidence!.Single()
            .Facts.Anchor.Quote.Should()
            .Be("Zoë 🎙 & <raw>");
        claims[0]
            .Candidates!.ReadSet.OwnerObservations.Should()
            .Equal(preparation.Inputs.Single().Claims[0].Candidates!.ReadSet.OwnerObservations);
    }

    [Test]
    public void RepeatedEvidenceAcrossManyClaimsFitsWithoutDroppingAnyExpandedValues()
    {
        var preparation = Preparation(new string('x', 5 * 1024 * 1024), claims: 8);
        JsonSerializer
            .SerializeToUtf8Bytes(preparation)
            .Length.Should()
            .BeGreaterThan(PeopleCheckpointCodec.MaximumCheckpointBytes);

        var stored = PeopleCheckpointCodec.WritePreparation(preparation);
        Encoding
            .UTF8.GetByteCount(stored)
            .Should()
            .BeLessThan(PeopleCheckpointCodec.MaximumCheckpointBytes);
        var restored = PeopleCheckpointCodec.ReadPreparation(stored);
        restored.Inputs.Single().Claims.Should().HaveCount(8);
        Same(preparation, restored);
    }

    [Test]
    public void RepeatedImmutableEvidenceDoesNotGetSerializedOncePerClaim()
    {
        var preparation = Preparation(claims: 40);
        var input = preparation.Inputs.Single();
        var evidence = new CountedList<CandidateActorEvidenceFact>(
            input.Claims[0].Candidates!.Facts.Single().ActorEvidence!
        );
        preparation = preparation with
        {
            Inputs =
            [
                input with
                {
                    Claims = input
                        .Claims.Select(claim =>
                            WithFact(claim, fact => fact with { ActorEvidence = evidence })
                        )
                        .ToArray(),
                },
            ],
        };

        var stored = PeopleCheckpointCodec.WritePreparation(preparation);
        evidence.Enumerations.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(3);
        PeopleCheckpointCodec.ReadPreparation(stored).Inputs.Single().Claims.Should().HaveCount(40);
    }

    [Test]
    public void GenuinelyDifferentEvidenceRemainsBoundedInsteadOfBeingSharedByPersonUuid()
    {
        var preparation = Preparation(new string('x', 5 * 1024 * 1024), claims: 8);
        var input = preparation.Inputs.Single();
        preparation = preparation with
        {
            Inputs =
            [
                input with
                {
                    Claims = input
                        .Claims.Select(
                            (claim, index) =>
                                WithFact(
                                    claim,
                                    fact =>
                                    {
                                        var witness = fact.ActorEvidence!.Single();
                                        return fact with
                                        {
                                            ActorEvidence =
                                            [
                                                witness with
                                                {
                                                    SupportUuid = Guid.NewGuid(),
                                                    Facts = witness.Facts with
                                                    {
                                                        Anchor = witness.Facts.Anchor with
                                                        {
                                                            Quote = new string(
                                                                (char)('a' + index),
                                                                5 * 1024 * 1024
                                                            ),
                                                        },
                                                    },
                                                },
                                            ],
                                        };
                                    }
                                )
                        )
                        .ToArray(),
                },
            ],
        };

        var distinctClaims = new CountedList<NormalizationPeopleResolutionClaim>(
            preparation.Inputs.Single().Claims
        );
        preparation = preparation with
        {
            Inputs = [preparation.Inputs.Single() with { Claims = distinctClaims }],
        };
        Action save = () => PeopleCheckpointCodec.WritePreparation(preparation);
        save.Should().Throw<ArgumentException>();
        distinctClaims.Visits.Should().BeLessThan(distinctClaims.Count);
    }

    [Test]
    public void LegacyRootsAndOmittedHistoricalFieldsKeepTheirPublicValues()
    {
        var preparation = Preparation();
        var batch = Batch(preparation);
        var plan = new PeopleAcceptancePlan(batch, null, []);
        var stage = Stage(preparation) with
        {
            ScopeCoverage = null,
            PolicyState = null,
            DecisionOnly = false,
            PeerTicket = null,
        };

        Same(
            preparation,
            PeopleCheckpointCodec.ReadPreparation(JsonSerializer.Serialize(preparation))
        );
        Same(batch, PeopleCheckpointCodec.ReadBatch(JsonSerializer.Serialize(batch)));
        var oldPlan = JsonNode.Parse(JsonSerializer.Serialize(plan))!.AsObject();
        oldPlan.Remove("PolicyState");
        oldPlan.Remove("NegativeReason");
        Same(plan, PeopleCheckpointCodec.ReadPlan(oldPlan.ToJsonString()));
        var oldStage = JsonNode.Parse(JsonSerializer.Serialize(stage))!.AsObject();
        foreach (
            var field in new[] { "ScopeCoverage", "PolicyState", "DecisionOnly", "PeerTicket" }
        )
        {
            oldStage.Remove(field);
        }
        Same(stage, PeopleCheckpointCodec.ReadStage(oldStage.ToJsonString()));
        PeopleCheckpointCodec
            .EquivalentPreparation(
                PeopleCheckpointCodec.ReadPreparation(JsonSerializer.Serialize(preparation)),
                PeopleCheckpointCodec.ReadPreparation(
                    PeopleCheckpointCodec.WritePreparation(preparation)
                )
            )
            .Should()
            .BeTrue();
    }

    [Test]
    public void NullAndEmptyEvidenceAndObservationListsStayDistinct()
    {
        var preparation = Preparation();
        var input = preparation.Inputs.Single();
        preparation = preparation with
        {
            Inputs =
            [
                input with
                {
                    Claims =
                    [
                        WithFact(input.Claims[0], fact => fact with { ActorEvidence = null }),
                        WithFact(input.Claims[1], fact => fact with { ActorEvidence = [] }),
                        input.Claims[0] with
                        {
                            Candidates = null,
                        },
                    ],
                },
            ],
        };
        Same(
            preparation,
            PeopleCheckpointCodec.ReadPreparation(
                PeopleCheckpointCodec.WritePreparation(preparation)
            )
        );
        var stage = Stage(preparation);
        stage = stage with
        {
            Contributions =
            [
                stage.Contributions[0] with
                {
                    ExpectedOwnerObservations = null,
                },
                stage.Contributions[0] with
                {
                    ExpectedOwnerObservations = [],
                },
            ],
            Ticket = stage.Ticket! with { OwnerObservations = null },
            PeerTicket = stage.Ticket! with { OwnerObservations = [] },
        };
        Same(stage, PeopleCheckpointCodec.ReadStage(PeopleCheckpointCodec.WriteStage(stage)));
    }

    [Test]
    public void EqualityIgnoresStorageIndexesButPreservesChangedQueriesWitnessesAndClocks()
    {
        var preparation = Preparation();
        var reordered = JsonNode
            .Parse(PeopleCheckpointCodec.WritePreparation(preparation))!
            .AsObject();
        var snapshots = reordered["CandidateSnapshots"]!.AsArray();
        var old = snapshots.Select(node => node!.DeepClone()).Reverse().ToArray();
        snapshots.Clear();
        foreach (var snapshot in old)
        {
            snapshots.Add(snapshot);
        }
        foreach (var claim in reordered["Value"]!["Inputs"]![0]!["Claims"]!.AsArray())
        {
            var index = claim!["CandidateSnapshotRef"]!.GetValue<int>();
            claim["CandidateSnapshotRef"] = old.Length - 1 - index;
        }
        PeopleCheckpointCodec
            .EquivalentPreparation(
                preparation,
                PeopleCheckpointCodec.ReadPreparation(reordered.ToJsonString())
            )
            .Should()
            .BeTrue();

        var original = preparation.Inputs.Single().Claims[0];
        var changedHit = WithFact(
            original,
            fact => fact with { Hit = fact.Hit with { Reasons = ["different"] } }
        );
        var changedSupport = WithFact(
            original,
            fact =>
                fact with
                {
                    ActorEvidence =
                    [
                        fact.ActorEvidence!.Single() with
                        {
                            SupportUuid = Guid.NewGuid(),
                        },
                    ],
                }
        );
        var changedFlag = WithFact(
            original,
            fact => fact with { ActorEvidenceTruncated = !fact.ActorEvidenceTruncated }
        );
        var changedClock = original with
        {
            Candidates = original.Candidates! with
            {
                ReadSet = original.Candidates!.ReadSet with
                {
                    OwnerObservations =
                    [
                        original.Candidates!.ReadSet.OwnerObservations[0] with
                        {
                            EditorialRevision = 99,
                        },
                    ],
                },
            },
        };
        foreach (var changed in new[] { changedHit, changedSupport, changedFlag, changedClock })
        {
            PeopleCheckpointCodec.EquivalentClaim(original, changed).Should().BeFalse();
            Same(changed, RoundtripClaim(changed));
        }
    }

    [TestCase("negative_snapshot")]
    [TestCase("missing_snapshot")]
    [TestCase("fractional_snapshot")]
    [TestCase("inline_snapshot")]
    [TestCase("inline_evidence")]
    [TestCase("missing_evidence_mapping")]
    [TestCase("missing_owner")]
    [TestCase("unused_evidence")]
    [TestCase("unknown_format")]
    [TestCase("missing_format")]
    [TestCase("wrong_kind")]
    public void MalformedReferencesAndUnknownFormatsCannotProducePublicFacts(string alteration)
    {
        var value = JsonNode
            .Parse(PeopleCheckpointCodec.WritePreparation(Preparation()))!
            .AsObject();
        var claim = value["Value"]!["Inputs"]![0]!["Claims"]![0]!;
        var snapshot = value["CandidateSnapshots"]![0]!;
        switch (alteration)
        {
            case "negative_snapshot":
                claim["CandidateSnapshotRef"] = -1;
                break;
            case "missing_snapshot":
                claim["CandidateSnapshotRef"] = 999;
                break;
            case "fractional_snapshot":
                claim["CandidateSnapshotRef"] = 0.5;
                break;
            case "inline_snapshot":
                claim["Value"]!["Candidates"] = JsonSerializer.SerializeToNode(
                    Preparation().Inputs.Single().Claims[0].Candidates
                );
                break;
            case "inline_evidence":
                snapshot["Value"]!["Facts"]![0]!["ActorEvidence"] = value["ActorEvidenceBlocks"]![
                    0
                ]!.DeepClone();
                break;
            case "missing_evidence_mapping":
                snapshot["ActorEvidenceRefs"] = new JsonArray();
                break;
            case "missing_owner":
                value["OwnerObservationLists"]![0]![0] = 999;
                break;
            case "unused_evidence":
                value["ActorEvidenceBlocks"]!.AsArray().Add(new JsonArray());
                break;
            case "unknown_format":
                value["Format"] = "people-checkpoint-future";
                break;
            case "missing_format":
                value.Remove("Format");
                break;
            case "wrong_kind":
                value["Kind"] = "acceptance_stage";
                break;
            default:
                throw new AssertionException("Unknown test alteration.");
        }
        Action read = () => PeopleCheckpointCodec.ReadPreparation(value.ToJsonString());
        read.Should().Throw<JsonException>();
    }

    [Test]
    public void DuplicateEnvelopePropertiesFailRatherThanOverwritingAReferenceTable()
    {
        var json = PeopleCheckpointCodec.WritePreparation(Preparation());
        json = json.Insert(1, "\"Format\":\"people-checkpoint-v2\",");
        Action read = () => PeopleCheckpointCodec.ReadPreparation(json);
        read.Should().Throw<JsonException>();
    }

    [Test]
    public void JsonTextFormattingDoesNotChangeTheAdmittedCheckpointBudget()
    {
        var empty = Preparation("");
        var overhead = Encoding.UTF8.GetByteCount(PeopleCheckpointCodec.WritePreparation(empty));
        var preparation = Preparation(
            new string('x', PeopleCheckpointCodec.MaximumCheckpointBytes - overhead - 32)
        );
        var compact = PeopleCheckpointCodec.WritePreparation(preparation);
        var formatted = JsonNode
            .Parse(compact)!
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        Encoding
            .UTF8.GetByteCount(formatted)
            .Should()
            .BeGreaterThan(PeopleCheckpointCodec.MaximumCheckpointBytes);
        Same(preparation, PeopleCheckpointCodec.ReadPreparation(formatted));
    }

    [Test]
    public void NearLimitLegacyRowsRemainReadableAndComparableWithoutNewWriteAdmission()
    {
        var empty = UnresolvedPreparation("");
        var overhead = JsonSerializer.SerializeToUtf8Bytes(empty).Length;
        var original = UnresolvedPreparation(
            new string('x', PeopleCheckpointCodec.MaximumCheckpointBytes - overhead - 1)
        );
        var legacy = JsonSerializer.Serialize(original);
        Encoding
            .UTF8.GetByteCount(legacy)
            .Should()
            .BeLessThan(PeopleCheckpointCodec.MaximumCheckpointBytes);
        var read = PeopleCheckpointCodec.ReadPreparation(legacy);
        PeopleCheckpointCodec.EquivalentPreparation(original, read).Should().BeTrue();
        Action newWrite = () => PeopleCheckpointCodec.WritePreparation(read);
        newWrite.Should().Throw<ArgumentException>();
    }

    [Test]
    public void ApplicationsCompareSeparatelyBoundedStagesWithoutAnAggregateWriteBudget()
    {
        var preparation = Preparation(new string('x', 5 * 1024 * 1024));
        var stage = Stage(preparation);
        var stored = PeopleCheckpointCodec.WriteStage(stage);
        var stages = Enumerable
            .Range(0, 8)
            .Select(_ => PeopleCheckpointCodec.ReadStage(stored))
            .ToArray();
        var application = new PeopleAcceptanceApplication(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "fixture",
            null,
            stages,
            null
        );
        JsonSerializer
            .SerializeToUtf8Bytes(application)
            .Length.Should()
            .BeGreaterThan(PeopleCheckpointCodec.MaximumCheckpointBytes);
        PeopleCheckpointCodec
            .EquivalentApplication(application, application with { Stages = stages.ToArray() })
            .Should()
            .BeTrue();
        PeopleCheckpointCodec
            .EquivalentApplication(
                application,
                application with
                {
                    Stages = stages
                        .Select(
                            (item, index) =>
                                index == 7
                                    ? item with
                                    {
                                        Receipt = new(item.WorkUuid, null, []),
                                    }
                                    : item
                        )
                        .ToArray(),
                }
            )
            .Should()
            .BeFalse();
    }

    private sealed class CountedList<T>(IReadOnlyList<T> values) : IReadOnlyList<T>
    {
        public int Enumerations { get; private set; }
        public int Visits { get; private set; }
        public int Count => values.Count;
        public T this[int index] => values[index];

        public IEnumerator<T> GetEnumerator()
        {
            Enumerations++;
            foreach (var value in values)
            {
                Visits++;
                yield return value;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static NormalizationPeopleResolutionClaim RoundtripClaim(
        NormalizationPeopleResolutionClaim claim
    )
    {
        var preparation = Preparation();
        preparation = preparation with
        {
            Inputs = [preparation.Inputs.Single() with { Claims = [claim] }],
        };
        return PeopleCheckpointCodec
            .ReadPreparation(PeopleCheckpointCodec.WritePreparation(preparation))
            .Inputs.Single()
            .Claims.Single();
    }

    private static void Same<T>(T expected, T actual) =>
        JsonElement
            .DeepEquals(
                JsonSerializer.SerializeToElement(expected),
                JsonSerializer.SerializeToElement(actual)
            )
            .Should()
            .BeTrue();

    private static NormalizationPeopleResolutionClaim WithFact(
        NormalizationPeopleResolutionClaim claim,
        Func<PersonCandidateFacts, PersonCandidateFacts> change
    ) =>
        claim with
        {
            Candidates = claim.Candidates! with
            {
                Facts = [change(claim.Candidates!.Facts.Single())],
            },
        };

    private static NormalizationPeopleResolutionPreparation UnresolvedPreparation(string quote)
    {
        var preparation = Preparation(claims: 0);
        var input = preparation.Inputs.Single();
        return preparation with
        {
            CreditContext = preparation.CreditContext with { IdentityOwnerObservations = [] },
            Inputs =
            [
                input with
                {
                    UnresolvedCredits =
                    [
                        new("unresolved", new(Guid.NewGuid(), "/notes", Quote: quote)),
                    ],
                },
            ],
        };
    }

    private static NormalizationPeopleResolutionPreparation Preparation(
        string quote = "Zoë 🎙 & <raw>",
        int claims = 2
    )
    {
        var source = Guid.NewGuid();
        var artist = Guid.NewGuid();
        var person = Guid.NewGuid();
        var revision = Guid.NewGuid();
        var resource = new NormalizationResourceKey("fixture", "recording", source.ToString());
        var citation = new NormalizedEvidenceReference(revision, "/taper", 0, 13, "Zoë 🎙 & <raw>");
        var anchor = citation with { Quote = quote, EndCodePoint = quote.EnumerateRunes().Count() };
        var owner = new NormalizationSubjectObservation(Guid.NewGuid(), 3, 4, Guid.NewGuid());
        var actor = new CreditedActorFacts<NormalizedEvidenceReference>(
            "actor-fixture",
            Guid.NewGuid(),
            Guid.NewGuid(),
            anchor,
            [new(Guid.NewGuid(), Guid.NewGuid(), "Z", [citation])],
            [
                new(
                    Guid.NewGuid(),
                    "https://example.test/z",
                    "example",
                    "profile",
                    "z",
                    ActorProfileRelationship.CreditedProjectProfile,
                    [citation]
                ),
            ],
            [new(Guid.NewGuid(), "z@example.test", "email", "z@example.test", [citation])],
            [new(Guid.NewGuid(), ActorContextKind.NamedRoleContext, [citation])],
            new("person", [citation]),
            [
                new(
                    Guid.NewGuid(),
                    ActorReferenceRelationship.JointRoles,
                    [Guid.NewGuid()],
                    [RecordingCreditRole.Taper, RecordingCreditRole.Transferrer],
                    [citation]
                ),
            ],
            [new(ActorFactKind.Contacts, false, "partial", [citation])],
            [new("unattributed", [Guid.NewGuid()], [citation])]
        );
        var evidence = new CandidateActorEvidenceFact(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            2,
            "provider_assertion",
            person,
            source,
            owner.SubjectUuid,
            resource,
            Guid.NewGuid(),
            Guid.NewGuid(),
            revision,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "explicit_credit",
            "Zoë",
            actor
        );
        var hit = new NormalizationCandidateHit(person, 5, "Zoë", owner.SubjectUuid, 1, ["name"]);
        var provenance = new CandidateAssertionProvenance(
            Guid.NewGuid(),
            person,
            owner.SubjectUuid,
            2,
            "provider_assertion",
            [citation]
        );
        var fact = new PersonCandidateFacts
        {
            Hit = hit,
            Header = new(person, "Zoë", 5, owner.SubjectUuid, "person", true),
            IdentityClosure = [new(person, "Zoë", 5, owner.SubjectUuid)],
            Redirects = [new(Guid.NewGuid(), person, Guid.NewGuid())],
            Aliases = [new(provenance, "Z", "fr", new(1970, 1, 1), new(1980, 1, 1))],
            ExternalIdentities = [new(provenance, "example", "profile", "z")],
            ActorEvidence = [evidence],
            ActorMatchingFacts = new(
                true,
                ["Zoë"],
                ["Z"],
                [new("email", "z@example.test")],
                [new("example", "profile", "z", ActorProfileRelationship.CreditedProjectProfile)],
                ["person"],
                true,
                false
            ),
            ActorContextCount = 99,
            ActorEvidenceTruncated = true,
            ArtistContexts =
            [
                new(
                    artist,
                    3,
                    [new("taper", "provider_assertion", 3)],
                    [
                        new(
                            provenance,
                            source,
                            "taper",
                            "Zoë",
                            null,
                            "explicit_credit",
                            CandidateCreditContextStatus.Artist,
                            artist
                        ),
                    ],
                    true
                ),
            ],
            MissingSourceCreditCount = 1,
            MissingArtistCreditCount = 2,
            MissingContextWitnesses =
            [
                new(
                    provenance,
                    source,
                    "transferrer",
                    "Z",
                    null,
                    "explicit_credit",
                    CandidateCreditContextStatus.MissingArtist
                ),
            ],
            MissingContextWitnessesTruncated = true,
        };
        var snapshot = new NormalizationCandidateFactsSnapshot<PersonCandidateFacts>(
            "people-candidate-facts-v4",
            "query-fixture",
            new(IdentityMatchingDomain.People, ["Zoë"], []),
            new()
            {
                MaximumActorEvidenceBytes = 8 * 1024 * 1024,
                MaximumActorMatchingBytes = 4 * 1024 * 1024,
            },
            new(IdentityMatchingDomain.People, [hit], false),
            [fact],
            new([new(NormalizationCandidateUniverseKey.People, 7)], [owner], []),
            [new(CandidateFactsIssueKind.MissingCatalogContext, person)]
        );
        var proposals = Enumerable
            .Range(0, claims)
            .Select(index => new NormalizationPeopleResolutionClaim(
                new(
                    Guid.NewGuid(),
                    new(
                        "Zoë",
                        index % 2 == 0
                            ? RecordingCreditRole.Taper
                            : RecordingCreditRole.Transferrer,
                        citation
                    )
                ),
                artist,
                index % 2 == 0 ? null : Guid.NewGuid(),
                null,
                snapshot with
                {
                    QueryHash = "query-" + index,
                    Selection = snapshot.Selection with
                    {
                        Items = [hit with { NameSimilarity = index == 0 ? 1 : 0.5 }],
                    },
                    Facts =
                    [
                        fact with
                        {
                            Hit = hit with { NameSimilarity = index == 0 ? 1 : 0.5 },
                        },
                    ],
                },
                [
                    new(
                        person,
                        IdentityMatchingDomain.People,
                        IdentityMatchDisposition.Review,
                        true,
                        1,
                        [],
                        [],
                        null,
                        null,
                        null
                    ),
                ],
                new(PeopleResolutionAction.Review, "fixture")
            ))
            .ToArray();
        var binding = new NormalizationRecordingBindingSnapshot(
            revision,
            resource,
            new(
                NormalizationCatalogBindingStatus.Bound,
                new(artist, source, Guid.NewGuid(), null, []),
                "bound"
            ),
            [],
            [],
            []
        );
        return new(
            new(Guid.NewGuid(), 2, 1, Guid.NewGuid()),
            new(
                source,
                owner,
                new(NormalizationCandidateUniverseKey.People, 7),
                [],
                [],
                [],
                [owner],
                [new(revision, resource)]
            ),
            1,
            [new(resource, 1)],
            [new(new(resource, 1, binding), 1, proposals, [], [new("retained_issue", citation)])]
        );
    }

    private static NormalizationPeopleResolutionBatch Batch(
        NormalizationPeopleResolutionPreparation preparation
    ) =>
        new(
            Guid.NewGuid(),
            preparation.CreditContext.SourceUuid,
            Guid.NewGuid(),
            "fixture",
            preparation,
            Ticket(preparation)
        );

    private static NormalizationWorkTicket Ticket(
        NormalizationPeopleResolutionPreparation preparation
    ) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            3,
            4,
            "fixture",
            Guid.NewGuid(),
            [],
            [],
            OwnerObservations: preparation.CreditContext.IdentityOwnerObservations
        );

    private static PeopleAcceptanceStage Stage(NormalizationPeopleResolutionPreparation preparation)
    {
        var contribution = new NormalizationPreparedInputContribution(
            new(preparation.Inputs.Single().OriginalInput.Contributor, 1, [], []),
            [],
            ExpectedOwnerObservations: preparation.CreditContext.IdentityOwnerObservations
        );
        return new(
            Guid.NewGuid(),
            null,
            null,
            preparation,
            [],
            [contribution],
            3,
            [],
            null,
            null,
            Ticket(preparation),
            new(Guid.NewGuid(), null, []),
            [],
            new(null, preparation.ExtractionObservation),
            true,
            Ticket(preparation)
        );
    }
}
