using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Relisten.RealityPipeline.Blobs;

public sealed record StoredFeatureBlob(
    string Key,
    string Md5,
    string Component,
    int Version,
    long StoredBytes,
    PutOutcome Outcome
);

/// Feature blobs, one object per (md5, component, version), byte-shuffled and zstd-compressed
/// in storage. Readers get the RLFT bytes back exactly as written.
public sealed class FeatureBlobStore(IBlobStore store, BlobCodec codec)
{
    public async Task<StoredFeatureBlob> PutAsync(byte[] rlft, CancellationToken ct = default)
    {
        var header = Rlft.Parse(rlft).Header;
        var key = BlobKeys.Feature(header.Md5, header.Component, (int)header.ComponentVersion);
        var encoded = codec.EncodeFeature(rlft);
        var outcome = await store.PutIfAbsentAsync(key, encoded, ct);
        return new StoredFeatureBlob(
            key,
            header.Md5,
            header.Component,
            (int)header.ComponentVersion,
            encoded.Length,
            outcome
        );
    }

    /// The RLFT bytes, crc-checked, or null if the blob isn't stored.
    public async Task<byte[]?> GetAsync(
        string md5,
        string component,
        int version,
        CancellationToken ct = default
    )
    {
        var stored = await store.GetAsync(BlobKeys.Feature(md5, component, version), ct);
        return stored == null ? null : BlobCodec.Decode(stored);
    }

    public Task DeleteAsync(
        string md5,
        string component,
        int version,
        CancellationToken ct = default
    ) => store.DeleteAsync(BlobKeys.Feature(md5, component, version), ct);
}
