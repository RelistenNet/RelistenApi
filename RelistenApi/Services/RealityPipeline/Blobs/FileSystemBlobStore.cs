using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Relisten.RealityPipeline.Blobs;

/// A directory as object storage, for tests and local development.
public sealed class FileSystemBlobStore(string root) : IBlobStore
{
    public string Root { get; } = Path.GetFullPath(root);

    public async Task<PutOutcome> PutIfAbsentAsync(
        string key,
        ReadOnlyMemory<byte> bytes,
        CancellationToken ct = default
    )
    {
        var path = PathOf(key);
        if (File.Exists(path))
            return PutOutcome.AlreadyExists;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(temp, bytes.ToArray(), ct);
        try
        {
            // No overwrite: a concurrent writer that got there first wins, as with If-None-Match.
            File.Move(temp, path, overwrite: false);
            return PutOutcome.Created;
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(temp);
            return PutOutcome.AlreadyExists;
        }
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        var path = PathOf(key);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(File.Exists(PathOf(key)));

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        File.Delete(PathOf(key));
        return Task.CompletedTask;
    }

    private string PathOf(string key)
    {
        BlobKeys.Validate(key);
        return Path.Combine(Root, key.Replace('/', Path.DirectorySeparatorChar));
    }
}
