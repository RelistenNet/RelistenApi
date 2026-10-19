using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Relisten.RealityPipeline.Blobs;

public enum PutOutcome
{
    Created,
    AlreadyExists,
}

/// Object storage for feature and reality blobs (R2 in production, a directory in tests and
/// local development). Objects are written once: a put never replaces an existing object.
public interface IBlobStore
{
    /// Writes the object unless one already exists at the key. Never overwrites.
    Task<PutOutcome> PutIfAbsentAsync(
        string key,
        ReadOnlyMemory<byte> bytes,
        CancellationToken ct = default
    );

    /// The object's bytes, or null if there is none.
    Task<byte[]?> GetAsync(string key, CancellationToken ct = default);

    Task<bool> ExistsAsync(string key, CancellationToken ct = default);

    /// Only feature blobs are ever deleted (garbage collection); reality blobs never are.
    Task DeleteAsync(string key, CancellationToken ct = default);
}
