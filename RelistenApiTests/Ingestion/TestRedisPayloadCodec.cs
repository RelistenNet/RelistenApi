using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Google.Protobuf;
using Relisten.Ingestion.Transport;
using StackExchange.Redis;
using Temporalio.Api.Common.V1;
using Temporalio.Converters;

namespace RelistenApiTests.Ingestion;

[TestFixture]
public class TestRedisPayloadCodec
{
    private readonly RedisPayloadCodec codec = new(
        DispatchProxy.Create<IDatabase, UnavailableRedis>(),
        "test"
    );

    [Test]
    public async Task InlinePayloadsWorkWithoutRedisAndPreserveMetadataOrderAndCount()
    {
        var first = new Payload { Data = ByteString.CopyFromUtf8("first") };
        first.Metadata.Add("custom", ByteString.CopyFrom(new byte[] { 0, 255 }));
        var threshold = new Payload { Data = ByteString.CopyFrom(new byte[256 * 1024 - 4]) };
        threshold.CalculateSize().Should().Be(256 * 1024);
        Payload[] originals = [first, threshold];
        var encoded = await codec.EncodeAsync(originals);
        var decoded = await codec.DecodeAsync(encoded);
        decoded.Should().Equal(originals);
        encoded.First().Should().BeSameAs(first);
    }

    [Test]
    public void OversizeIsRejectedBeforeRedisOrSerialization()
    {
        var payload = new Payload { Data = ByteString.CopyFrom(new byte[64 * 1024 * 1024]) };
        Assert.That(
            async () => await codec.EncodeAsync([payload]),
            Throws.TypeOf<RedisPayloadException>().With.Property("Code").EqualTo("PayloadTooLarge")
        );
    }

    [TestCase("wrong-namespace")]
    [TestCase("invalid-digest")]
    [TestCase("oversize")]
    [TestCase("extra-field")]
    [TestCase("duplicate-field")]
    [TestCase("unsupported-version")]
    public void InvalidReferencesFailBeforeRedisLookup(string defect)
    {
        var digest = new string('a', 64);
        var reference = new Dictionary<string, object>
        {
            ["version"] = defect == "unsupported-version" ? 2 : 1,
            ["key"] =
                "relisten:temporal:"
                + (defect == "wrong-namespace" ? "other" : "test")
                + ":payload:v1:"
                + digest,
            ["length"] = defect == "oversize" ? 64 * 1024 * 1024 + 1 : 300_000,
            ["sha256"] = defect == "invalid-digest" ? "secret" : digest,
        };
        if (defect == "extra-field")
            reference["unexpected"] = true;
        var json = JsonSerializer.Serialize(reference);
        if (defect == "duplicate-field")
            json = json.Replace("\"length\"", "\"key\"");
        var payload = new Payload
        {
            Metadata = { ["encoding"] = ByteString.CopyFromUtf8(RedisPayloadCodec.Encoding) },
            Data = ByteString.CopyFromUtf8(json),
        };
        Assert.That(
            async () => await codec.DecodeAsync([payload]),
            Throws
                .TypeOf<RedisPayloadException>()
                .With.Property("Code")
                .EqualTo(
                    defect == "unsupported-version"
                        ? "UnsupportedPayloadReference"
                        : "InvalidPayloadReference"
                )
        );
    }

    [Test]
    public void OffloadPropagatesRedisFailure()
    {
        var payload = LargePayload();
        Assert.That(
            async () => await codec.EncodeAsync([payload]),
            Throws.TypeOf<RedisConnectionException>()
        );
    }

    internal static Payload LargePayload() =>
        new()
        {
            Metadata =
            {
                ["encoding"] = ByteString.CopyFromUtf8("binary/plain"),
                ["custom"] = ByteString.CopyFrom(new byte[] { 255, 0, 1 }),
            },
            Data = ByteString.CopyFrom(new byte[300_000]),
        };

    public class UnavailableRedis : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new RedisConnectionException(
                ConnectionFailureType.UnableToConnect,
                "Redis unavailable for test"
            );
    }
}

/// <summary>Uses only uniquely named keys in the isolated local-dev/temporal Redis.</summary>
[TestFixture, Category("LocalIngestion")]
public class TestRedisPayloadCodecPersistence
{
    private ConnectionMultiplexer connection = null!;
    private IDatabase database = null!;
    private RedisPayloadCodec codec = null!;
    private string keyPrefix = null!;
    private readonly HashSet<string> keys = [];

