using System.Collections;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationSourceActorAliasApplications
{
    [Test]
    public async Task OneThousandReviewedClaimsAndCorrespondencesShareCompleteProofWithoutLosingTypedValues()
    {
        var stage = await ReviewedCodecStage();
        var scaled = ScaleReviewCodecFacts(stage, 1000);

        var encoded = SourceActorAliasApplicationCheckpointCodec.WriteStage(scaled);
        var restored = SourceActorAliasApplicationCheckpointCodec.ReadStage(encoded);

        Encoding
            .UTF8.GetByteCount(encoded)
            .Should()
            .BeLessThan(SourceActorAliasApplicationCheckpointCodec.MaximumCheckpointBytes);
        SourceActorAliasApplicationCheckpointCodec.SameStage(scaled, restored).Should().BeTrue();
        var reviews = restored.Facts.SourceReviews.Reviews;
        var proofs = restored.Facts.SourceReviews.Correspondences!;
        reviews.Should().HaveCount(1000);
        proofs.Should().HaveCount(1000);
        var expected = scaled.Facts.SourceReviews.Correspondences!;
        CodecValuesEqual(
            scaled.Facts.SourceReviews.Reviews[0].OriginalScope,
            reviews[0].OriginalScope
        );
        CodecValuesEqual(expected[0].Current.Scope, proofs[0].Current.Scope);
        CodecValuesEqual(expected[0].LocalIds, proofs[0].LocalIds);
        CodecValuesEqual(expected[0].Evidence, proofs[0].Evidence);
        CodecValuesEqual(expected[0].Target, proofs[0].Target);
        for (var index = 0; index < proofs.Count; index++)
        {
            var proof = proofs[index];
            CodecValuesEqual(
                scaled.Facts.SourceReviews.Reviews[index] with
                {
                    OriginalScope = null!,
                },
                reviews[index] with
                {
                    OriginalScope = null!,
                }
            );
            proof.OriginalReview.Should().BeSameAs(reviews[index]);
            proof.Original.Scope.Should().BeSameAs(reviews[0].OriginalScope);
            proof.Current.Scope.Should().BeSameAs(proofs[0].Current.Scope);
            proof.LocalIds.Should().BeSameAs(proofs[0].LocalIds);
            proof.Evidence.Should().BeSameAs(proofs[0].Evidence);
            proof.Target.Should().BeSameAs(proofs[0].Target);
            proof.Version.Should().Be(expected[index].Version);
            proof.Original.Coordinates.Should().Be(expected[index].Original.Coordinates);
            proof.Current.Coordinates.Should().Be(expected[index].Current.Coordinates);
            proof
                .Original.ExtractionAttemptUuid.Should()
                .Be(expected[index].Original.ExtractionAttemptUuid);
            proof
                .Current.ExtractionAttemptUuid.Should()
                .Be(expected[index].Current.ExtractionAttemptUuid);
            proof.Original.FieldText.Should().Be(expected[index].Original.FieldText);
            proof.Current.FieldText.Should().Be(expected[index].Current.FieldText);
            proof.Original.FieldEvidence.Should().Be(expected[index].Original.FieldEvidence);
            proof.Current.FieldEvidence.Should().Be(expected[index].Current.FieldEvidence);
        }
        var tables = JsonNode.Parse(encoded)!["Value"]!;
        tables["OriginalScopes"]!.AsArray().Should().HaveCount(2);
        tables["FieldBodies"]!.AsArray().Should().HaveCount(2);
        tables["LocalMaps"]!.AsArray().Should().ContainSingle();
        tables["EvidenceMaps"]!.AsArray().Should().ContainSingle();
        tables["ReviewTargets"]!.AsArray().Should().ContainSingle();
    }

    [Test]
    public async Task RepeatedLargeCorrespondenceMapsAreEnumeratedOnlyForTheirSharedBlock()
    {
        var stage = ScaleReviewCodecFacts(await ReviewedCodecStage(), 1000);
        var proof = stage.Facts.SourceReviews.Correspondences![0];
        var ids = new CodecCountedList<SourceActorReviewLocalIdMap>(proof.LocalIds);
        var evidence = new CodecCountedList<SourceActorReviewEvidenceMap>(proof.Evidence);
        stage = stage with
        {
            Facts = stage.Facts with
            {
                SourceReviews = stage.Facts.SourceReviews with
                {
                    Correspondences = stage
                        .Facts.SourceReviews.Correspondences!.Select(value =>
                            value with
                            {
                                LocalIds = ids,
                                Evidence = evidence,
                            }
                        )
                        .ToArray(),
                },
            },
        };

        var encoded = SourceActorAliasApplicationCheckpointCodec.WriteStage(stage);

        ids.Enumerations.Should().BeInRange(1, 2);
        evidence.Enumerations.Should().BeInRange(1, 2);
        SourceActorAliasApplicationCheckpointCodec
            .ReadStage(encoded)
            .Facts.SourceReviews.Correspondences.Should()
            .HaveCount(1000);
    }

    [Test]
    public async Task EqualClonedProofBlocksCoalesceWhileDifferentFieldsAndOwnerStampsRemainDistinct()
    {
        var stage = ScaleReviewCodecFacts(await ReviewedCodecStage(), 3);
        var proofs = stage.Facts.SourceReviews.Correspondences!;
        var clone = proofs[1] with
        {
            OriginalReview = proofs[1].OriginalReview with
            {
                OriginalScope = proofs[1].OriginalReview.OriginalScope with { },
            },
            Original = proofs[1].Original with { Scope = proofs[1].Original.Scope with { } },
            Current = proofs[1].Current with { Scope = proofs[1].Current.Scope with { } },
            LocalIds = proofs[1].LocalIds.ToArray(),
            Evidence = proofs[1].Evidence.ToArray(),
            Target = proofs[1].Target with
            {
                OwnerObservations = proofs[1].Target.OwnerObservations.ToArray(),
            },
        };
        var changed = proofs[2] with
        {
            Current = proofs[2].Current with
            {
                FieldText = proofs[2].Current.FieldText + " changed",
            },
            Evidence = proofs[2]
                .Evidence.Append(
                    new(
                        proofs[2].Original.FieldEvidence,
                        proofs[2].Current.FieldEvidence with
                        {
                            JsonPointer = "/other",
                        }
                    )
                )
                .ToArray(),
            Target = proofs[2].Target with
            {
                OwnerObservations = proofs[2]
                    .Target.OwnerObservations.Select(value =>
                        value with
                        {
                            EditorialRevision = value.EditorialRevision + 1,
                        }
                    )
                    .ToArray(),
            },
        };
        stage = stage with
        {
            Facts = stage.Facts with
            {
                SourceReviews = stage.Facts.SourceReviews with
                {
                    Correspondences = [proofs[0], clone, changed],
                },
            },
        };

        var encoded = SourceActorAliasApplicationCheckpointCodec.WriteStage(stage);
        var restored = SourceActorAliasApplicationCheckpointCodec.ReadStage(encoded);

        SourceActorAliasApplicationCheckpointCodec.SameStage(stage, restored).Should().BeTrue();
        var tables = JsonNode.Parse(encoded)!["Value"]!;
        tables["OriginalScopes"]!.AsArray().Should().HaveCount(2);
        tables["FieldBodies"]!.AsArray().Should().HaveCount(3);
        tables["LocalMaps"]!.AsArray().Should().ContainSingle();
        tables["EvidenceMaps"]!.AsArray().Should().HaveCount(2);
        tables["ReviewTargets"]!.AsArray().Should().HaveCount(2);
    }

    [Test]
    public async Task ReviewCodecRetainsAbsentAndEmptyCorrespondenceInventoriesSeparately()
    {
        var stage = await ReviewedCodecStage();
        foreach (var values in new IReadOnlyList<ReviewedSourceCorrespondence>?[] { null, [] })
        {
            var expected = stage with
            {
                Facts = stage.Facts with
                {
                    SourceReviews = stage.Facts.SourceReviews with { Correspondences = values },
                },
            };
            var restored = SourceActorAliasApplicationCheckpointCodec.ReadStage(
                SourceActorAliasApplicationCheckpointCodec.WriteStage(expected)
            );
            CodecValuesEqual(values, restored.Facts.SourceReviews.Correspondences);
            SourceActorAliasApplicationCheckpointCodec
                .SameStage(expected, restored)
                .Should()
                .BeTrue();
        }
    }

    [TestCase("invalid_scope")]
    [TestCase("invalid_field_body")]
    [TestCase("invalid_review")]
    [TestCase("invalid_local_map")]
    [TestCase("invalid_evidence_map")]
    [TestCase("invalid_target")]
    [TestCase("unused_scope")]
    [TestCase("unused_field_body")]
    [TestCase("unused_review")]
    [TestCase("unused_correspondence")]
    [TestCase("hidden_reviews")]
    [TestCase("hidden_correspondences")]
    public async Task ReviewCodecRejectsInvalidUnusedAndHiddenProofBlocks(string alteration)
    {
        var stage = ScaleReviewCodecFacts(await ReviewedCodecStage(), 2);
        var json = JsonNode.Parse(SourceActorAliasApplicationCheckpointCodec.WriteStage(stage))!;
        var tables = json["Value"]!;
        switch (alteration)
        {
            case "invalid_scope":
                tables["Reviews"]![0]!["Scope"] = -1;
                break;
            case "invalid_field_body":
                tables["Fields"]![0]!["Body"] = int.MaxValue;
                break;
            case "invalid_review":
                tables["Correspondences"]![0]!["OriginalReview"] = -1;
                break;
            case "invalid_local_map":
                tables["Correspondences"]![0]!["LocalIds"] = -1;
                break;
            case "invalid_evidence_map":
                tables["Correspondences"]![0]!["Evidence"] = -1;
                break;
            case "invalid_target":
                tables["Correspondences"]![0]!["Target"] = -1;
                break;
            case "unused_scope":
                tables["OriginalScopes"]!.AsArray().Add(tables["OriginalScopes"]![0]!.DeepClone());
                break;
            case "unused_field_body":
                tables["FieldBodies"]!.AsArray().Add(tables["FieldBodies"]![0]!.DeepClone());
                break;
            case "unused_review":
                tables["Reviews"]!.AsArray().Add(tables["Reviews"]![0]!.DeepClone());
                break;
            case "unused_correspondence":
                tables["Correspondences"]!
                    .AsArray()
                    .Add(tables["Correspondences"]![0]!.DeepClone());
                break;
            case "hidden_reviews":
                tables["Value"]!["Facts"]!["SourceReviews"]!["Reviews"] =
                    JsonSerializer.SerializeToNode(stage.Facts.SourceReviews.Reviews);
                break;
            case "hidden_correspondences":
                tables["Value"]!["Facts"]!["SourceReviews"]!["Correspondences"] =
                    JsonSerializer.SerializeToNode(stage.Facts.SourceReviews.Correspondences);
                break;
        }

        Action read = () =>
            SourceActorAliasApplicationCheckpointCodec.ReadStage(json.ToJsonString());

        read.Should().Throw<JsonException>();
    }

    [Test]
    public async Task DistinctCorrespondenceFieldBodiesStillRefuseOverflowWithoutIncreasingTheCap()
    {
        SourceActorAliasApplicationCheckpointCodec
            .MaximumCheckpointBytes.Should()
            .Be(32 * 1024 * 1024);
        var stage = ScaleReviewCodecFacts(await ReviewedCodecStage(), 8);
        stage = stage with
        {
            Facts = stage.Facts with
            {
                SourceReviews = stage.Facts.SourceReviews with
                {
                    Correspondences = stage
                        .Facts.SourceReviews.Correspondences!.Select(
                            (value, index) =>
                                value with
                                {
                                    Current = value.Current with
                                    {
                                        FieldText = new string(
                                            (char)('a' + index),
                                            5 * 1024 * 1024
                                        ),
                                    },
                                }
                        )
                        .ToArray(),
                },
            },
        };

        Action write = () => SourceActorAliasApplicationCheckpointCodec.WriteStage(stage);

        write.Should().Throw<ArgumentException>().WithMessage("*32 MiB*no*truncated*");
    }

    private async Task<SourceActorAliasPersonStage> ReviewedCodecStage()
    {
        var name = Unique("Codec reviewed Robin");
        var person = await Person(name);
        var source = await Source(Personnel(name, Unique("Codec reviewed Finch")));
        await Review(source, SourceActorReviewAction.Accept, person);
        source = await Resolve(source.Source);
        var application = await Apply(source);
        application
            .Result!.Disposition.Should()
            .Be(SourceActorAliasApplicationDisposition.Complete);
        var stage = (await applications.GetStageAsync(application.Stages.First().StageUuid))!;
        return stage with { Ticket = null, Receipt = null, TerminalReason = null };
    }

    // Only the seed above is admitted. These scaled values exercise serialization in memory;
    // generated coordinates and proofs are never passed to an accepted writer or persisted.
    private static SourceActorAliasPersonStage ScaleReviewCodecFacts(
        SourceActorAliasPersonStage stage,
        int count
    )
    {
        var seed = stage.Facts.SourceReviews.Reviews.Single();
        var assertions = Enumerable
            .Range(0, count)
            .Select(index =>
                seed.OriginalAssertion with
                {
                    AssertionUuid = Guid.NewGuid(),
                    ActorReferenceUuid = Guid.NewGuid(),
                    Name = seed.OriginalAssertion.Name + " " + index,
                }
            )
            .ToArray();
        var originalScope = seed.OriginalScope with
        {
            Extraction = seed.OriginalScope.Extraction with { Assertions = assertions },
        };
        var currentRevision = Guid.NewGuid();
        var currentScope = originalScope with { InputRevisionUuid = currentRevision };
        var field = stage
            .Facts.Inputs.SelectMany(value => value.Scopes)
            .SelectMany(value => value.Fields)
            .Single(value => value.Evidence.JsonPointer == seed.Coordinates.FieldJsonPointer);
        var text = field.OriginalText + "\n" + new string('x', 128 * 1024) + " Zoë 🎙 & <raw>";
        var evidence = field.Evidence with
        {
            Quote = text,
            EndCodePoint = text.EnumerateRunes().Count(),
        };
        var currentEvidence = evidence with { InputRevisionUuid = currentRevision };
        var reviews = assertions
            .Select(assertion =>
                seed with
                {
                    Uuid = Guid.NewGuid(),
                    Coordinates = seed.Coordinates with
                    {
                        AssertionUuid = assertion.AssertionUuid,
                        ActorReferenceUuid = assertion.ActorReferenceUuid,
                    },
                    OriginalAssertion = assertion,
                    OriginalScope = originalScope,
                    LastOperationUuid = Guid.NewGuid(),
                }
            )
            .ToArray();
        var localIds = reviews
            .Select(value => new SourceActorReviewLocalIdMap(
                SourceActorReviewLocalIdKind.Assertion,
                Guid.NewGuid(),
                value.Coordinates.AssertionUuid,
                Guid.NewGuid()
            ))
            .ToArray();
        var evidenceMap = reviews
            .Select(value => new SourceActorReviewEvidenceMap(
                value.OriginalAssertion.NameEvidence,
                value.OriginalAssertion.NameEvidence with
                {
                    InputRevisionUuid = currentRevision,
                }
            ))
            .Prepend(new(evidence, currentEvidence))
            .ToArray();
        var currentExtraction = Guid.NewGuid();
        var target = stage.Facts.Targets.Single(value =>
            value.OriginalPersonUuid == stage.OriginalPersonUuid
        );
        var correspondences = reviews
            .Select(value => new ReviewedSourceCorrespondence(
                SourceActorReviewCorrespondence.Version,
                value,
                new(
                    stage.Facts.ExtractionAttemptUuid,
                    value.Coordinates,
                    originalScope,
                    text,
                    evidence
                ),
                new(
                    currentExtraction,
                    value.Coordinates with
                    {
                        InputRevisionUuid = currentRevision,
                    },
                    currentScope,
                    text,
                    currentEvidence
                ),
                localIds,
                evidenceMap,
                target
            ))
            .ToArray();
        return stage with
        {
            Facts = stage.Facts with
            {
                SourceReviews = stage.Facts.SourceReviews with
                {
                    Reviews = reviews,
                    Correspondences = correspondences,
                },
            },
        };
    }

    private static void CodecValuesEqual<T>(T expected, T actual) =>
        JsonElement
            .DeepEquals(
                JsonSerializer.SerializeToElement(expected),
                JsonSerializer.SerializeToElement(actual)
            )
            .Should()
            .BeTrue();

    private sealed class CodecCountedList<T>(IReadOnlyList<T> values) : IReadOnlyList<T>
    {
        public int Enumerations { get; private set; }
        public int Count => values.Count;
        public T this[int index] => values[index];

        public IEnumerator<T> GetEnumerator()
        {
            Enumerations++;
            return values.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
