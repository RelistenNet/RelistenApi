using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

/// <summary>
/// Exercises immutable transport, not source or identity authority. The public resolver
/// integration tests separately establish authentic inputs, admission and publication.
/// These fixtures deliberately retain contradictory snapshots to detect lossy storage.
/// </summary>
[TestFixture]
public sealed class TestSourceActorResolutionCheckpointV2
{
    [Test]
    public void AThousandDistinctQueriesRetainIndependentLargeFactsAndEveryClaim()
    {
        var fact = Fact(matchingNames: 2001, witnesses: 256);
        var pools = Enumerable.Range(0, 1000).Select(index => Pool(index, [Clone(fact)])).ToArray();
        var preparation = Preparation(pools);
        pools[0].Facts[0].Should().NotBeSameAs(pools[1].Facts[0]);
        pools[0].Facts[0].ActorEvidence.Should().NotBeSameAs(pools[1].Facts[0].ActorEvidence);
        ((long)JsonSerializer.SerializeToUtf8Bytes(fact).Length * pools.Length)
            .Should()
            .BeGreaterThan(SourceActorResolutionCheckpoint.MaximumCheckpointBytes);

        var stored = SourceActorResolutionCheckpoint.WritePreparation(preparation);
        Encoding
            .UTF8.GetByteCount(stored)
            .Should()
            .BeLessThan(SourceActorResolutionCheckpoint.MaximumCheckpointBytes);
        var restored = SourceActorResolutionCheckpoint.ReadPreparation(stored);

        restored.CandidatePools.Should().HaveCount(1000);
        var claims = restored.Inputs.Single().Scopes.Single().Claims;
        claims.Should().HaveCount(1000);
        for (var index = 0; index < pools.Length; index++)
        {
            // Compare one expanded snapshot at a time. A single giant expected JSON tree
            // would recreate the storage problem in this test's assertion machinery.
            Same(pools[index], restored.CandidatePools[index]);
            claims[index].CandidatePoolIndex.Should().Be(index);
            claims[index]
                .Projection.Record.ArtistUuids.Should()
                .BeEquivalentTo(
                    preparation.Inputs[0].Scopes[0].Claims[index].Projection.Record.ArtistUuids
                );
            restored
                .CandidatePools[index]
                .Facts[0]
                .AcceptedAliasMatchingNames.Should()
                .HaveCount(2001);
            restored.CandidatePools[index].Facts[0].ActorEvidence.Should().HaveCount(256);
        }
        SourceActorResolutionCheckpoint
            .EquivalentPreparation(preparation, restored)
            .Should()
            .BeTrue();

        var batch = Batch(preparation);
        var restoredBatch = SourceActorResolutionCheckpoint.ReadBatch(
            SourceActorResolutionCheckpoint.WriteBatch(batch)
        );
        SourceActorResolutionCheckpoint.EquivalentBatch(batch, restoredBatch).Should().BeTrue();
        Same(batch.Ticket, restoredBatch.Ticket);
    }

