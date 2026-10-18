using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>
/// Source publication has its own format. Immutable 55/56/stage references remain references;
/// this codec interns repeated candidate, review, correspondence and owner proof blocks within one
/// bounded stage. It never merges conflicting stamps or discards evidence to satisfy capacity.
/// </summary>
internal static class SourceActorAliasApplicationCheckpointCodec
{
    internal const string FormatVersion = "source-actor-alias-checkpoint-v1";
    internal const int MaximumCheckpointBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new ReadOnlyGuidSetJsonConverter() },
    };

    internal static string WritePlan(SourceActorAliasApplicationPlan value) => Write("plan", value);

    internal static SourceActorAliasApplicationPlan ReadPlan(string json) =>
        Read<SourceActorAliasApplicationPlan>(json, "plan");

    internal static string WriteReceipt(SourceActorAliasStageReceipt value) =>
        Write("receipt", value);

    internal static SourceActorAliasStageReceipt ReadReceipt(string json) =>
        Read<SourceActorAliasStageReceipt>(json, "receipt");

    internal static string WriteResult(SourceActorAliasApplicationResult value) =>
        Write("result", value);

    internal static SourceActorAliasApplicationResult ReadResult(string json) =>
        Read<SourceActorAliasApplicationResult>(json, "result");

    internal static string WriteTicket(NormalizationWorkTicket value) => Write("ticket", value);

    internal static NormalizationWorkTicket ReadTicket(string json) =>
        Read<NormalizationWorkTicket>(json, "ticket");

    internal static string WriteStage(SourceActorAliasPersonStage value)
    {
        var encoder = new Encoder();
        return Write("stage", encoder.Encode(value));
    }

    internal static SourceActorAliasPersonStage ReadStage(string json)
    {
        var stored = Read<StoredStage>(json, "stage");
        var decoder = new Decoder(stored);
        var stage = decoder.Decode();
        if (!Same(stored, new Encoder().Encode(stage)))
        {
            throw new JsonException(
                "Source alias checkpoint has unused, hidden or noncanonical local proof blocks."
            );
        }
        return stage;
    }

    internal static bool Same<T>(T left, T right) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(left),
            JsonSerializer.SerializeToElement(right)
        );

    internal static bool SameApplication(
        SourceActorAliasApplication left,
        SourceActorAliasApplication right
    )
    {
        // Recovery carries compact stage headers; full proof bodies are independently bounded
        // and compared through SameStage only when explicitly loaded by their immutable ID.
        return Same(left, right);
    }

    internal static bool SameStage(
        SourceActorAliasPersonStage left,
        SourceActorAliasPersonStage right
    )
    {
        var leftBody = left with { Ticket = null, Receipt = null, TerminalReason = null };
        var rightBody = right with { Ticket = null, Receipt = null, TerminalReason = null };
        return Same(new Encoder().Encode(leftBody), new Encoder().Encode(rightBody))
            && Same(left.Ticket, right.Ticket)
            && Same(left.Receipt, right.Receipt)
            && left.TerminalReason == right.TerminalReason;
    }

    /// <summary>
    /// Preparation reserves the actual encoded candidate outcome and resulting map, not an
    /// estimate based on UUID width. Call before admission with the conservative full outcome.
    /// The callback checks the real receipt again before any accepted transaction can commit.
    /// </summary>
    internal static void RequireReceiptCapacity(SourceActorAliasStageReceipt conservativeReceipt) =>
        WriteReceipt(conservativeReceipt);

    private static string Write<T>(string kind, T value)
    {
        using var stream = new BoundedStream();
        JsonSerializer.Serialize(stream, new Envelope<T>(FormatVersion, kind, value));
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static T Read<T>(string json, string kind)
    {
        var envelope =
            JsonSerializer.Deserialize<Envelope<T>>(json, ReadOptions)
            ?? throw new JsonException("Missing source alias checkpoint envelope.");
        if (envelope.Format != FormatVersion || envelope.Kind != kind || envelope.Value == null)
        {
            throw new JsonException("Unsupported source alias checkpoint version or kind.");
        }
        // JSONB transport whitespace is not part of the canonical encoded byte budget.
        Write(kind, envelope.Value);
        return envelope.Value;
    }

    private sealed record Envelope<T>(string Format, string Kind, T Value);

    private sealed record StoredPool(
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts> Value,
        int Owners,
        IReadOnlyList<int> FactRefs,
        IReadOnlyList<NormalizationCandidateHit> Hits
    );

    private sealed record StoredCandidateFact(PersonCandidateFacts Value, int? ActorEvidence);

    private sealed record StoredReview(SourceActorAliasReviewState Value, int Scope);

    private sealed record StoredFieldBody(
        Guid ExtractionAttemptUuid,
        int Scope,
        string Text,
        NormalizedEvidenceReference Evidence
    );

    private sealed record StoredField(SourceActorAliasCoordinates Coordinates, int Body);

    private sealed record StoredTarget(SourceActorReviewTarget Value, int Owners);

    private sealed record StoredCorrespondence(
        string Version,
        int OriginalReview,
        int OriginalField,
        int CurrentField,
        int LocalIds,
        int Evidence,
        int Target
    );

    private sealed record StoredStage(
        SourceActorAliasPersonStage Value,
        IReadOnlyList<NormalizationSubjectObservation> Owners,
        IReadOnlyList<IReadOnlyList<int>> OwnerLists,
        IReadOnlyList<IReadOnlyList<CandidateActorEvidenceFact>> ActorBlocks,
        IReadOnlyList<StoredPool> Pools,
        IReadOnlyList<StoredCandidateFact> CandidateFacts,
        IReadOnlyList<int> PoolRefs,
        int? ReadOwners,
        int CarryOwners,
        int? ExtractionOwners,
        IReadOnlyList<int> TargetOwners,
        IReadOnlyList<int?> ContributionOwners,
        IReadOnlyList<NormalizationRecordingSourceActorAliasScope> OriginalScopes,
        IReadOnlyList<StoredReview> Reviews,
        IReadOnlyList<StoredFieldBody> FieldBodies,
        IReadOnlyList<StoredField> Fields,
        IReadOnlyList<IReadOnlyList<SourceActorReviewLocalIdMap>> LocalMaps,
        IReadOnlyList<IReadOnlyList<SourceActorReviewEvidenceMap>> EvidenceMaps,
        IReadOnlyList<StoredTarget> ReviewTargets,
        IReadOnlyList<StoredCorrespondence> Correspondences,
        IReadOnlyList<int> ReviewRefs,
        IReadOnlyList<int>? CorrespondenceRefs
    );

    private sealed class Encoder
    {
        private readonly List<NormalizationSubjectObservation> owners = [];
        private readonly List<IReadOnlyList<int>> ownerLists = [];
        private readonly List<IReadOnlyList<CandidateActorEvidenceFact>> actorBlocks = [];
        private readonly List<StoredPool> pools = [];
        private readonly List<StoredCandidateFact> candidateFacts = [];
        private readonly Dictionary<string, int> ownerIndex = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> listIndex = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> actorIndex = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> poolIndex = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> factIndex = new(StringComparer.Ordinal);

        private readonly BlockTable<NormalizationRecordingSourceActorAliasScope> scopes = new();
        private readonly BlockTable<StoredReview> reviews = new();
        private readonly List<StoredFieldBody> fieldBodies = [];
        private readonly Dictionary<StoredFieldBody, int> fieldBodyIndex = [];
        private readonly BlockTable<StoredField> fields = new();
        private readonly BlockTable<IReadOnlyList<SourceActorReviewLocalIdMap>> localMaps = new();
        private readonly BlockTable<IReadOnlyList<SourceActorReviewEvidenceMap>> evidenceMaps =
            new();
        private readonly BlockTable<StoredTarget> reviewTargets = new();
        private readonly BlockTable<StoredCorrespondence> correspondences = new();
        private readonly Dictionary<object, int> reviewReferences = new(
            ReferenceEqualityComparer.Instance
        );
        private readonly Dictionary<object, int> fieldReferences = new(
            ReferenceEqualityComparer.Instance
        );
        private readonly Dictionary<object, int> targetReferences = new(
            ReferenceEqualityComparer.Instance
        );

        internal StoredStage Encode(SourceActorAliasPersonStage stage)
        {
            if (stage.Ticket != null || stage.Receipt != null || stage.TerminalReason != null)
            {
                throw new ArgumentException(
                    "Only the immutable pre-admission stage body is encoded as a preparation."
                );
            }
            var facts = stage.Facts;
            var readOwners = OptionalOwners(facts.CurrentReadSet.OwnerObservations);
            var carryOwners = Owners(facts.CarryObservations);
            var extractionOwners = OptionalOwners(facts.OriginalExtractionTicket.OwnerObservations);
            var targetOwners = facts
                .Targets.Select(target => Owners(target.OwnerObservations))
                .ToArray();
            var contributionOwners = stage
                .Contributions.Select(value => OptionalOwners(value.ExpectedOwnerObservations))
                .ToArray();
            var poolRefs = facts.CandidatePools.Select(Pool).ToArray();
            var reviewRefs = facts.SourceReviews.Reviews.Select(Review).ToArray();
            var correspondenceRefs = facts
                .SourceReviews.Correspondences?.Select(Correspondence)
                .ToArray();
            var stripped = stage with
            {
                Contributions = stage
                    .Contributions.Select(value => value with { ExpectedOwnerObservations = null })
                    .ToArray(),
                Facts = facts with
                {
                    CurrentReadSet = facts.CurrentReadSet with { OwnerObservations = null },
                    CarryObservations = [],
                    OriginalExtractionTicket = facts.OriginalExtractionTicket with
                    {
                        OwnerObservations = null,
                    },
                    Targets = facts
                        .Targets.Select(target => target with { OwnerObservations = [] })
                        .ToArray(),
                    CandidatePools = [],
                    SourceReviews = facts.SourceReviews with
                    {
                        Reviews = [],
                        Correspondences = null,
                    },
                },
            };
            return new(
                stripped,
                owners,
                ownerLists,
                actorBlocks,
                pools,
                candidateFacts,
                poolRefs,
                readOwners,
                carryOwners,
                extractionOwners,
                targetOwners,
                contributionOwners,
                scopes.Values,
                reviews.Values,
                fieldBodies,
                fields.Values,
                localMaps.Values,
                evidenceMaps.Values,
                reviewTargets.Values,
                correspondences.Values,
                reviewRefs,
                correspondenceRefs
            );
        }

        private int Review(SourceActorAliasReviewState review)
        {
            if (reviewReferences.TryGetValue(review, out var existing))
            {
                return existing;
            }
            var result = reviews.Add(
                new(review with { OriginalScope = null! }, scopes.Add(review.OriginalScope))
            );
            reviewReferences.Add(review, result);
            return result;
        }

        private int Field(SourceActorReviewField field)
        {
            if (fieldReferences.TryGetValue(field, out var existing))
            {
                return existing;
            }
            // Coordinates vary per assertion; the complete field and containing scope do not.
            // Typed body equality avoids serializing the full field text once per assertion.
            var body = new StoredFieldBody(
                field.ExtractionAttemptUuid,
                scopes.Add(field.Scope),
                field.FieldText,
                field.FieldEvidence
            );
            if (!fieldBodyIndex.TryGetValue(body, out var bodyRef))
            {
                bodyRef = fieldBodies.Count;
                fieldBodies.Add(body);
                fieldBodyIndex.Add(body, bodyRef);
            }
            var result = fields.Add(new(field.Coordinates, bodyRef));
            fieldReferences.Add(field, result);
            return result;
        }

        private int ReviewTarget(SourceActorReviewTarget target)
        {
            if (targetReferences.TryGetValue(target, out var existing))
            {
                return existing;
            }
            var result = reviewTargets.Add(
                new(target with { OwnerObservations = [] }, Owners(target.OwnerObservations))
            );
            targetReferences.Add(target, result);
            return result;
        }

        private int Correspondence(ReviewedSourceCorrespondence proof) =>
            correspondences.Add(
                new(
                    proof.Version,
                    Review(proof.OriginalReview),
                    Field(proof.Original),
                    Field(proof.Current),
                    localMaps.Add(proof.LocalIds),
                    evidenceMaps.Add(proof.Evidence),
                    ReviewTarget(proof.Target)
                )
            );

        private int? OptionalOwners(IReadOnlyList<NormalizationSubjectObservation>? values) =>
            values == null ? null : Owners(values);

        private int Owners(IReadOnlyList<NormalizationSubjectObservation> values)
        {
            var refs = values.Select(value => Intern(value, owners, ownerIndex)).ToArray();
            return Intern<IReadOnlyList<int>>(refs, ownerLists, listIndex);
        }

        private int Pool(NormalizationCandidateFactsSnapshot<PersonCandidateFacts> pool)
        {
            var observation = Owners(pool.ReadSet.OwnerObservations);
            var factRefs = pool
                .Facts.Select(fact =>
                {
                    var actor =
                        fact.ActorEvidence == null
                            ? (int?)null
                            : Intern(fact.ActorEvidence, actorBlocks, actorIndex);
                    // Query-specific retrieval scores/reasons belong to the pool. The complete
                    // matching facts and their original evidence can be shared across queries.
                    var value = fact with
                    {
                        Hit = fact.Hit with { NameSimilarity = 0, Reasons = [] },
                        ActorEvidence = null,
                    };
                    return Intern(new StoredCandidateFact(value, actor), candidateFacts, factIndex);
                })
                .ToArray();
            var stored = new StoredPool(
                pool with
                {
                    ReadSet = pool.ReadSet with { OwnerObservations = [] },
                    Facts = [],
                },
                observation,
                factRefs,
                pool.Facts.Select(fact => fact.Hit).ToArray()
            );
            return Intern(stored, pools, poolIndex);
        }

        private static int Intern<T>(T value, List<T> values, Dictionary<string, int> index)
        {
            var key = JsonSerializer.Serialize(value);
            if (index.TryGetValue(key, out var existing))
            {
                return existing;
            }
            var result = values.Count;
            values.Add(value);
            index.Add(key, result);
            return result;
        }
    }

    private sealed class Decoder(StoredStage stored)
    {
        private readonly Dictionary<int, SourceActorAliasReviewState> reviews = [];
        private readonly Dictionary<int, SourceActorReviewField> fields = [];
        private readonly Dictionary<int, SourceActorReviewTarget> targets = [];

        internal SourceActorAliasPersonStage Decode()
        {
            var stage = stored.Value;
            var facts = stage.Facts;
            if (
                stored.TargetOwners.Count != facts.Targets.Count
                || stored.ContributionOwners.Count != stage.Contributions.Count
            )
            {
                throw new JsonException(
                    "Source alias owner block shape differs from the saved stage."
                );
            }
            return stage with
            {
                Contributions = stage
                    .Contributions.Select(
                        (value, index) =>
                            value with
                            {
                                ExpectedOwnerObservations = OptionalOwners(
                                    stored.ContributionOwners[index]
                                ),
                            }
                    )
                    .ToArray(),
                Facts = facts with
                {
                    CurrentReadSet = facts.CurrentReadSet with
                    {
                        OwnerObservations = OptionalOwners(stored.ReadOwners),
                    },
                    CarryObservations = Owners(stored.CarryOwners),
                    OriginalExtractionTicket = facts.OriginalExtractionTicket with
                    {
                        OwnerObservations = OptionalOwners(stored.ExtractionOwners),
                    },
                    Targets = facts
                        .Targets.Select(
                            (target, index) =>
                                target with
                                {
                                    OwnerObservations = Owners(stored.TargetOwners[index]),
                                }
                        )
                        .ToArray(),
                    CandidatePools = stored.PoolRefs.Select(Pool).ToArray(),
                    SourceReviews = facts.SourceReviews with
                    {
                        Reviews = stored.ReviewRefs.Select(Review).ToArray(),
                        Correspondences = stored
                            .CorrespondenceRefs?.Select(Correspondence)
                            .ToArray(),
                    },
                },
            };
        }

        private SourceActorAliasReviewState Review(int index)
        {
            if (!reviews.TryGetValue(index, out var result))
            {
                var row = At(stored.Reviews, index);
                result = row.Value with { OriginalScope = At(stored.OriginalScopes, row.Scope) };
                reviews.Add(index, result);
            }
            return result;
        }

        private SourceActorReviewField Field(int index)
        {
            if (!fields.TryGetValue(index, out var result))
            {
                var field = At(stored.Fields, index);
                var body = At(stored.FieldBodies, field.Body);
                result = new(
                    body.ExtractionAttemptUuid,
                    field.Coordinates,
                    At(stored.OriginalScopes, body.Scope),
                    body.Text,
                    body.Evidence
                );
                fields.Add(index, result);
            }
            return result;
        }

        private SourceActorReviewTarget ReviewTarget(int index)
        {
            if (!targets.TryGetValue(index, out var result))
            {
                var target = At(stored.ReviewTargets, index);
                result = target.Value with { OwnerObservations = Owners(target.Owners) };
                targets.Add(index, result);
            }
            return result;
        }

        private ReviewedSourceCorrespondence Correspondence(int index)
        {
            var proof = At(stored.Correspondences, index);
            return new(
                proof.Version,
                Review(proof.OriginalReview),
                Field(proof.OriginalField),
                Field(proof.CurrentField),
                At(stored.LocalMaps, proof.LocalIds),
                At(stored.EvidenceMaps, proof.Evidence),
                ReviewTarget(proof.Target)
            );
        }

        private IReadOnlyList<NormalizationSubjectObservation>? OptionalOwners(int? index) =>
            index.HasValue ? Owners(index.Value) : null;

        private IReadOnlyList<NormalizationSubjectObservation> Owners(int index) =>
            At(stored.OwnerLists, index).Select(value => At(stored.Owners, value)).ToArray();

        private NormalizationCandidateFactsSnapshot<PersonCandidateFacts> Pool(int index)
        {
            var pool = At(stored.Pools, index);
            if (pool.FactRefs.Count != pool.Hits.Count)
            {
                throw new JsonException(
                    "Source alias candidate block shape differs from saved retrieval hits."
                );
            }
            return pool.Value with
            {
                ReadSet = pool.Value.ReadSet with { OwnerObservations = Owners(pool.Owners) },
                Facts = pool
                    .FactRefs.Select(
                        (reference, ordinal) =>
                        {
                            var fact = At(stored.CandidateFacts, reference);
                            return fact.Value with
                            {
                                Hit = pool.Hits[ordinal],
                                ActorEvidence = fact.ActorEvidence is { } actor
                                    ? At(stored.ActorBlocks, actor)
                                    : null,
                            };
                        }
                    )
                    .ToArray(),
            };
        }

        private static T At<T>(IReadOnlyList<T> values, int index) =>
            index >= 0 && index < values.Count
                ? values[index]
                : throw new JsonException(
                    "Source alias checkpoint contains an invalid local block reference."
                );
    }

    /// <summary>
    /// Immutable proof objects commonly recur by reference. Equal independent clones also
    /// share storage; a digest narrows lookup, while exact encoded bytes prevent a collision
    /// from coalescing different evidence. Only unique blocks retain their encoded bytes.
    /// </summary>
    private sealed class BlockTable<T>
        where T : class
    {
        private readonly List<T> values = [];
        private readonly List<byte[]> encoded = [];
        private readonly Dictionary<string, List<int>> content = new(StringComparer.Ordinal);
        private readonly Dictionary<object, int> references = new(
            ReferenceEqualityComparer.Instance
        );
        internal IReadOnlyList<T> Values => values;

        internal int Add(T value)
        {
            if (references.TryGetValue(value, out var existing))
            {
                return existing;
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!content.TryGetValue(hash, out var matches))
            {
                matches = [];
                content.Add(hash, matches);
            }
            foreach (var match in matches)
            {
                if (bytes.AsSpan().SequenceEqual(encoded[match]))
                {
                    references.Add(value, match);
                    return match;
                }
            }
            var result = values.Count;
            values.Add(value);
            encoded.Add(bytes);
            matches.Add(result);
            references.Add(value, result);
            return result;
        }
    }

    private sealed class BoundedStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            RequireCapacity(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            RequireCapacity(buffer.Length);
            base.Write(buffer);
        }

        private void RequireCapacity(int added)
        {
            if (Length + added > MaximumCheckpointBytes)
            {
                throw new ArgumentException(
                    "Source alias checkpoint exceeds 32 MiB; no proof, map or observation was truncated."
                );
            }
        }
    }
}
