using System.Diagnostics;
using Relisten.RealityPipeline.Blobs;

namespace RelistenApiTests.Reality;

// Measures the feature codec over a directory of RLFT blobs ({md5}/{component}-v{N}.bin, as
// the alignment CLI caches them). Run explicitly:
//   RELISTEN_FEATURE_CACHE=…/cache/features/mpg123 dotnet test --filter BenchFeatureCompression
[Explicit("Benchmark over a local feature cache")]
public class BenchFeatureCompression
{
    [TestCase(3)]
    [TestCase(19)]
    public void CompressionRatioAndSpeed(int level)
    {
        var root = Environment.GetEnvironmentVariable("RELISTEN_FEATURE_CACHE");
        if (string.IsNullOrEmpty(root))
            Assert.Ignore("Set RELISTEN_FEATURE_CACHE");
        var limit = int.Parse(
            Environment.GetEnvironmentVariable("RELISTEN_FEATURE_LIMIT") ?? "446"
        );
        var codec = new BlobCodec(level);
        long raw = 0,
            stored = 0;
        var encode = new Stopwatch();
        var decode = new Stopwatch();
        var byComponent = new Dictionary<string, (long Raw, long Stored)>();
        foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d).Take(limit))
        foreach (var file in Directory.GetFiles(dir, "*.bin"))
        {
            var rlft = File.ReadAllBytes(file);
            encode.Start();
            var encoded = codec.EncodeFeature(rlft);
            encode.Stop();
            decode.Start();
            BlobCodec.Decode(encoded);
            decode.Stop();
            raw += rlft.Length;
            stored += encoded.Length;
            var component = Path.GetFileName(file).Split('-')[0];
            var (r, s) = byComponent.GetValueOrDefault(component);
            byComponent[component] = (r + rlft.Length, s + encoded.Length);
        }

        TestContext.Out.WriteLine(
            $"zstd-{level}: {raw / 1e6:F1} MB → {stored / 1e6:F1} MB ({100.0 * stored / raw:F1}%), "
                + $"encode {raw / 1e6 / encode.Elapsed.TotalSeconds:F1} MB/s, decode {raw / 1e6 / decode.Elapsed.TotalSeconds:F1} MB/s"
        );
        foreach (var (component, (r, s)) in byComponent.OrderBy(c => c.Key))
            TestContext.Out.WriteLine($"  {component}: {100.0 * s / r:F1}%");
    }
}