    [Test]
    public void SameIdentityDoesNotEraseChangedEvidenceOptionalFieldsOrOwnerClocks()
    {
        var original = Fact();
        var witness = original.ActorEvidence!.Single();
        var changed = original with
        {
            ActorEvidence =
            [
                witness with
                {
                    Facts = witness.Facts with
                    {
                        Anchor = witness.Facts.Anchor with
                        {
                            Quote = "Zoë 🎙 — corrected, not normalized",
                        },
                    },
                },
            ],
            AliasSupportCount = original.AliasSupportCount + 1,
        };
        var absent = original with { ActorEvidence = null, AcceptedAliasMatchingNames = null };
        var empty = original with { ActorEvidence = [], AcceptedAliasMatchingNames = [] };
        var pools = new[]
        {
            Pool(0, [original]),
            Pool(1, [changed]),
            Pool(2, [absent]),
            Pool(3, [empty]),
        };
        var firstOwner = pools[0].ReadSet.OwnerObservations[0];
        var otherOwner = new NormalizationSubjectObservation(Guid.NewGuid(), 2, 8, Guid.NewGuid());
        pools[0] = pools[0] with
        {
            ReadSet = pools[0].ReadSet with { OwnerObservations = [firstOwner, otherOwner] },
        };
        pools[1] = pools[1] with
        {
            ReadSet = pools[1].ReadSet with
            {
                OwnerObservations =
                [
                    firstOwner with
                    {
                        DesiredGeneration = firstOwner.DesiredGeneration + 1,
                    },
                    otherOwner,
                ],
            },
        };
        pools[2] = pools[2] with
        {
            ReadSet = pools[2].ReadSet with { OwnerObservations = [otherOwner, firstOwner] },
        };
        pools[3] = pools[3] with { ReadSet = pools[3].ReadSet with { OwnerObservations = [] } };
        var preparation = Preparation(pools);
        var restored = SourceActorResolutionCheckpoint.ReadPreparation(
            SourceActorResolutionCheckpoint.WritePreparation(preparation)
        );
        for (var index = 0; index < pools.Length; index++)
        {
            Same(pools[index], restored.CandidatePools[index]);
        }
        restored.CandidatePools[2].Facts[0].ActorEvidence.Should().BeNull();
        restored.CandidatePools[3].Facts[0].ActorEvidence.Should().BeEmpty();
        restored.CandidatePools[2].Facts[0].AcceptedAliasMatchingNames.Should().BeNull();
        restored.CandidatePools[3].Facts[0].AcceptedAliasMatchingNames.Should().BeEmpty();
        SourceActorResolutionCheckpoint
            .EquivalentPreparation(preparation, restored)
            .Should()
            .BeTrue();
        SourceActorResolutionCheckpoint
            .EquivalentPreparation(
                preparation,
                restored with
                {
                    CandidatePools = [pools[0], pools[0], pools[2], pools[3]],
                }
            )
            .Should()
            .BeFalse();
    }

