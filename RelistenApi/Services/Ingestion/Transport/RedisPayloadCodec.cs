using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Google.Protobuf;
using StackExchange.Redis;
using Temporalio.Api.Common.V1;
using Temporalio.Converters;

namespace Relisten.Ingestion.Transport;

/// <summary>
/// Keeps large serialized Temporal values in Redis, including their original metadata.
/// Redis must retain these bytes for the execution and closed-history retention periods;
/// a missing value is an error because refetching upstream data would change replay inputs.
/// </summary>
public sealed class RedisPayloadCodec : IPayloadCodec
{
    public const int InlineThreshold = 256 * 1024;
    public const int MaximumPayloadSize = 64 * 1024 * 1024;
    public const string Encoding = "binary/relisten-redis-v1";
    public static readonly TimeSpan Retention = TimeSpan.FromDays(15);

    // Compare and refresh in one Redis operation. A collision or damaged existing value
    // must not silently replace bytes that another execution already references.
    private const string StoreScript = """
        local kind = redis.call('TYPE', KEYS[1]).ok
        if kind ~= 'none' and kind ~= 'string' then return 0 end
        local existing = redis.call('GET', KEYS[1])
        if existing then
            if existing ~= ARGV[1] then return 0 end
            redis.call('PEXPIRE', KEYS[1], ARGV[2])
        else
            redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[2])
        end
        return 1
        """;

    // Check length at Redis before transferring bytes into the worker process.
    private const string ReadScript = """
        if redis.call('EXISTS', KEYS[1]) == 0 then return {0} end
        if redis.call('TYPE', KEYS[1]).ok ~= 'string' then return {1} end
        if redis.call('STRLEN', KEYS[1]) ~= tonumber(ARGV[1]) then return {1} end
        return {2, redis.call('GET', KEYS[1])}
        """;

    private readonly IDatabase database;
    private readonly string keyPrefix;

    public RedisPayloadCodec(IDatabase database, string temporalNamespace)
    {
        ArgumentException.ThrowIfNullOrEmpty(temporalNamespace);
        this.database = database;
        keyPrefix = $"relisten:temporal:{Uri.EscapeDataString(temporalNamespace)}:payload:v1:";
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<Payload>> EncodeAsync(
        IReadOnlyCollection<Payload> payloads
    )
    {
        var encoded = new List<Payload>(payloads.Count);
        foreach (var payload in payloads)
        {
            encoded.Add(await EncodePayloadAsync(payload));
        }

        return encoded;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<Payload>> DecodeAsync(
        IReadOnlyCollection<Payload> payloads
    )
    {
        var decoded = new List<Payload>(payloads.Count);
        foreach (var payload in payloads)
        {
            decoded.Add(IsReference(payload) ? await DecodeReferenceAsync(payload) : payload);
        }

        return decoded;
    }

    private async Task<Payload> EncodePayloadAsync(Payload payload)
    {
        var length = payload.CalculateSize();
        if (length > MaximumPayloadSize)
        {
            throw new RedisPayloadException(
                "PayloadTooLarge",
                "Serialized payload exceeds 64 MiB."
            );
        }

        if (length <= InlineThreshold)
        {
            return payload;
        }

        // Store the complete protobuf value: replay needs the original metadata too.
        var bytes = payload.ToByteArray();
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var key = keyPrefix + digest;
        var stored = await database.ScriptEvaluateAsync(
            StoreScript,
            new RedisKey[] { key },
            new RedisValue[] { bytes, (long)Retention.TotalMilliseconds }
        );

        if ((int)stored != 1)
        {
            throw Error("PayloadContentConflict", key);
        }

        // Return a reference only after Redis has stored or refreshed its immutable bytes.
        return new Payload
        {
            Metadata = { ["encoding"] = ByteString.CopyFromUtf8(Encoding) },
            Data = ByteString.CopyFrom(
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        version = 1,
                        key,
                        length,
                        sha256 = digest,
                    }
                )
            ),
        };
    }

    private async Task<Payload> DecodeReferenceAsync(Payload payload)
    {
        var reference = ParseReference(payload);
        var result = (RedisResult[])
            (
                await database.ScriptEvaluateAsync(
                    ReadScript,
                    new RedisKey[] { reference.Key },
                    new RedisValue[] { reference.Length }
                )
            )!;

        if ((int)result[0] == 0)
        {
            throw Error("PayloadNotFound", reference.Key);
        }

        if ((int)result[0] != 2)
        {
            throw Error("PayloadCorrupt", reference.Key);
        }

        // Neither valid JSON nor a matching Redis key proves that the bytes are intact.
        var bytes = (byte[])result[1]!;
        if (
            bytes.Length != reference.Length
            || Convert.ToHexStringLower(SHA256.HashData(bytes)) != reference.Digest
        )
        {
            throw Error("PayloadCorrupt", reference.Key);
        }

        Payload original;
        try
        {
            original = Payload.Parser.ParseFrom(bytes);
        }
        catch (InvalidProtocolBufferException)
        {
            throw Error("PayloadCorrupt", reference.Key);
        }

        if (IsReference(original))
        {
            throw Error("NestedPayloadReference", reference.Key);
        }

        return original;
    }

    private (string Key, int Length, string Digest) ParseReference(Payload payload)
    {
        // Reference documents are small. Bound JSON parsing before inspecting supplied keys.
        if (payload.Data.Length > 4096)
        {
            throw new RedisPayloadException(
                "InvalidPayloadReference",
                "Reference document exceeds 4096 bytes."
            );
        }

        try
        {
            using var document = JsonDocument.Parse(payload.Data.Memory);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4)
            {
                throw new JsonException();
            }

            if (root.GetProperty("version").GetInt32() != 1)
            {
                throw new RedisPayloadException(
                    "UnsupportedPayloadReference",
                    "Unsupported Redis payload reference version."
                );
            }

            var key = root.GetProperty("key").GetString();
            var digest = root.GetProperty("sha256").GetString();
            var length = root.GetProperty("length").GetInt32();
            if (
                digest is null
                || digest.Length != 64
                || digest.Any(c => !char.IsAsciiHexDigitLower(c))
                || key != keyPrefix + digest
                || length <= InlineThreshold
                || length > MaximumPayloadSize
            )
            {
                throw new JsonException();
            }

            return (key, length, digest);
        }
        catch (Exception error)
            when (error
                    is JsonException
                        or InvalidOperationException
                        or KeyNotFoundException
                        or FormatException
            )
        {
            // Never include arbitrary supplied keys or the JSON in diagnostic messages.
            throw new RedisPayloadException(
                "InvalidPayloadReference",
                "Invalid Redis payload reference."
            );
        }
    }

    private static bool IsReference(Payload payload) =>
        payload.Metadata.TryGetValue("encoding", out var encoding)
        && encoding.ToStringUtf8() == Encoding;

    private static RedisPayloadException Error(string code, string key) =>
        new(code, $"{code}: {key}");
}

/// <summary>A payload integrity or reference error. Code is stable for operator diagnostics.</summary>
public sealed class RedisPayloadException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
