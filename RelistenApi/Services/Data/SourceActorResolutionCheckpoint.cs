using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

/// <summary>
/// Stores repeated source scopes, correspondence maps and complete candidate facts once per
/// immutable checkpoint. Local references restore the original domain values without reading
/// current state, changing evidence hashes or replacing original observations. V1 history
/// remains readable; new V2 storage does not change matching or Work admission.
/// </summary>
internal static partial class SourceActorResolutionCheckpoint
{
    internal const string FormatVersion = "source-actor-resolution-checkpoint-v2";
    internal const string LegacyFormatVersion = "source-actor-resolution-checkpoint-v1";
    internal static string[] SupportedFormats => [LegacyFormatVersion, FormatVersion];
    internal const string PreparationKind = "resolution_preparation";
    internal const string BatchKind = "resolution_batch";
    internal const int MaximumCheckpointBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        RespectRequiredConstructorParameters = true,
        Converters = { new ReadOnlyGuidSetJsonConverter() },
    };

    // V1 rows remain immutable. New writes share complete candidate bodies; equality
    // compares logical encoder values without imposing a new write budget on old history.
    internal static string WritePreparation(SourceActorResolutionPreparation value) =>
        SerializeBounded(EncodePreparation(value, enforceCapacity: true));

    internal static string WriteBatch(SourceActorResolutionBatch value) =>
        SerializeBounded(EncodeBatch(value, enforceCapacity: true));

    internal static SourceActorResolutionPreparation ReadPreparation(string json)
    {
        using var document = ReadDocument(json, PreparationKind);
        var root = document.RootElement;
        if (root.GetProperty("Format").GetString() == LegacyFormatVersion)
        {
            var legacy = ReadLegacy<StoredPreparationV1>(root, PreparationKind);
            var decoder = new Decoder(legacy.Scopes, legacy.CorrespondencePairs);
            var value = decoder.Preparation(legacy.Value);
            decoder.RequireAllReferenced();
            return value;
        }

        var envelope = ReadCurrent<StoredPreparationV2>(root, PreparationKind);
        var current = new Decoder(
            envelope.Scopes,
            envelope.CorrespondencePairs,
            new CandidateDecoder(envelope.CandidateFacts, envelope.OwnerBlocks)
        );
        var result = current.Preparation(envelope.Value);
        current.RequireAllReferenced();
        RequireCanonical(root, EncodePreparation(result));
        return result;
    }

    internal static SourceActorResolutionBatch ReadBatch(string json)
    {
        using var document = ReadDocument(json, BatchKind);
        var root = document.RootElement;
        if (root.GetProperty("Format").GetString() == LegacyFormatVersion)
        {
            var legacy = ReadLegacy<StoredBatchV1>(root, BatchKind);
            var decoder = new Decoder(legacy.Scopes, legacy.CorrespondencePairs);
            var value = decoder.Batch(legacy.Value);
            decoder.RequireAllReferenced();
            return value;
        }

        var envelope = ReadCurrent<StoredBatchV2>(root, BatchKind);
        var current = new Decoder(
            envelope.Scopes,
            envelope.CorrespondencePairs,
            new CandidateDecoder(envelope.CandidateFacts, envelope.OwnerBlocks)
        );
        var result = current.Batch(envelope.Value);
        current.RequireAllReferenced();
        RequireCanonical(root, EncodeBatch(result));
        return result;
    }

    internal static bool EquivalentPreparation(
        SourceActorResolutionPreparation? left,
        SourceActorResolutionPreparation? right
    )
    {
        if (left == null || right == null)
        {
            return left == right;
        }
        return Same(EncodePreparation(left), EncodePreparation(right));
    }

    internal static bool EquivalentBatch(
        SourceActorResolutionBatch? left,
        SourceActorResolutionBatch? right
    )
    {
        if (left == null || right == null)
        {
            return left == right;
        }
        return Same(EncodeBatch(left), EncodeBatch(right));
    }

    private static EnvelopeV2<StoredPreparationV2> EncodePreparation(
        SourceActorResolutionPreparation value,
        bool enforceCapacity = false
    )
    {
        var encoder = new Encoder(enforceCapacity);
        var stored = encoder.Preparation(value);
        return new(
            FormatVersion,
            PreparationKind,
            stored,
            encoder.Scopes,
            encoder.CorrespondencePairs,
            encoder.Candidates.Facts,
            encoder.Candidates.OwnerBlocks
        );
    }

    private static EnvelopeV2<StoredBatchV2> EncodeBatch(
        SourceActorResolutionBatch value,
        bool enforceCapacity = false
    )
    {
        var encoder = new Encoder(enforceCapacity);
        var stored = encoder.Batch(value);
        return new(
            FormatVersion,
            BatchKind,
            stored,
            encoder.Scopes,
            encoder.CorrespondencePairs,
            encoder.Candidates.Facts,
            encoder.Candidates.OwnerBlocks
        );
    }

    /// <summary>
    /// Compares the complete ordered review rows without expanding their repeated original
    /// scopes. Owner clocks and derived correspondences are fenced separately by publication.
    /// </summary>
    internal static bool EquivalentReviewRows(
        IReadOnlyList<SourceActorAliasReviewState> left,
        IReadOnlyList<SourceActorAliasReviewState> right
    )
    {
        var leftEncoder = new Encoder();
        var leftValue = leftEncoder.Reviews(new(null, left));
        var rightEncoder = new Encoder();
        var rightValue = rightEncoder.Reviews(new(null, right));
        return Same(
            new EnvelopeV1<StoredReviews>(
                LegacyFormatVersion,
                "review_rows_equality",
                leftValue,
                leftEncoder.Scopes,
                leftEncoder.CorrespondencePairs
            ),
            new EnvelopeV1<StoredReviews>(
                LegacyFormatVersion,
                "review_rows_equality",
                rightValue,
                rightEncoder.Scopes,
                rightEncoder.CorrespondencePairs
            )
        );
    }

    // Canonical first-encounter scope tables make equality independent of persisted table
    // indexes. Same and the source hashes retain the default typed JSON/text conventions.
    internal static bool Same<T>(T left, T right) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(left),
            JsonSerializer.SerializeToElement(right)
        );

    internal static string TextHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    internal static string AssertionHash(
        SourceActorAliasAssertion<NormalizedEvidenceReference> assertion
    ) =>
        Convert
            .ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(assertion)))
            .ToLowerInvariant();

    private static readonly JsonSerializerOptions CurrentReadOptions = new(ReadOptions)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static JsonDocument ReadDocument(string json, string kind)
    {
        var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (
            root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject()
                .Select(property => property.Name)
                .Distinct(StringComparer.Ordinal)
                .Count() != root.EnumerateObject().Count()
            || !root.TryGetProperty("Format", out var format)
            || format.ValueKind != JsonValueKind.String
            || !SupportedFormats.Contains(format.GetString(), StringComparer.Ordinal)
            || !root.TryGetProperty("Kind", out var storedKind)
            || storedKind.ValueKind != JsonValueKind.String
            || storedKind.GetString() != kind
        )
        {
            document.Dispose();
            throw Invalid("Unsupported source actor checkpoint format, kind or payload.");
        }
        return document;
    }

    private static EnvelopeV1<T> ReadLegacy<T>(JsonElement root, string kind)
    {
        var envelope =
            root.Deserialize<EnvelopeV1<T>>(ReadOptions)
            ?? throw Invalid("Missing source actor checkpoint envelope.");
        if (
            envelope.Format != LegacyFormatVersion
            || envelope.Kind != kind
            || envelope.Value is null
            || envelope.Scopes == null
            || envelope.CorrespondencePairs == null
        )
        {
            throw Invalid("Unsupported source actor checkpoint format, kind or payload.");
        }
        // Retain the original format's budget; a larger V2 framing must not invalidate V1 history.
        SerializeBounded(envelope);
        return envelope;
    }

    private static EnvelopeV2<T> ReadCurrent<T>(JsonElement root, string kind)
    {
        RequireUniqueProperties(root);
        var envelope =
            root.Deserialize<EnvelopeV2<T>>(CurrentReadOptions)
            ?? throw Invalid("Missing source actor checkpoint envelope.");
        if (
            envelope.Format != FormatVersion
            || envelope.Kind != kind
            || envelope.Value is null
            || envelope.Scopes == null
            || envelope.CorrespondencePairs == null
            || envelope.CandidateFacts == null
            || envelope.OwnerBlocks == null
        )
        {
            throw Invalid("Unsupported source actor checkpoint format, kind or payload.");
        }
        var preparation = envelope.Value switch
        {
            StoredPreparationV2 value => value,
            StoredBatchV2 batch when batch.Preparation != null => batch.Preparation,
            _ => throw Invalid("Missing source actor candidate preparation."),
        };
        if (preparation.CandidatePools == null)
        {
            throw Invalid("Missing source actor candidate pools.");
        }
        foreach (var pool in preparation.CandidatePools)
        {
            CandidateDecoder.RequirePoolShape(pool);
        }
        // JSONB whitespace and escaping do not enlarge the canonical encoded write budget.
        SerializeBounded(envelope);
        return envelope;
    }

    private static void RequireUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw Invalid("Duplicate source actor checkpoint property.");
                }
                RequireUniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                RequireUniqueProperties(item);
            }
        }
    }

    private static void RequireCanonical<T>(JsonElement original, EnvelopeV2<T> canonical)
    {
        if (!JsonElement.DeepEquals(original, JsonSerializer.SerializeToElement(canonical)))
        {
            throw Invalid(
                "Source actor checkpoint has hidden, unused or noncanonical proof blocks."
            );
        }
    }

    private static string SerializeBounded<T>(T value)
    {
        using var stream = new CheckpointStream();
        JsonSerializer.Serialize(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static JsonException Invalid(string message) => new(message);

    private sealed class CheckpointStream : MemoryStream
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

        private void RequireCapacity(int count)
        {
            if (Length + count > MaximumCheckpointBytes)
            {
                throw new SourceActorResolutionCapacityException("checkpoint_capacity_exceeded");
            }
        }
    }

    // Stored contracts are private to this format. Inputs keep their original contributor
    // and revision fields at the same named paths used by configured-membership recovery.
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record EnvelopeV1<T>(
        string Format,
        string Kind,
        T Value,
        IReadOnlyList<NormalizationRecordingSourceActorAliasScope> Scopes,
        IReadOnlyList<StoredCorrespondencePair> CorrespondencePairs
    );

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record EnvelopeV2<T>(
        string Format,
        string Kind,
        T Value,
        IReadOnlyList<NormalizationRecordingSourceActorAliasScope> Scopes,
        IReadOnlyList<StoredCorrespondencePair> CorrespondencePairs,
        IReadOnlyList<PersonCandidateFacts> CandidateFacts,
        IReadOnlyList<IReadOnlyList<NormalizationSubjectObservation>> OwnerBlocks
    );

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StoredScope(
        int OriginalScopeRef,
        IReadOnlyList<SourceActorResolutionField> Fields,
        IReadOnlyList<SourceActorResolutionClaim> Claims
    );

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StoredInput(
        NormalizationRecordingPreparedInput OriginalInput,
        long ExpectedContributionRevision,
        bool AliasPayloadPresent,
        IReadOnlyList<StoredScope> Scopes,
        IReadOnlyList<ProviderDecodeIssue> DecodeIssues
    );

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StoredReview(
        Guid Uuid,
        SourceActorAliasCoordinates Coordinates,
        string AssertionSha256,
        SourceActorAliasAssertion<NormalizedEvidenceReference> OriginalAssertion,
        int OriginalScopeRef,
        Guid SourceResolutionOwnerUuid,
        long ReviewRevision,
        string State,
        Guid? SelectedPersonUuid,
        Guid LastOperationUuid
    );

    // A field's common identity has no selected assertion/reference UUID. Those belong
    // to individual reviews, while the complete bijection and raw field are shared.
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StoredField(
        Guid ExtractionAttemptUuid,
        Guid SourceUuid,
        NormalizationResourceKey Contributor,
        Guid ResourceUuid,
        Guid InputRevisionUuid,
        string FieldJsonPointer,
        string ProducerVersion,
        string FieldSha256,
        int ScopeRef,
        string FieldText,
        NormalizedEvidenceReference FieldEvidence
    );

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StoredCorrespondencePair(
        StoredField Original,
        StoredField Current,
        IReadOnlyList<SourceActorReviewLocalIdMap> LocalIds,
        IReadOnlyList<SourceActorReviewEvidenceMap> Evidence
    );

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StoredCorrespondence(
        string Version,
        StoredReview OriginalReview,
        int PairRef,
        SourceActorAliasCoordinates CurrentCoordinates,
        SourceActorReviewTarget Target
    );

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StoredReviews(
        NormalizationSubjectObservation? OwnerObservation,
        IReadOnlyList<StoredReview> Reviews,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            IReadOnlyList<StoredCorrespondence>? Correspondences = null
    );

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StoredPreparationV1(
        NormalizationSubjectObservation ExtractionObservation,
        NormalizationWorkTicket ExtractionTicket,
        PeopleAcceptancePolicyState PolicyState,
        NormalizationCandidateUniverseDependency PeopleUniverse,
        StoredReviews SourceReviews,
        long? ExpectedConfigurationGeneration,
        IReadOnlyList<NormalizationInputContributionRevision> ExpectedContributions,
        IReadOnlyList<StoredInput> Inputs,
        IReadOnlyList<NormalizationCandidateFactsSnapshot<PersonCandidateFacts>> CandidatePools
    );

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StoredBatchV1(
        Guid AttemptUuid,
        Guid SourceUuid,
        Guid ExtractionAttemptUuid,
        string ProcessingVersion,
        StoredPreparationV1 Preparation,
        NormalizationWorkTicket Ticket
    );

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StoredPreparationV2(
        NormalizationSubjectObservation ExtractionObservation,
        NormalizationWorkTicket ExtractionTicket,
        PeopleAcceptancePolicyState PolicyState,
        NormalizationCandidateUniverseDependency PeopleUniverse,
        StoredReviews SourceReviews,
        long? ExpectedConfigurationGeneration,
        IReadOnlyList<NormalizationInputContributionRevision> ExpectedContributions,
        IReadOnlyList<StoredInput> Inputs,
        IReadOnlyList<StoredPool> CandidatePools
    );

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StoredBatchV2(
        Guid AttemptUuid,
        Guid SourceUuid,
        Guid ExtractionAttemptUuid,
        string ProcessingVersion,
        StoredPreparationV2 Preparation,
        NormalizationWorkTicket Ticket
    );

    private sealed class Encoder(bool enforceCapacity = false)
    {
        private readonly Dictionary<string, List<int>> scopeHashes = new(StringComparer.Ordinal);
        private readonly Dictionary<
            NormalizationRecordingSourceActorAliasScope,
            int
        > scopeReferences = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, List<int>> pairHashes = new(StringComparer.Ordinal);
        private readonly Dictionary<StoredCorrespondencePair, int> pairReferences = new(
            new PairReferenceComparer()
        );
        public List<NormalizationRecordingSourceActorAliasScope> Scopes { get; } = [];
        public List<StoredCorrespondencePair> CorrespondencePairs { get; } = [];
        public CandidateEncoder Candidates { get; } = new(enforceCapacity);

        public StoredPreparationV2 Preparation(SourceActorResolutionPreparation value) =>
            new(
                value.ExtractionObservation,
                value.ExtractionTicket,
                value.PolicyState,
                value.PeopleUniverse,
                Reviews(value.SourceReviews),
                value.ExpectedConfigurationGeneration,
                value.ExpectedContributions,
                value.Inputs.Select(Input).ToArray(),
                value.CandidatePools.Select(Candidates.Pool).ToArray()
            );

        public StoredBatchV2 Batch(SourceActorResolutionBatch value) =>
            new(
                value.AttemptUuid,
                value.SourceUuid,
                value.ExtractionAttemptUuid,
                value.ProcessingVersion,
                Preparation(value.Preparation),
                value.Ticket
            );

        private StoredInput Input(SourceActorResolutionInput value) =>
            new(
                value.OriginalInput,
                value.ExpectedContributionRevision,
                value.AliasPayloadPresent,
                value
                    .Scopes.Select(scope => new StoredScope(
                        Scope(scope.OriginalScope),
                        scope.Fields,
                        scope.Claims
                    ))
                    .ToArray(),
                value.DecodeIssues
            );

        public StoredReviews Reviews(SourceActorResolutionReviewSnapshot value) =>
            new(
                value.OwnerObservation,
                value.Reviews.Select(Review).ToArray(),
                value.Correspondences?.Select(Correspondence).ToArray()
            );

        private StoredReview Review(SourceActorAliasReviewState value) =>
            new(
                value.Uuid,
                value.Coordinates,
                value.AssertionSha256,
                value.OriginalAssertion,
                Scope(value.OriginalScope),
                value.SourceResolutionOwnerUuid,
                value.ReviewRevision,
                value.State,
                value.SelectedPersonUuid,
                value.LastOperationUuid
            );

        private StoredField Field(SourceActorReviewField value)
        {
            var coordinates = value.Coordinates;
            return new(
                value.ExtractionAttemptUuid,
                coordinates.SourceUuid,
                coordinates.Contributor,
                coordinates.ResourceUuid,
                coordinates.InputRevisionUuid,
                coordinates.FieldJsonPointer,
                coordinates.ProducerVersion,
                coordinates.FieldSha256,
                Scope(value.Scope),
                value.FieldText,
                value.FieldEvidence
            );
        }

        private StoredCorrespondence Correspondence(ReviewedSourceCorrespondence value)
        {
            if (!Same(value.Original.Coordinates, value.OriginalReview.Coordinates))
            {
                throw Invalid("Correspondence original coordinates differ from their review.");
            }
            var review = Review(value.OriginalReview);
            var pair = new StoredCorrespondencePair(
                Field(value.Original),
                Field(value.Current),
                value.LocalIds,
                value.Evidence
            );
            return new(value.Version, review, Pair(pair), value.Current.Coordinates, value.Target);
        }

        private int Pair(StoredCorrespondencePair value)
        {
            // Record equality checks every common field and the exact shared list objects.
            // This avoids hashing the whole field map again for each assertion in that field.
            if (pairReferences.TryGetValue(value, out var existing))
            {
                return existing;
            }
            var hash = Convert.ToHexString(
                SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value))
            );
            if (!pairHashes.TryGetValue(hash, out var indexes))
            {
                indexes = [];
                pairHashes.Add(hash, indexes);
            }
            foreach (var index in indexes)
            {
                if (Same(CorrespondencePairs[index], value))
                {
                    pairReferences.Add(value, index);
                    return index;
                }
            }
            var added = CorrespondencePairs.Count;
            CorrespondencePairs.Add(value);
            indexes.Add(added);
            pairReferences.Add(value, added);
            return added;
        }

        private int Scope(NormalizationRecordingSourceActorAliasScope value)
        {
            if (scopeReferences.TryGetValue(value, out var existing))
            {
                return existing;
            }
            var hash = Convert.ToHexString(
                SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value))
            );
            if (!scopeHashes.TryGetValue(hash, out var indexes))
            {
                indexes = [];
                scopeHashes.Add(hash, indexes);
            }
            foreach (var index in indexes)
            {
                // Hashes only narrow the search. Evidence is shared only after exact DTO equality.
                if (Same(Scopes[index], value))
                {
                    scopeReferences.Add(value, index);
                    return index;
                }
            }
            var added = Scopes.Count;
            Scopes.Add(value);
            indexes.Add(added);
            scopeReferences.Add(value, added);
            return added;
        }
    }

    // Shared mapping lists are immutable domain results. Reference identity is a fast path
    // only when both complete fields also match; independent equal lists use exact interning.
    private sealed class PairReferenceComparer : IEqualityComparer<StoredCorrespondencePair>
    {
        public bool Equals(StoredCorrespondencePair? left, StoredCorrespondencePair? right) =>
            ReferenceEquals(left, right)
            || (
                left != null
                && right != null
                && left.Original == right.Original
                && left.Current == right.Current
                && ReferenceEquals(left.LocalIds, right.LocalIds)
                && ReferenceEquals(left.Evidence, right.Evidence)
            );

        public int GetHashCode(StoredCorrespondencePair value) =>
            HashCode.Combine(
                value.Original,
                value.Current,
                RuntimeHelpers.GetHashCode(value.LocalIds),
                RuntimeHelpers.GetHashCode(value.Evidence)
            );
    }

    private sealed class Decoder(
        IReadOnlyList<NormalizationRecordingSourceActorAliasScope> scopes,
        IReadOnlyList<StoredCorrespondencePair> pairs,
        CandidateDecoder? candidates = null
    )
    {
        private readonly bool[] usedScopes = new bool[scopes.Count];
        private readonly bool[] usedPairs = new bool[pairs.Count];

        public SourceActorResolutionPreparation Preparation(StoredPreparationV1 value) =>
            new(
                value.ExtractionObservation,
                value.ExtractionTicket,
                value.PolicyState,
                value.PeopleUniverse,
                Reviews(value.SourceReviews),
                value.ExpectedConfigurationGeneration,
                value.ExpectedContributions,
                value.Inputs.Select(Input).ToArray(),
                value.CandidatePools
            );

        public SourceActorResolutionBatch Batch(StoredBatchV1 value) =>
            new(
                value.AttemptUuid,
                value.SourceUuid,
                value.ExtractionAttemptUuid,
                value.ProcessingVersion,
                Preparation(value.Preparation),
                value.Ticket
            );

        public SourceActorResolutionPreparation Preparation(StoredPreparationV2 value) =>
            new(
                value.ExtractionObservation,
                value.ExtractionTicket,
                value.PolicyState,
                value.PeopleUniverse,
                Reviews(value.SourceReviews),
                value.ExpectedConfigurationGeneration,
                value.ExpectedContributions,
                value.Inputs.Select(Input).ToArray(),
                value.CandidatePools.Select(candidates!.Pool).ToArray()
            );

        public SourceActorResolutionBatch Batch(StoredBatchV2 value) =>
            new(
                value.AttemptUuid,
                value.SourceUuid,
                value.ExtractionAttemptUuid,
                value.ProcessingVersion,
                Preparation(value.Preparation),
                value.Ticket
            );

        public void RequireAllReferenced()
        {
            candidates?.RequireAllReferenced();
            if (usedScopes.Any(used => !used) || usedPairs.Any(used => !used))
            {
                throw Invalid("Unreferenced source actor checkpoint scope or correspondence pair.");
            }
        }

        private SourceActorResolutionInput Input(StoredInput value) =>
            new(
                value.OriginalInput,
                value.ExpectedContributionRevision,
                value.AliasPayloadPresent,
                value
                    .Scopes.Select(scope => new SourceActorResolutionScope(
                        Scope(scope.OriginalScopeRef),
                        scope.Fields,
                        scope.Claims
                    ))
                    .ToArray(),
                value.DecodeIssues
            );

        private SourceActorResolutionReviewSnapshot Reviews(StoredReviews value) =>
            new(
                value.OwnerObservation,
                value.Reviews.Select(Review).ToArray(),
                value.Correspondences?.Select(Correspondence).ToArray()
            );

        private SourceActorAliasReviewState Review(StoredReview value) =>
            new(
                value.Uuid,
                value.Coordinates,
                value.AssertionSha256,
                value.OriginalAssertion,
                Scope(value.OriginalScopeRef),
                value.SourceResolutionOwnerUuid,
                value.ReviewRevision,
                value.State,
                value.SelectedPersonUuid,
                value.LastOperationUuid
            );

        private SourceActorReviewField Field(
            StoredField value,
            SourceActorAliasCoordinates coordinates
        )
        {
            if (
                value.SourceUuid != coordinates.SourceUuid
                || value.Contributor != coordinates.Contributor
                || value.ResourceUuid != coordinates.ResourceUuid
                || value.InputRevisionUuid != coordinates.InputRevisionUuid
                || value.FieldJsonPointer != coordinates.FieldJsonPointer
                || value.ProducerVersion != coordinates.ProducerVersion
                || value.FieldSha256 != coordinates.FieldSha256
            )
            {
                throw Invalid("Correspondence selected coordinates differ from their field pair.");
            }
            return new(
                value.ExtractionAttemptUuid,
                coordinates,
                Scope(value.ScopeRef),
                value.FieldText,
                value.FieldEvidence
            );
        }

        private ReviewedSourceCorrespondence Correspondence(StoredCorrespondence value)
        {
            if ((uint)value.PairRef >= (uint)pairs.Count || pairs[value.PairRef] == null)
            {
                throw Invalid("Invalid source actor checkpoint correspondence pair reference.");
            }
            usedPairs[value.PairRef] = true;
            var pair = pairs[value.PairRef];
            if (
                pair.Original == null
                || pair.Current == null
                || pair.LocalIds == null
                || pair.Evidence == null
            )
            {
                throw Invalid("Missing source actor checkpoint correspondence field or map.");
            }
            var review = Review(value.OriginalReview);
            return new(
                value.Version,
                review,
                Field(pair.Original, review.Coordinates),
                Field(pair.Current, value.CurrentCoordinates),
                pair.LocalIds,
                pair.Evidence,
                value.Target
            );
        }

        private NormalizationRecordingSourceActorAliasScope Scope(int index)
        {
            if ((uint)index >= (uint)scopes.Count || scopes[index] == null)
            {
                throw Invalid("Invalid source actor checkpoint scope reference.");
            }
            usedScopes[index] = true;
            return scopes[index];
        }
    }
}

internal sealed class SourceActorResolutionCapacityException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}
