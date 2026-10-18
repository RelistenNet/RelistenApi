using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Stores repeated people evidence once per immutable checkpoint. Domain services receive the
/// original expanded DTOs; this format changes neither their hashes nor their publication clocks.
/// Legacy JSON remains readable. A compact checkpoint requires this reader before rollback.
/// </summary>
internal static partial class PeopleCheckpointCodec
{
    internal const string FormatVersion = "people-checkpoint-v2";
    internal const int MaximumCheckpointBytes = 32 * 1024 * 1024;
    internal const string PreparationKind = "resolution_preparation";
    internal const string BatchKind = "resolution_batch";
    internal const string PlanKind = "acceptance_plan";
    internal const string StageKind = "acceptance_stage";
    private static readonly JsonSerializerOptions StoredReadOptions = new()
    {
        RespectRequiredConstructorParameters = true,
    };

    internal static string WritePreparation(NormalizationPeopleResolutionPreparation value) =>
        Write(PreparationKind, value, static (encoder, item) => encoder.Preparation(item));

    internal static string WriteBatch(NormalizationPeopleResolutionBatch value) =>
        Write(BatchKind, value, static (encoder, item) => encoder.Batch(item));

    internal static string WritePlan(PeopleAcceptancePlan value) =>
        Write(PlanKind, value, static (encoder, item) => encoder.Plan(item));

    internal static string WriteStage(PeopleAcceptanceStage value) =>
        Write(StageKind, value, static (encoder, item) => encoder.Stage(item));

    internal static NormalizationPeopleResolutionPreparation ReadPreparation(string json) =>
        Read<NormalizationPeopleResolutionPreparation, StoredPreparation>(
            json,
            PreparationKind,
            static (decoder, item) => decoder.Preparation(item)
        );

    internal static NormalizationPeopleResolutionBatch ReadBatch(string json) =>
        Read<NormalizationPeopleResolutionBatch, StoredBatch>(
            json,
            BatchKind,
            static (decoder, item) => decoder.Batch(item)
        );

    internal static PeopleAcceptancePlan ReadPlan(string json) =>
        Read<PeopleAcceptancePlan, StoredPlan>(
            json,
            PlanKind,
            static (decoder, item) => decoder.Plan(item)
        );

    internal static PeopleAcceptanceStage ReadStage(string json) =>
        Read<PeopleAcceptanceStage, StoredStage>(
            json,
            StageKind,
            static (decoder, item) => decoder.Stage(item)
        );

    internal static bool EquivalentPreparation(
        NormalizationPeopleResolutionPreparation? left,
        NormalizationPeopleResolutionPreparation? right
    ) => Equivalent(left, right, static (encoder, value) => encoder.Preparation(value));

    internal static bool EquivalentBatch(
        NormalizationPeopleResolutionBatch? left,
        NormalizationPeopleResolutionBatch? right
    ) => Equivalent(left, right, static (encoder, value) => encoder.Batch(value));

    internal static bool EquivalentPlan(PeopleAcceptancePlan? left, PeopleAcceptancePlan? right) =>
        Equivalent(left, right, static (encoder, value) => encoder.Plan(value));

    internal static bool EquivalentStage(
        PeopleAcceptanceStage? left,
        PeopleAcceptanceStage? right
    ) => Equivalent(left, right, static (encoder, value) => encoder.Stage(value));

    internal static bool EquivalentClaim(
        NormalizationPeopleResolutionClaim? left,
        NormalizationPeopleResolutionClaim? right
    ) => Equivalent(left, right, static (encoder, value) => encoder.Claim(value));

    internal static bool EquivalentApplication(
        PeopleAcceptanceApplication left,
        PeopleAcceptanceApplication right
    )
    {
        // An application is several independently bounded rows. Never combine its stages
        // into a storage envelope or impose one checkpoint budget on their aggregate.
        if (
            !JsonSame(
                left with
                {
                    Plan = null,
                    Stages = [],
                },
                right with
                {
                    Plan = null,
                    Stages = [],
                }
            )
            || !EquivalentPlan(left.Plan, right.Plan)
            || left.Stages.Count != right.Stages.Count
        )
        {
            return false;
        }
        return left.Stages.Zip(right.Stages).All(pair => EquivalentStage(pair.First, pair.Second));
    }