    [OneTimeSetUp]
    public async Task Connect()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
            Assert.Ignore("Start local-dev/temporal and set RELISTEN_LOCAL_INGESTION_TESTS=1.");
        connection = await ConnectionMultiplexer.ConnectAsync("127.0.0.1:16380");
        database = connection.GetDatabase();
        var ns = "codec-test:" + Guid.NewGuid().ToString("N");
        codec = new RedisPayloadCodec(database, ns);
        keyPrefix = "relisten:temporal:" + Uri.EscapeDataString(ns) + ":payload:v1:";
    }

    [TearDown]
    public async Task ClearKeys()
    {
        foreach (var key in keys)
            await database.KeyDeleteAsync(key);
        keys.Clear();
    }

    [OneTimeTearDown]
    public void Disconnect() => connection?.Dispose();

    [Test]
    public async Task FullProtobufRoundTripPreservesBinaryMetadataAndMixedOrder()
    {
        var large = TestRedisPayloadCodec.LargePayload();
        var snapshot = large.Clone();
        var small = new Payload { Data = ByteString.CopyFromUtf8("small") };
        var encoded = (await codec.EncodeAsync([small, large, small])).ToArray();
        TrackKey(encoded[1]);
        large.Should().Be(snapshot);
        (await codec.DecodeAsync(encoded)).Should().Equal(small, large, small);
    }

    [Test]
    public async Task MetadataContributesToTheOffloadThreshold()
    {
        var payload = new Payload
        {
            Data = ByteString.CopyFromUtf8("small body"),
            Metadata = { ["catalog"] = ByteString.CopyFrom(new byte[256 * 1024]) },
        };
        var reference = (await codec.EncodeAsync([payload])).Single();
        TrackKey(reference);
        reference.CalculateSize().Should().BeLessThan(4096);
        (await codec.DecodeAsync([reference])).Single().Should().Be(payload);
    }

    [Test]
    public async Task ConcurrentReuseKeepsOneKeyAndRefreshesExpiry()
    {
        var payload = TestRedisPayloadCodec.LargePayload();
        var first = (await codec.EncodeAsync([payload])).Single();
        var key = TrackKey(first);
        await database.KeyExpireAsync(key, TimeSpan.FromSeconds(1));
        var reused = await Task.WhenAll(
            Enumerable.Range(0, 12).Select(_ => codec.EncodeAsync([payload]))
        );
        foreach (var result in reused)
            result.Single().Should().Be(first);
        (await database.KeyTimeToLiveAsync(key))
            .Should()
            .BeCloseTo(TimeSpan.FromDays(15), TimeSpan.FromSeconds(10));
        ((byte[])(await database.StringGetAsync(key))!).Should().Equal(payload.ToByteArray());
    }

    [TestCase("missing", "PayloadNotFound")]
    [TestCase("wrong-type", "PayloadCorrupt")]
    [TestCase("wrong-length", "PayloadCorrupt")]
    [TestCase("wrong-digest", "PayloadCorrupt")]
    public async Task LostOrCorruptBytesFailClosed(string defect, string expectedCode)
    {
        var payload = TestRedisPayloadCodec.LargePayload();
        var reference = (await codec.EncodeAsync([payload])).Single();
        var key = TrackKey(reference);
        if (defect == "missing")
            await database.KeyDeleteAsync(key);
        else if (defect == "wrong-type")
        {
            await database.KeyDeleteAsync(key);
            await database.ListRightPushAsync(key, "damaged");
        }
        else
        {
            var bytes = payload.ToByteArray();
            if (defect == "wrong-length")
                bytes = [0];
            else
                bytes[^1] ^= 1;
            await database.StringSetAsync(key, bytes);
        }
        Assert.That(
            async () => await codec.DecodeAsync([reference]),
            Throws.TypeOf<RedisPayloadException>().With.Property("Code").EqualTo(expectedCode)
        );
    }

    [Test]
    public async Task ReuseCannotReplaceDifferentStoredBytes()
    {
        var payload = TestRedisPayloadCodec.LargePayload();
        var key = TrackKey((await codec.EncodeAsync([payload])).Single());
        await database.StringSetAsync(key, "damaged");
        Assert.That(
            async () => await codec.EncodeAsync([payload]),
            Throws
                .TypeOf<RedisPayloadException>()
                .With.Property("Code")
                .EqualTo("PayloadContentConflict")
        );
        (await database.StringGetAsync(key)).ToString().Should().Be("damaged");
    }

    [TestCase(false, "PayloadCorrupt")]
    [TestCase(true, "NestedPayloadReference")]
    public async Task DigestMatchingBytesStillRequireAValidNonNestedPayload(
        bool nested,
        string code
    )
    {
        var bytes = nested
            ? new Payload
            {
                Metadata = { ["encoding"] = ByteString.CopyFromUtf8(RedisPayloadCodec.Encoding) },
                Data = ByteString.CopyFrom(new byte[300_000]),
            }.ToByteArray()
            : Enumerable.Repeat((byte)255, 300_000).ToArray();
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var key = keyPrefix + digest;
        keys.Add(key);
        await database.StringSetAsync(key, bytes);
        var reference = new Payload
        {
            Metadata = { ["encoding"] = ByteString.CopyFromUtf8(RedisPayloadCodec.Encoding) },
            Data = ByteString.CopyFrom(
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        version = 1,
                        key,
                        length = bytes.Length,
                        sha256 = digest,
                    }
                )
            ),
        };
        Assert.That(
            async () => await codec.DecodeAsync([reference]),
            Throws.TypeOf<RedisPayloadException>().With.Property("Code").EqualTo(code)
        );
    }

    [Test]
    public async Task TemporalDataConverterRoundTripsTypedValues()
    {
        var converter = DataConverter.Default with { PayloadCodec = codec };
        var value = new Catalog("artist", [new string('x', 300_000), "second"]);
        var encoded = await converter.ToPayloadsAsync(new object[] { value });
        TrackKey(encoded.Single());
        var decoded = await converter.ToSingleValueAsync<Catalog>(encoded);
        decoded.Should().BeEquivalentTo(value);
    }

    private string TrackKey(Payload reference)
    {
        using var json = JsonDocument.Parse(reference.Data.Memory);
        var key = json.RootElement.GetProperty("key").GetString()!;
        keys.Add(key);
        return key;
    }

    public record Catalog(string Artist, string[] Items);
}
