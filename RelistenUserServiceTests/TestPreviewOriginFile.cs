using FluentAssertions;
using NUnit.Framework;
using RelistenUserService.Authentication.OpenIdConnect;

namespace RelistenUserServiceTests;

[TestFixture]
public sealed class TestPreviewOriginFile
{
    private string _path = "";

    [SetUp]
    public void CreatePath() =>
        _path = Path.Combine(Path.GetTempPath(), $"preview-file-{Guid.NewGuid():N}.json");

    [TearDown]
    public void RemoveFile() => File.Delete(_path);

    [TestCase("null")]
    [TestCase("{}")]
    [TestCase("{\"version\":2,\"revision\":1,\"origins\":[]}")]
    [TestCase("{\"version\":1,\"revision\":1,\"origins\":null}")]
    [TestCase("{\"version\":1,\"revision\":1,\"origins\":[],\"extra\":true}")]
    [TestCase("{\"version\":1,\"revision\":1,\"revision\":2,\"origins\":[]}")]
    [TestCase("{\"version\":1,\"revision\":1,\"origins\":[\"https://evil.example\"]}")]
    public async Task Invalid_snapshots_are_not_consumable(string json)
    {
        await File.WriteAllTextAsync(_path, json);
        var read = () => PreviewOriginFileLoader.ReadAsync(_path, CancellationToken.None);
        await read.Should().ThrowAsync<Exception>();
    }

    [Test]
    public async Task Oversized_snapshot_is_rejected_before_deserialization()
    {
        await File.WriteAllTextAsync(
            _path,
            new string(' ', PreviewOriginFileLoader.MaximumSnapshotBytes + 1)
        );
        var read = () => PreviewOriginFileLoader.ReadAsync(_path, CancellationToken.None);
        await read.Should().ThrowAsync<InvalidDataException>();
    }

    [Test]
    public async Task Valid_snapshot_preserves_revision_and_exact_origins()
    {
        await File.WriteAllTextAsync(
            _path,
            """{"version":1,"revision":42,"origins":["https://relisten-pr-138.tail09dbf.ts.net"]}"""
        );
        var snapshot = await PreviewOriginFileLoader.ReadAsync(_path, CancellationToken.None);
        snapshot.Revision.Should().Be(42);
        snapshot.Origins.Should().Equal("https://relisten-pr-138.tail09dbf.ts.net");
    }
}