    private static string Write<T, TStored>(string kind, T value, Func<Encoder, T, TStored> encode)
    {
        var encoder = new Encoder(enforceWriteBudget: true);
        var stored = encode(encoder, value);
        return SerializeBounded(encoder.Wrap(kind, stored));
    }

    private static T Read<T, TStored>(string json, string kind, Func<Decoder, TStored, T> decode)
        where T : class
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("A people checkpoint must be an object.");
        }
        if (!root.TryGetProperty("Format", out var format))
        {
            if (
                root.TryGetProperty("CandidateSnapshots", out _)
                || root.TryGetProperty("ActorEvidenceBlocks", out _)
            )
            {
                throw Invalid("A compact people checkpoint must name its format.");
            }
            // Legacy rows remain immutable. They need no compact-write admission check,
            // and historical omitted optional fields keep their existing DTO defaults.
            return JsonSerializer.Deserialize<T>(json)
                ?? throw Invalid("The legacy people checkpoint is empty.");
        }
        if (format.ValueKind != JsonValueKind.String || format.GetString() != FormatVersion)
        {
            throw Invalid("Unsupported people checkpoint format.");
        }
        var names = root.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
        {
            throw Invalid("Duplicate people checkpoint envelope property.");
        }
        var envelope =
            JsonSerializer.Deserialize<Envelope<TStored>>(json, StoredReadOptions)
            ?? throw Invalid("The people checkpoint envelope is empty.");
        if (envelope.Kind != kind || envelope.Value is null)
        {
            throw Invalid("People checkpoint kind or value does not match its reader.");
        }

        // JSONB ::text adds whitespace and can change escaping. Count the same canonical
        // codec bytes admitted on write, rather than rejecting valid rows by transport size.
        SerializeBounded(envelope);
        var decoder = new Decoder(
            envelope.ActorEvidenceBlocks,
            envelope.OwnerObservations,
            envelope.OwnerObservationLists,
            envelope.CandidateSnapshots
        );
        var result = decode(decoder, envelope.Value);
        decoder.RequireAllReferenced();
        return result;
    }

    private static bool Equivalent<T, TStored>(T? left, T? right, Func<Encoder, T, TStored> encode)
        where T : class
    {
        if (left == null || right == null)
        {
            return left == right;
        }
        var leftEncoder = new Encoder(enforceWriteBudget: false);
        var leftValue = encode(leftEncoder, left);
        var rightEncoder = new Encoder(enforceWriteBudget: false);
        var rightValue = encode(rightEncoder, right);

        // Canonical first-encounter tables compare semantic DTOs, not incoming table indexes.
        // This normalization deliberately has no new-write byte guard: descriptor overhead
        // must not make a valid near-limit legacy row unreadable or unequal to itself.
        return JsonSame(
            leftEncoder.Wrap("equality", leftValue),
            rightEncoder.Wrap("equality", rightValue)
        );
    }

    private static bool JsonSame<T>(T left, T right) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(left),
            JsonSerializer.SerializeToElement(right)
        );

    private static string SerializeBounded<T>(T value) =>
        Encoding.UTF8.GetString(SerializeBoundedBytes(value));

    private static byte[] SerializeBoundedBytes<T>(T value)
    {
        using var stream = new CheckpointStream();
        JsonSerializer.Serialize(stream, value);
        return stream.ToArray();
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

        private void RequireCapacity(int added)
        {
            if (Length + added > MaximumCheckpointBytes)
            {
                throw new ArgumentException(
                    "People checkpoint exceeds 32 MiB; no claims, evidence or original observations were truncated."
                );
            }
        }
    }
}