    [Test]
    public void FactsAndRetrievalHitsKeepTheirIndependentOrderAndQuerySpecificValues()
    {
        var first = Fact();
        var second = Fact() with { AliasEvidenceTruncated = true, AliasAssertionCount = 99 };
        var pool = Pool(0, [first, second]);
        pool = pool with
        {
            Selection = pool.Selection with
            {
                Items = pool.Selection.Items.Reverse().ToArray(),
                ContextCandidatesTruncated = true,
            },
            Issues =
            [
                new(CandidateFactsIssueKind.UnknownAuthority, second.Hit.Uuid, "raw-profile"),
            ],
        };
        var preparation = Preparation([pool, Pool(1, [Clone(second), Clone(first)])]);
        var restored = SourceActorResolutionCheckpoint.ReadPreparation(
            SourceActorResolutionCheckpoint.WritePreparation(preparation)
        );
        Same(preparation, restored);
        restored
            .CandidatePools[0]
            .Selection.Items.Select(hit => hit.Uuid)
            .Should()
            .Equal(second.Hit.Uuid, first.Hit.Uuid);
        restored
            .CandidatePools[0]
            .Facts.Select(fact => fact.Hit.Uuid)
            .Should()
            .Equal(first.Hit.Uuid, second.Hit.Uuid);
        restored
            .CandidatePools[0]
            .Facts[0]
            .Hit.NameSimilarity.Should()
            .NotBe(restored.CandidatePools[1].Facts[1].Hit.NameSimilarity);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void WhitespaceFromJsonTransportPreservesTheCompleteDomainValues(bool batch)
    {
        var preparation = Preparation([Pool(0, [Fact()]), Pool(1, [Fact()])]);
        var json = Write(preparation, batch);
        var formatted = JsonNode
            .Parse(json)!
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (batch)
        {
            Same(Batch(preparation), SourceActorResolutionCheckpoint.ReadBatch(formatted));
        }
        else
        {
            Same(preparation, SourceActorResolutionCheckpoint.ReadPreparation(formatted));
        }
    }

    [TestCaseSource(nameof(InvalidDocuments))]
    public void RefusesHiddenConflictingOrNoncanonicalCandidateStorage(string change, bool batch)
    {
        var preparation = Preparation([Pool(0, [Fact()]), Pool(1, [Fact()])]);
        var root = JsonNode.Parse(Write(preparation, batch))!.AsObject();
        var value = batch ? root["Value"]!["Preparation"]!.AsObject() : root["Value"]!.AsObject();
        var pools = value["CandidatePools"]!.AsArray();
        var firstPool = pools[0]!.AsObject();
        var bodies = root["CandidateFacts"]!.AsArray();
        var owners = root["OwnerBlocks"]!.AsArray();
        switch (change)
        {
            case "negative fact ref":
                firstPool["FactRefs"]![0] = -1;
                break;
            case "out of range fact ref":
                firstPool["FactRefs"]![0] = bodies.Count;
                break;
            case "out of range owner ref":
                firstPool["OwnerBlockRef"] = owners.Count;
                break;
            case "null referenced owner block":
                owners[0] = null;
                break;
            case "null owner observation":
                owners[0]![0] = null;
                break;
            case "null referenced fact block":
                bodies[0] = null;
                break;
            case "missing hit":
                firstPool["FactHits"]!.AsArray().RemoveAt(0);
                break;
            case "missing ref":
                firstPool["FactRefs"]!.AsArray().RemoveAt(0);
                break;
            case "different identity hit":
                firstPool["FactHits"]![0]!["Uuid"] = Guid.NewGuid().ToString();
                break;
            case "different hit revision":
                firstPool["FactHits"]![0]!["Revision"] = 900;
                break;
            case "different hit display":
                firstPool["FactHits"]![0]!["DisplayText"] = "counterfeit";
                break;
            case "different hit owner":
                firstPool["FactHits"]![0]!["OwnerSubjectUuid"] = Guid.NewGuid().ToString();
                break;
            case "different body header":
                bodies[0]!["Header"]!["Uuid"] = Guid.NewGuid().ToString();
                break;
            case "different body revision":
                bodies[0]!["Header"]!["Revision"] = 900;
                break;
            case "different body owner":
                bodies[0]!["Header"]!["OwnerSubjectUuid"] = Guid.NewGuid().ToString();
                break;
            case "different body display":
                bodies[0]!["Header"]!["DisplayText"] = "counterfeit";
                break;
            case "nonneutral score":
                bodies[0]!["Hit"]!["NameSimilarity"] = 0.5;
                break;
            case "nonneutral reasons":
                bodies[0]!["Hit"]!["Reasons"] = new JsonArray("hidden");
                break;
            case "hidden inline facts":
                firstPool["Value"]!["Facts"] = new JsonArray(
                    JsonSerializer.SerializeToNode(preparation.CandidatePools[0].Facts[0])
                );
                break;
            case "hidden inline owners":
                firstPool["Value"]!["ReadSet"]!["OwnerObservations"] =
                    JsonSerializer.SerializeToNode(
                        preparation.CandidatePools[0].ReadSet.OwnerObservations
                    );
                break;
            case "unused fact":
                bodies.Add(bodies[0]!.DeepClone());
                break;
            case "unused owners":
                owners.Add(owners[0]!.DeepClone());
                break;
            case "referenced duplicate fact":
                bodies.Add(bodies[0]!.DeepClone());
                pools[1]!["FactRefs"]!.AsArray().Add(bodies.Count - 1);
                pools[1]!["FactHits"]!.AsArray().Add(firstPool["FactHits"]![0]!.DeepClone());
                break;
            case "referenced duplicate owners":
                owners.Add(owners[0]!.DeepClone());
                pools.Add(firstPool.DeepClone());
                pools[2]!["OwnerBlockRef"] = owners.Count - 1;
                break;
            case "noncanonical facts":
                Swap(bodies);
                firstPool["FactRefs"]![0] = 1;
                pools[1]!["FactRefs"]![0] = 0;
                break;
            case "noncanonical owners":
                Swap(owners);
                firstPool["OwnerBlockRef"] = 1;
                pools[1]!["OwnerBlockRef"] = 0;
                break;
            case "missing candidate table":
                root.Remove("CandidateFacts");
                break;
            case "null candidate table":
                root["CandidateFacts"] = null;
                break;
            case "missing owner table":
                root.Remove("OwnerBlocks");
                break;
            case "null owner table":
                root["OwnerBlocks"] = null;
                break;
            case "unknown field":
                firstPool["IgnoredProof"] = "must not disappear";
                break;
            case "unknown body field":
                bodies[0]!["IgnoredProof"] = "must not disappear";
                break;
            case "unknown nested field":
                firstPool["Value"]!["Query"]!["IgnoredProof"] = "must not disappear";
                break;
            case "wrong kind":
                root["Kind"] = batch ? "resolution_preparation" : "resolution_batch";
                break;
            case "unknown format":
                root["Format"] = "source-actor-resolution-checkpoint-future";
                break;
            default:
                throw new ArgumentException(change);
        }
        Action read = () => Read(root.ToJsonString(), batch);
        read.Should().Throw<JsonException>();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void UniqueEvidenceFitsAtTheByteBoundaryAndTheNextByteIsRefused(bool batch)
    {
        var preparation = UniqueEvidence("");
        var overhead = Encoding.UTF8.GetByteCount(Write(preparation, batch));
        var quote = new string(
            'x',
            SourceActorResolutionCheckpoint.MaximumCheckpointBytes - overhead
        );
        var exact = UniqueEvidence(quote, preparation);
        var stored = Write(exact, batch);
        Encoding
            .UTF8.GetByteCount(stored)
            .Should()
            .Be(SourceActorResolutionCheckpoint.MaximumCheckpointBytes);
        var restored = batch
            ? SourceActorResolutionCheckpoint.ReadBatch(stored).Preparation
            : SourceActorResolutionCheckpoint.ReadPreparation(stored);
        restored
            .CandidatePools[0]
            .Facts[0]
            .Aliases[0]
            .Provenance.Evidence[0]
            .Quote.Should()
            .Be(quote);
        SourceActorResolutionCheckpoint.EquivalentPreparation(exact, restored).Should().BeTrue();

        Action save = () => Write(UniqueEvidence(quote + "x", preparation), batch);
        save.Should()
            .Throw<SourceActorResolutionCapacityException>()
            .Which.Reason.Should()
            .Be("checkpoint_capacity_exceeded");
    }

    private static IEnumerable<TestCaseData> InvalidDocuments()
    {
        string[] changes =
        [
            "negative fact ref",
            "out of range fact ref",
            "out of range owner ref",
            "null referenced owner block",
            "null owner observation",
            "null referenced fact block",
            "missing hit",
            "missing ref",
            "different identity hit",
            "different hit revision",
            "different hit display",
            "different hit owner",
            "different body header",
            "different body revision",
            "different body owner",
            "different body display",
            "nonneutral score",
            "nonneutral reasons",
            "hidden inline facts",
            "hidden inline owners",
            "unused fact",
            "unused owners",
            "referenced duplicate fact",
            "referenced duplicate owners",
            "noncanonical facts",
            "noncanonical owners",
            "missing candidate table",
            "null candidate table",
            "missing owner table",
            "null owner table",
            "unknown field",
            "unknown body field",
            "unknown nested field",
            "wrong kind",
            "unknown format",
        ];
        foreach (var change in changes)
        {
            foreach (var batch in new[] { false, true })
            {
                yield return new TestCaseData(change, batch).SetName(
                    $"{change} ({(batch ? "batch" : "preparation")})"
                );
            }
        }
    }

    private static void Swap(JsonArray values)
    {
        var first = values[0]!.DeepClone();
        var second = values[1]!.DeepClone();
        values[0] = second;
        values[1] = first;
    }

    private static string Write(SourceActorResolutionPreparation preparation, bool batch) =>
        batch
            ? SourceActorResolutionCheckpoint.WriteBatch(Batch(preparation))
            : SourceActorResolutionCheckpoint.WritePreparation(preparation);

    private static void Read(string json, bool batch)
    {
        if (batch)
        {
            SourceActorResolutionCheckpoint.ReadBatch(json);
        }
        else
        {
            SourceActorResolutionCheckpoint.ReadPreparation(json);
        }
    }

    private static void Same<T>(T expected, T actual) =>
        JsonElement
            .DeepEquals(
                JsonSerializer.SerializeToElement(expected),
                JsonSerializer.SerializeToElement(actual)
            )
            .Should()
            .BeTrue();

    private static SourceActorResolutionBatch Batch(SourceActorResolutionPreparation preparation) =>
        new(
            preparation.ExtractionTicket.WorkUuid,
            preparation.ExtractionTicket.SubjectUuid,
            preparation.ExtractionTicket.WorkUuid,
            "source-codec-fixture",
            preparation,
            preparation.ExtractionTicket
        );

    private static SourceActorResolutionPreparation UniqueEvidence(
        string quote,
        SourceActorResolutionPreparation? original = null
    )
    {
        var preparation = original ?? Preparation([Pool(0, [Fact()])]);
        var pool = preparation.CandidatePools[0];
        var fact = pool.Facts[0];
        var alias = fact.Aliases[0];
        return preparation with
        {
            CandidatePools =
            [
                pool with
                {
                    Facts =
                    [
                        fact with
                        {
                            Aliases =
                            [
                                alias with
                                {
                                    Provenance = alias.Provenance with
                                    {
                                        Evidence =
                                        [
                                            alias.Provenance.Evidence[0] with
                                            {
                                                Quote = quote,
                                            },
                                        ],
                                    },
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }

    private static SourceActorResolutionPreparation Preparation(
        IReadOnlyList<NormalizationCandidateFactsSnapshot<PersonCandidateFacts>> pools
    )
    {
        var source = Guid.NewGuid();
        var artist = Guid.NewGuid();
        var revision = Guid.NewGuid();
        var resourceUuid = Guid.NewGuid();
        var resource = new NormalizationResourceKey("fixture", "recording", source.ToString());
        var citation = new NormalizedEvidenceReference(revision, "/description", 0, 3, "Zoë");
        var producer = "source-codec-fixture";
        var assertions = pools
            .Select(
                (pool, index) =>
                    new SourceActorAliasAssertion<NormalizedEvidenceReference>(
                        producer,
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        "field-hash",
                        pool.Query.Names[0],
                        citation,
                        [],
                        [],
                        new(citation, [], citation),
                        []
                    )
            )
            .ToArray();
        var scope = new NormalizationRecordingSourceActorAliasScope(
            producer,
            revision,
            resource,
            new(resource, citation, ProviderObservationScope.ResourceItem),
            new(assertions, [], [new("description", true, "complete", [citation])])
        );
        var claims = assertions
            .Select(
                (assertion, index) =>
                    new SourceActorResolutionClaim(
                        new(
                            source,
                            resource,
                            resourceUuid,
                            revision,
                            citation.JsonPointer,
                            producer,
                            assertion.AssertionUuid,
                            assertion.ActorReferenceUuid,
                            assertion.FieldSha256
                        ),
                        SourceActorResolutionCheckpoint.AssertionHash(assertion),
                        index,
                        new(
                            pools[index].Query,
                            new(
                                null,
                                null,
                                [new(assertion.Name)],
                                [],
                                new HashSet<Guid>
                                {
                                    artist,
                                    Guid.Parse("c65bddfb-20f4-47ba-b763-79531e782c42"),
                                }
                            ),
                            true
                        ),
                        index,
                        [],
                        null,
                        0,
                        new(SourceActorResolutionAction.Unresolved, "codec-fixture", null, null)
                    )
            )
            .ToArray();
        var extraction = new NormalizationSubjectObservation(Guid.NewGuid(), 4, 2, Guid.NewGuid());
        var ticket = new NormalizationWorkTicket(
            extraction.PublishedWorkUuid!.Value,
            extraction.SubjectUuid,
            extraction.DesiredGeneration,
            extraction.EditorialRevision,
            producer,
            null,
            [new(resourceUuid, revision, 7)],
            [],
            [new(artist, 11)]
        );
        var binding = new NormalizationRecordingBindingSnapshot(
            revision,
            resource,
            new(
                NormalizationCatalogBindingStatus.Bound,
                new(artist, source, null, null, []),
                "fixture"
            ),
            [],
            [new(artist, 11)],
            []
        );
        return new(
            extraction,
            ticket,
            new(null, new(Guid.NewGuid(), 3, 5, null)),
            new(NormalizationCandidateUniverseKey.People, 19),
            new(null, []),
            null,
            [],
            [
                new(
                    new(resource, 0, binding),
                    0,
                    true,
                    [new(scope, [new("description", "Zoë", "field-hash", citation)], claims)],
                    []
                ),
            ],
            pools
        );
    }

    private static NormalizationCandidateFactsSnapshot<PersonCandidateFacts> Pool(
        int index,
        IReadOnlyList<PersonCandidateFacts> facts
    )
    {
        var selected = facts
            .Select(
                (fact, ordinal) =>
                    fact.Hit with
                    {
                        NameSimilarity = 0.25 + ((index + ordinal) % 70) / 100.0,
                        Reasons = ["query-name", $"query-{index}-position-{ordinal}"],
                    }
            )
            .ToArray();
        var materialized = facts
            .Select((fact, ordinal) => fact with { Hit = selected[ordinal] })
            .ToArray();
        return new(
            "people-candidate-facts-v5",
            $"query-{index}",
            new(IdentityMatchingDomain.People, [$"Zoë alias {index}"], []),
            new() { MaximumAliasMatchingNames = 5000, MaximumAliasMatchingBytes = 4 * 1024 * 1024 },
            new(IdentityMatchingDomain.People, selected, false),
            materialized,
            new(
                [new(NormalizationCandidateUniverseKey.People, 19)],
                [new(facts[0].Header.OwnerSubjectUuid!.Value, 9, 4, facts[0].Hit.Uuid)],
                [new(Guid.Parse("31d6d077-e35b-421d-ae5b-ea16ad6ea037"), 7)]
            ),
            []
        );
    }

    private static PersonCandidateFacts Fact(int matchingNames = 3, int witnesses = 1)
    {
        var person = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var source = Guid.NewGuid();
        var revision = Guid.NewGuid();
        var evidence = new NormalizedEvidenceReference(
            revision,
            "/description",
            1,
            8,
            "Zoë 🎙 & <raw>"
        );
        var provenance = new CandidateAssertionProvenance(
            Guid.NewGuid(),
            person,
            owner,
            5,
            "provider_assertion",
            [evidence]
        );
        var actor = new CreditedActorFacts<NormalizedEvidenceReference>(
            "actor-fixture",
            Guid.NewGuid(),
            Guid.NewGuid(),
            evidence,
            [new(Guid.NewGuid(), Guid.NewGuid(), "Z 🎙", [evidence])],
            [],
            [],
            [],
            new("person", [evidence]),
            [],
            [],
            []
        );
        return new()
        {
            Hit = new(person, 8, "Zoë 🎙", owner, 0.9, ["name"]),
            Header = new(person, "Zoë 🎙", 8, owner, "person", true),
            IdentityClosure =
            [
                new(person, "Zoë 🎙", 8, owner),
                new(Guid.NewGuid(), "Old Z", 2, owner),
            ],
            Redirects = [new(Guid.NewGuid(), person, Guid.NewGuid())],
            Aliases = [new(provenance, "Z 🎙", "fr", new(1970, 1, 1), new(1990, 1, 1))],
            AcceptedAliasMatchingNames = Enumerable
                .Range(0, matchingNames)
                .Select(index => $"Zoë alias {index:D4}")
                .ToArray(),
            AliasMatchingNamesComplete = true,
            AliasAssertionCount = matchingNames,
            AliasSupportCount = matchingNames + 7,
            AliasEvidenceTruncated = true,
            ExternalIdentities = [new(provenance, "fixture", "profile", "zoe")],
            ActorEvidence = Enumerable
                .Range(0, witnesses)
                .Select(index => new CandidateActorEvidenceFact(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    5,
                    "provider_assertion",
                    person,
                    source,
                    owner,
                    new("fixture", "recording", source.ToString()),
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    revision,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "explicit_credit",
                    $"Zoë witness {index}",
                    actor
                ))
                .ToArray(),
            ActorMatchingFacts = new(true, ["Zoë"], ["Z 🎙"], [], [], ["person"], true, false),
            ActorContextCount = witnesses + 50,
            ActorEvidenceTruncated = true,
            ArtistContexts =
            [
                new(
                    Guid.NewGuid(),
                    17,
                    [new("taper", "provider_assertion", 17)],
                    [
                        new(
                            provenance,
                            source,
                            "taper",
                            "Zoë 🎙",
                            null,
                            "explicit_credit",
                            CandidateCreditContextStatus.Artist
                        ),
                    ],
                    true
                ),
            ],
            MissingSourceCreditCount = 3,
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
    }

    private static PersonCandidateFacts Clone(PersonCandidateFacts value) =>
        value with
        {
            Hit = value.Hit with { Reasons = value.Hit.Reasons.ToArray() },
            Header = value.Header with { },
            IdentityClosure = value.IdentityClosure.Select(header => header with { }).ToArray(),
            Redirects = value.Redirects.Select(redirect => redirect with { }).ToArray(),
            Aliases = value
                .Aliases.Select(alias =>
                    alias with
                    {
                        Provenance = alias.Provenance with
                        {
                            Evidence = alias
                                .Provenance.Evidence.Select(evidence => evidence with { })
                                .ToArray(),
                        },
                    }
                )
                .ToArray(),
            AcceptedAliasMatchingNames = value.AcceptedAliasMatchingNames?.ToArray(),
            ExternalIdentities = value
                .ExternalIdentities.Select(identity =>
                    identity with
                    {
                        Provenance = identity.Provenance with
                        {
                            Evidence = identity.Provenance.Evidence.ToArray(),
                        },
                    }
                )
                .ToArray(),
            ActorEvidence = value
                .ActorEvidence?.Select(witness =>
                    witness with
                    {
                        Facts = witness.Facts with
                        {
                            Anchor = witness.Facts.Anchor with { },
                            Aliases = witness
                                .Facts.Aliases.Select(alias =>
                                    alias with
                                    {
                                        Evidence = alias.Evidence.ToArray(),
                                    }
                                )
                                .ToArray(),
                            Profiles = witness.Facts.Profiles.ToArray(),
                            Contacts = witness.Facts.Contacts.ToArray(),
                            Context = witness.Facts.Context.ToArray(),
                            Kind = witness.Facts.Kind! with
                            {
                                Evidence = witness.Facts.Kind.Evidence.ToArray(),
                            },
                            Relationships = witness.Facts.Relationships.ToArray(),
                            Coverage = witness.Facts.Coverage.ToArray(),
                            Unresolved = witness.Facts.Unresolved.ToArray(),
                        },
                    }
                )
                .ToArray(),
            ActorMatchingFacts = value.ActorMatchingFacts! with
            {
                CreditedNames = value.ActorMatchingFacts.CreditedNames.ToArray(),
                Aliases = value.ActorMatchingFacts.Aliases.ToArray(),
                Contacts = value.ActorMatchingFacts.Contacts.ToArray(),
                Profiles = value.ActorMatchingFacts.Profiles.ToArray(),
                SourceKinds = value.ActorMatchingFacts.SourceKinds.ToArray(),
            },
            ArtistContexts = value
                .ArtistContexts.Select(context =>
                    context with
                    {
                        Counts = context.Counts.ToArray(),
                        Witnesses = context.Witnesses.ToArray(),
                    }
                )
                .ToArray(),
            MissingContextWitnesses = value.MissingContextWitnesses.ToArray(),
        };
}
