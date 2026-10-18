using FluentAssertions;
using Relisten.RealityPipeline.Blobs;

namespace RelistenApiTests.Reality;

// The same behaviour from every IBlobStore: the filesystem store always, and an S3-compatible
// store (MinIO locally, R2 in production) when RELISTEN_TEST_S3_URL is set, e.g.
//   docker run -d -p 19000:9000 minio/minio server /data
//   RELISTEN_TEST_S3_URL=http://127.0.0.1:19000 (bucket relisten-test, minioadmin/minioadmin)
[TestFixture("filesystem")]
[TestFixture("s3")]
public class TestBlobStores(string provider)
{
    private IBlobStore store = null!;
    private string? root;

    [SetUp]
    public async Task CreateStore()
    {
        if (provider == "filesystem")
        {
            root = Path.Combine(Path.GetTempPath(), $"relisten-blobs-{Guid.NewGuid():N}");
            store = new FileSystemBlobStore(root);
            return;
        }

        var url = Environment.GetEnvironmentVariable("RELISTEN_TEST_S3_URL");
        if (string.IsNullOrEmpty(url))
            Assert.Ignore(
                "Set RELISTEN_TEST_S3_URL to an S3-compatible endpoint to run S3 store tests."
            );
        var options = new S3BlobStoreOptions
        {
            ServiceUrl = url,
            Bucket =
                Environment.GetEnvironmentVariable("RELISTEN_TEST_S3_BUCKET") ?? "relisten-test",
            AccessKeyId =
                Environment.GetEnvironmentVariable("RELISTEN_TEST_S3_KEY") ?? "minioadmin",
            SecretAccessKey =
                Environment.GetEnvironmentVariable("RELISTEN_TEST_S3_SECRET") ?? "minioadmin",
            Region = "us-east-1",
        };
        await EnsureBucket(options);
        store = new S3BlobStore(options);
    }

    [TearDown]
    public void RemoveStore()
    {
        (store as IDisposable)?.Dispose();
        if (root != null && Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static string UniqueKey(string name) => $"test/{Guid.NewGuid():N}/{name}";

    [Test]
    public async Task PutsAreWriteOnce()
    {
        var key = UniqueKey("a.bin");

        (await store.PutIfAbsentAsync(key, new byte[] { 1, 2, 3 })).Should().Be(PutOutcome.Created);
        (await store.PutIfAbsentAsync(key, new byte[] { 9 })).Should().Be(PutOutcome.AlreadyExists);

        (await store.GetAsync(key)).Should().Equal(1, 2, 3);
        (await store.ExistsAsync(key)).Should().BeTrue();
    }

    [Test]
    public async Task MissingObjectsReadAsNull()
    {
        var key = UniqueKey("missing.bin");

        (await store.GetAsync(key)).Should().BeNull();
        (await store.ExistsAsync(key)).Should().BeFalse();
    }

    [Test]
    public async Task DeleteRemovesTheObject()
    {
        var key = UniqueKey("gone.bin");
        await store.PutIfAbsentAsync(key, new byte[] { 1 });

        await store.DeleteAsync(key);

        (await store.ExistsAsync(key)).Should().BeFalse();
    }

    [Test]
    public async Task FeatureBlobsComeBackAsWritten()
    {
        var features = new FeatureBlobStore(store, new BlobCodec());
        var rlft = RealityFixtures.FeatureBlob("cens");

        var stored = await features.PutAsync(rlft);

        stored.Key.Should().Be($"features/{RealityFixtures.FeatureMd5}/cens-v1.bin");
        stored.StoredBytes.Should().BeLessThan(rlft.Length);
        (await features.GetAsync(RealityFixtures.FeatureMd5, "cens", 1)).Should().Equal(rlft);
        await features.DeleteAsync(RealityFixtures.FeatureMd5, "cens", 1);
    }

    [Test]
    public async Task RealityBlobsAreImmutable()
    {
        var reality = new RealityBlobStore(store, new BlobCodec());
        var message = RealityFixtures.CornellReality();
        var eventUuid = Guid.CreateVersion7();

        var blob = await reality.PutAsync(eventUuid, 1, message);
        var retried = await reality.PutAsync(eventUuid, 1, message);

        retried.Should().Be(blob);
        blob.Key.Should().Be($"reality/{eventUuid:D}/v1.binpb");
        blob.Bytes.Should().Be(message.Length);
        (await reality.GetAsync(blob.Key, blob.Sha256)).Should().Equal(message);

        var changed = (byte[])message.Clone();
        changed[^1] ^= 1;
        var overwrite = () => reality.PutAsync(eventUuid, 1, changed);
        await overwrite
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*different content*");

        var wrongSha = () => reality.GetAsync(blob.Key, new string('0', 64));
        await wrongSha.Should().ThrowAsync<InvalidDataException>();
    }

    private static async Task EnsureBucket(S3BlobStoreOptions options)
    {
        using var client = new Amazon.S3.AmazonS3Client(
            options.AccessKeyId,
            options.SecretAccessKey,
            new Amazon.S3.AmazonS3Config { ServiceURL = options.ServiceUrl, ForcePathStyle = true }
        );
        try
        {
            await client.PutBucketAsync(options.Bucket);
        }
        catch (Amazon.S3.AmazonS3Exception e)
            when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists") { }
    }
}
