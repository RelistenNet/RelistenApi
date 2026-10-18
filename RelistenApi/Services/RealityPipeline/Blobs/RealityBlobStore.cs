using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Relisten.RealityPipeline.Blobs;

/// What reality.versions records about a version's blob. Sha256 and Bytes are of the
/// Reality message itself, not of its compressed form in storage.
public sealed record RealityBlobRef(string Key, string Sha256, long Bytes);

/// Reality version blobs: every version's relisten.reality.v1.Reality message, written once
/// and never deleted (data-model.md §5).
public sealed class RealityBlobStore(IBlobStore store, BlobCodec codec)
{
    /// Writes v{n} of the event. Retrying with identical bytes is a no-op; different bytes
    /// under an existing key are refused, because versions are immutable.
    public async Task<RealityBlobRef> PutAsync(
        Guid eventUuid,
        int versionN,
        byte[] message,
        CancellationToken ct = default
    )
    {
        var blob = new RealityBlobRef(
            BlobKeys.Reality(eventUuid, versionN),
            Sha256Hex(message),
            message.Length
        );
        var outcome = await store.PutIfAbsentAsync(blob.Key, codec.EncodeReality(message), ct);
        if (outcome == PutOutcome.AlreadyExists)
        {
            var existing = await GetAsync(blob.Key, expectedSha256: null, ct);
            if (existing == null || Sha256Hex(existing) != blob.Sha256)
                throw new InvalidOperationException(
                    $"Reality blob {blob.Key} already exists with different content"
                );
        }
        return blob;
    }

    /// The Reality message bytes, verified against expectedSha256 when given.
    public async Task<byte[]?> GetAsync(
        string key,
        string? expectedSha256,
        CancellationToken ct = default
    )
    {
        var stored = await store.GetAsync(key, ct);
        if (stored == null)
            return null;
        var message = BlobCodec.Decode(stored);
        if (expectedSha256 != null && Sha256Hex(message) != expectedSha256)
            throw new InvalidDataException($"Reality blob {key} doesn't match its recorded sha256");
        return message;
    }

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));
}
