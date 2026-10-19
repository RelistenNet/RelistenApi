using FluentAssertions;
using Relisten.RealityPipeline.Blobs;

namespace RelistenApiTests.Reality;

public class TestBlobCodec
{
    private readonly BlobCodec codec = new();

    [TestCase("peaks")]
    [TestCase("cens")]
    [TestCase("tuning")]
    [TestCase("chromaprint")]
    public void FeatureBlobsRoundTripExactly(string component)
    {
        var rlft = RealityFixtures.FeatureBlob(component);

        var stored = codec.EncodeFeature(rlft);

        BlobCodec.Decode(stored).Should().Equal(rlft);
    }

    [Test]
    public void ShufflingBeatsPlainZstdOnRealFeatures()
    {
        long raw = 0,
            plain = 0,
            shuffled = 0;
        foreach (var component in new[] { "peaks", "cens", "chromaprint" })
        {
            var rlft = RealityFixtures.FeatureBlob(component);
            raw += rlft.Length;
            plain += codec.EncodeReality(rlft).Length; // codec 1: zstd alone
            shuffled += codec.EncodeFeature(rlft).Length;
        }

        shuffled.Should().BeLessThan(plain);
        ((double)shuffled / raw).Should().BeLessThan(0.85);
    }

    [Test]
    public void RealityBlobsRoundTripExactly()
    {
        var message = RealityFixtures.CornellReality();

        var stored = codec.EncodeReality(message);

        stored.Length.Should().BeLessThan(message.Length / 2);
        BlobCodec.Decode(stored).Should().Equal(message);
    }

    [Test]
    public void CorruptFeatureBlobsAreRejected()
    {
        var rlft = RealityFixtures.FeatureBlob("peaks");
        rlft[rlft.Length / 2] ^= 0xff;

        var encode = () => codec.EncodeFeature(rlft);

        encode.Should().Throw<InvalidDataException>().WithMessage("*crc32*");
    }

    [Test]
    public void UnencodedBytesAreRejected()
    {
        var decode = () => BlobCodec.Decode(RealityFixtures.FeatureBlob("peaks"));

        decode.Should().Throw<InvalidDataException>().WithMessage("*RLZ1*");
    }

    [TestCase("features/abc/peaks-v1.bin", true)]
    [TestCase("reality/01a14202-2800-7292-b696-2ae15226d1c8/v1.binpb", true)]
    [TestCase("../etc/passwd", false)]
    [TestCase("/abs/key", false)]
    [TestCase("features/../x", false)]
    [TestCase("Features/Upper", false)]
    public void KeysAreValidated(string key, bool valid)
    {
        var validate = () => BlobKeys.Validate(key);

        if (valid)
            validate.Should().NotThrow();
        else
            validate.Should().Throw<ArgumentException>();
    }
}
