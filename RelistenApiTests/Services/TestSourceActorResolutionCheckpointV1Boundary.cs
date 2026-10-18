using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>
/// Reads exact boundary bytes emitted by the actual pre-upgrade writer. The generator pads
/// one query hash, so these fixtures test storage compatibility only and cannot authorize
/// matching, Work admission or identity publication. Public recovery uses separate captures.
/// </summary>
[TestFixture]
public sealed class TestSourceActorResolutionCheckpointV1Boundary
{
    private static readonly JsonSerializerOptions ReceiptOptions = new()
    {
        RespectRequiredConstructorParameters = true,
    };

    [TestCase(false)]
    [TestCase(true)]
    public async Task ValidV1BoundaryRemainsReadableAndEqualWhenV2CannotWriteIt(bool batch)
    {
        var receiptFile =
            Environment.GetEnvironmentVariable("RELISTEN_SOURCE_ACTOR_V1_BOUNDARY_RECEIPT_FILE")
            ?? Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "normalization",
                "source-actor-v1-boundary-receipt.json"
            );
        File.Exists(receiptFile)
            .Should()
            .BeTrue("the actual unchanged v1 writer must capture these bytes before upgrade");
        var receipt = JsonSerializer.Deserialize<Receipt>(
            await File.ReadAllTextAsync(receiptFile),
            ReceiptOptions
        )!;
        receipt.CaptureVersion.Should().Be("source-actor-v1-storage-boundary-capture-v1");
        receipt.FixturePurpose.Should().StartWith("STORAGE-ONLY");
        receipt.WriterFormat.Should().Be("source-actor-resolution-checkpoint-v1");
        var artifact = batch ? receipt.Batch : receipt.Preparation;
        var fileName = batch
            ? "source-actor-v1-boundary-batch.json.gz"
            : "source-actor-v1-boundary-preparation.json.gz";
        artifact.FileName.Should().Be(fileName);
        artifact.BaselineBytes.Should().BeGreaterThan(0);
        (artifact.BaselineBytes + artifact.PaddingBytes)
            .Should()
            .Be(SourceActorResolutionCheckpoint.MaximumCheckpointBytes);
        var compressed = await File.ReadAllBytesAsync(
            Path.Combine(Path.GetDirectoryName(receiptFile)!, fileName)
        );
        compressed.Length.Should().Be(artifact.GzipBytes);
        Hash(compressed).Should().Be(artifact.GzipSha256);
        await using var input = new MemoryStream(compressed);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        await using var output = new MemoryStream();
        await gzip.CopyToAsync(output);
        var bytes = output.ToArray();
        bytes
            .Length.Should()
            .Be(artifact.UncompressedBytes)
            .And.Be(SourceActorResolutionCheckpoint.MaximumCheckpointBytes);
        Hash(bytes).Should().Be(artifact.UncompressedSha256);
        var json = Encoding.UTF8.GetString(bytes);
        using (var document = JsonDocument.Parse(json))
        {
            document
                .RootElement.GetProperty("Format")
                .GetString()
                .Should()
                .Be(receipt.WriterFormat);
            document
                .RootElement.GetProperty("Kind")
                .GetString()
                .Should()
                .Be(batch ? "resolution_batch" : "resolution_preparation");
        }

        if (batch)
        {
            var original = SourceActorResolutionCheckpoint.ReadBatch(json);
            var reread = SourceActorResolutionCheckpoint.ReadBatch(json);
            SourceActorResolutionCheckpoint.EquivalentBatch(original, original).Should().BeTrue();
            SourceActorResolutionCheckpoint.EquivalentBatch(original, reread).Should().BeTrue();
            RequirePadding(original.Preparation, artifact);
            Action newWrite = () => SourceActorResolutionCheckpoint.WriteBatch(original);
            newWrite
                .Should()
                .Throw<SourceActorResolutionCapacityException>()
                .Which.Reason.Should()
                .Be("checkpoint_capacity_exceeded");
        }
        else
        {
            var original = SourceActorResolutionCheckpoint.ReadPreparation(json);
            var reread = SourceActorResolutionCheckpoint.ReadPreparation(json);
            SourceActorResolutionCheckpoint
                .EquivalentPreparation(original, original)
                .Should()
                .BeTrue();
            SourceActorResolutionCheckpoint
                .EquivalentPreparation(original, reread)
                .Should()
                .BeTrue();
            RequirePadding(original, artifact);
            Action newWrite = () => SourceActorResolutionCheckpoint.WritePreparation(original);
            newWrite
                .Should()
                .Throw<SourceActorResolutionCapacityException>()
                .Which.Reason.Should()
                .Be("checkpoint_capacity_exceeded");
        }
    }

    private static void RequirePadding(
        SourceActorResolutionPreparation preparation,
        Artifact artifact
    )
    {
        var hash = preparation.CandidatePools[0].QueryHash;
        hash.Length.Should().BeGreaterThan(artifact.PaddingBytes);
        hash.AsSpan(hash.Length - artifact.PaddingBytes).IndexOfAnyExcept('x').Should().Be(-1);
        var originalHash = hash[..^artifact.PaddingBytes];
        Hash(Encoding.UTF8.GetBytes(originalHash)).Should().Be(artifact.OriginalQueryHashSha256);
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record Receipt(
        string CaptureVersion,
        string FixturePurpose,
        string WriterFormat,
        string ResolverVersion,
        string SourceCaptureFileName,
        string SourceCaptureSha256,
        Guid SourcePublishedAttemptUuid,
        string Mutation,
        Artifact Preparation,
        Artifact Batch
    );

    private sealed record Artifact(
        string FileName,
        int UncompressedBytes,
        string UncompressedSha256,
        int GzipBytes,
        string GzipSha256,
        int BaselineBytes,
        int PaddingBytes,
        string OriginalQueryHashSha256
    );
}
