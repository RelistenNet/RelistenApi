using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Relisten.RealityPipeline.Blobs;

/// Object keys (docs/formats.md): one bucket, two prefixes.
public static partial class BlobKeys
{
    /// <c>features/{md5}/{component}-v{N}.bin</c>, one object per component.
    public static string Feature(string md5, string component, int version) =>
        $"features/{md5}/{component}-v{version}.bin";

    /// <c>reality/{event_uuid}/v{n}.binpb</c>: relisten.reality.v1.Reality, never rewritten.
    public static string Reality(Guid eventUuid, int versionN) =>
        $"reality/{eventUuid:D}/v{versionN}.binpb";

    public static void Validate(string key)
    {
        if (!KeyPattern().IsMatch(key) || key.Contains(".."))
            throw new ArgumentException($"Invalid blob key '{key}'", nameof(key));
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*(/[a-z0-9][a-z0-9._-]*)*$")]
    private static partial Regex KeyPattern();
}
