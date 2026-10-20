using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Services;

[TestFixture]
public sealed class TestNormalizationReviewBenchmarkQueue
{
    private const string Artist = "77a58ff9-2e01-c59c-b8eb-cff106049b72";
    private const string OtherArtist = "ca53d281-0041-ae33-a050-c87702d93b0c";
    private string directory = null!;
    private string QueuePath => Path.Combine(directory, "errors.jsonl");

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "normalization-review-queue-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(directory, true);

    [Test]
    public async Task Resolved_agreement_does_not_ask_for_duplicate_ID_review_and_retains_source_coverage()
    {
        var resolvedAgreement = Row("resolved", Artist, Comparison(["drums"], ["drums"]));
        var realDisagreement = Row(
            "unresolved",
            Artist,
            Comparison(["drums", "space"], ["drums", "jam"])
        );
        var unaligned = new
        {
            key = new[] { Artist, "setlist", "unaligned" },
            status = "unaligned_reference",
            reference = new
            {
                artistUuid = Artist,
                source = new { path = "original.jsonl.gz", line = 42 },
            },
        };
        await WriteAsync(
            "artist-setlist-reference-benchmark-v2",
            resolvedAgreement,
            realDisagreement,
            unaligned
        );

        var result = await new ReviewBenchmarkQueue().ReadAsync(QueuePath, new());

        result.ComparisonBasis.Should().Be("resolvedIdentity");
        result.RowCount.Should().Be(3);
        result.Total.Should().Be(2);
        result.ArtistCounts[Artist].Should().Be(2);
        result
            .CategoryCounts.Should()
            .BeEquivalentTo(
                new Dictionary<string, int> { ["compared"] = 1, ["unaligned_reference"] = 1 }
            );
        result
            .SelectedRows.Select(row => row.Status)
            .Should()
            .Equal("compared", "unaligned_reference");
        var current = result.SelectedRows[0];
        current.ActionKeys.Should().Equal("jam", "space");
        current.RecordedTitles.Should().Equal("Current jam file");
        current
            .Original.GetProperty("rawIdentity")
            .GetProperty("missing")[0]
            .GetString()
            .Should()
            .Be("old-drums");
        current.Comparison.GetProperty("missing")[0].GetString().Should().Be("space");
        result
            .SelectedRows[1]
            .Original.GetProperty("reference")
            .GetProperty("source")
            .GetProperty("line")
            .GetInt32()
            .Should()
            .Be(42);
        result.SelectedRows[1].ActionKeys.Should().BeEmpty();
        result
            .Artists[0]
            .GetProperty("rawIdentity")
            .GetProperty("missing")
            .GetInt32()
            .Should()
            .Be(8);
    }

    [Test]
    public async Task Filter_counts_cover_the_full_scan_and_offset_applies_after_both_filters()
    {
        await WriteAsync(
            "artist-setlist-reference-benchmark-v2",
            Row("a", Artist, Comparison(["a"], ["b"])),
            Coverage("coverage", Artist),
            Row("other", OtherArtist, Comparison(["c"], ["d"])),
            Row("b", Artist, Comparison(["e"], ["f"]))
        );

        var result = await new ReviewBenchmarkQueue().ReadAsync(
            QueuePath,
            new(1, 1, Artist, "compared")
        );

        result.RowCount.Should().Be(4);
        result.Total.Should().Be(2);
        result
            .ArtistCounts.Should()
            .BeEquivalentTo(new Dictionary<string, int> { [Artist] = 3, [OtherArtist] = 1 });
        result
            .CategoryCounts.Should()
            .BeEquivalentTo(
                new Dictionary<string, int> { ["compared"] = 2, ["missing_reference"] = 1 }
            );
        result.SelectedRows.Should().ContainSingle().Which.ActionKeys.Should().Equal("f", "e");
        var anotherDomain = await new ReviewBenchmarkQueue().ReadAsync(
            QueuePath,
            new(Domain: "people")
        );
        anotherDomain.RowCount.Should().Be(4);
        anotherDomain.Total.Should().Be(0);
        anotherDomain.ArtistCounts.Should().BeEmpty();
        anotherDomain.CategoryCounts.Should().BeEmpty();
    }

    [Test]
    public async Task Legacy_completed_benchmarks_keep_their_retained_identity_comparison()
    {
        var row = new
        {
            key = new[] { Artist, "legacy" },
            artistUuid = Artist,
            status = "compared",
            expected = new[] { "old-drums" },
            predicted = new[] { "drums" },
            missing = new[] { "old-drums" },
            extra = new[] { "drums" },
            filesForReview = new[] { new { title = "Drums", predicted = new[] { "drums" } } },
        };
        await WriteAsync("artist-setlist-reference-benchmark-v1", row);

        var result = await new ReviewBenchmarkQueue().ReadAsync(QueuePath, new());

        result.ComparisonBasis.Should().Be("retainedIdentity");
        result.Total.Should().Be(1);
        result.SelectedRows[0].ActionKeys.Should().Equal("drums", "old-drums");
        result.SelectedRows[0].RecordedTitles.Should().Equal("Drums");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Changed_bytes_are_rejected_before_returning_any_page(bool changeLength)
    {
        await WriteAsync(
            "artist-setlist-reference-benchmark-v2",
            Row("a", Artist, Comparison(["a"], ["b"]))
        );
        if (changeLength)
        {
            await File.AppendAllTextAsync(QueuePath, " ");
        }
        else
        {
            var text = await File.ReadAllTextAsync(QueuePath);
            await File.WriteAllTextAsync(
                QueuePath,
                text.Replace("compared", "compareD", StringComparison.Ordinal)
            );
        }

        var read = () => new ReviewBenchmarkQueue().ReadAsync(QueuePath, new());

        await read.Should()
            .ThrowAsync<NormalizationConflictException>()
            .WithMessage("*differs from its completion manifest*");
    }

    [Test]
    public async Task A_hash_valid_row_cannot_claim_another_artists_reference()
    {
        await WriteAsync(
            "artist-setlist-reference-benchmark-v2",
            new
            {
                key = new[] { Artist, "reference" },
                artistUuid = Artist,
                status = "missing_reference",
                referenceAccounts = new[] { new { artistUuid = OtherArtist } },
            }
        );

        var read = () => new ReviewBenchmarkQueue().ReadAsync(QueuePath, new());

        await read.Should()
            .ThrowAsync<NormalizationConflictException>()
            .WithMessage("*different artist*");
    }

    [Test]
    public async Task V2_cannot_silently_fall_back_to_raw_comparison_or_hide_inconsistent_sets()
    {
        await WriteAsync(
            "artist-setlist-reference-benchmark-v2",
            new
            {
                key = new[] { Artist, "legacy" },
                artistUuid = Artist,
                status = "compared",
                expected = new[] { "old-drums" },
                predicted = new[] { "drums" },
                missing = new[] { "old-drums" },
                extra = new[] { "drums" },
            }
        );
        var read = () => new ReviewBenchmarkQueue().ReadAsync(QueuePath, new());
        await read.Should().ThrowAsync<ArgumentException>().WithMessage("*no resolved identity*");

        await WriteAsync(
            "artist-setlist-reference-benchmark-v2",
            Row(
                "bad",
                Artist,
                new
                {
                    status = "compared",
                    expected = new[] { "space" },
                    predicted = new[] { "jam" },
                    missing = Array.Empty<string>(),
                    extra = Array.Empty<string>(),
                }
            )
        );
        await read.Should()
            .ThrowAsync<NormalizationConflictException>()
            .WithMessage("*sets do not match*");
    }

    [Test]
    public async Task Authenticated_queues_larger_than_fifty_MiB_are_scanned_completely()
    {
        var line =
            JsonSerializer.Serialize(Coverage("large", Artist)) + new string(' ', 900_000) + "\n";
        await using (var output = File.Create(QueuePath))
        {
            var bytes = Encoding.UTF8.GetBytes(line);
            for (var index = 0; index < 60; index++)
            {
                await output.WriteAsync(bytes);
            }
        }
        await WriteManifestAsync("artist-setlist-reference-benchmark-v2");

        var result = await new ReviewBenchmarkQueue().ReadAsync(QueuePath, new(59, 1));

        result.RowCount.Should().Be(60);
        result.Total.Should().Be(60);
        result.SelectedRows.Should().ContainSingle();
        result.ArtistCounts[Artist].Should().Be(60);
    }

    [Test]
    public async Task Queues_above_the_explicit_bound_are_rejected_without_scanning()
    {
        using (var file = File.Create(QueuePath))
        {
            file.SetLength(128L * 1024 * 1024 + 1);
        }
        var read = () => new ReviewBenchmarkQueue().ReadAsync(QueuePath, new());

        await read.Should().ThrowAsync<ArgumentException>().WithMessage("*at most 128 MiB*");
    }

    private static object Row(string id, string artist, object comparison) =>
        new
        {
            key = new[] { artist, id },
            artistUuid = artist,
            status = "compared",
            rawIdentity = new { missing = new[] { "old-drums" }, extra = new[] { "drums" } },
            resolvedIdentity = comparison,
            identityResolutions = new[] { new { originalKey = "old-jam", targetKey = "jam" } },
            filesForReview = new[]
            {
                new { title = "Already resolved drums", predicted = new[] { "drums" } },
                new { title = "Unrelated raw candidate", predicted = new[] { "old-unrelated" } },
                new { title = "Current jam file", predicted = new[] { "old-jam" } },
            },
        };

    private static object Comparison(string[] expected, string[] predicted) =>
        new
        {
            status = "compared",
            expected,
            predicted,
            missing = expected.Except(predicted).ToArray(),
            extra = predicted.Except(expected).ToArray(),
        };

    private static object Coverage(string id, string artist) =>
        new
        {
            key = new[] { artist, id },
            artistUuid = artist,
            status = "missing_reference",
            resolvedIdentity = new { status = "missing_reference" },
        };

    private async Task WriteAsync(string version, params object[] rows)
    {
        await File.WriteAllLinesAsync(QueuePath, rows.Select(row => JsonSerializer.Serialize(row)));
        await WriteManifestAsync(version);
    }

    private async Task WriteManifestAsync(string version)
    {
        await using var input = File.OpenRead(QueuePath);
        var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(input));
        var manifest = new
        {
            version,
            state = "completed",
            outputs = new[]
            {
                new
                {
                    path = QueuePath,
                    bytes = input.Length,
                    sha256,
                },
            },
            artists = new[] { Artist, OtherArtist }.Select(artistUuid => new
            {
                artistUuid,
                artist = artistUuid == Artist ? "Grateful Dead" : "Phish",
                rawIdentity = new { missing = 8 },
                resolvedIdentity = new { missing = 1 },
            }),
            limitations = new[] { "Set membership is not occurrence alignment." },
        };
        await File.WriteAllTextAsync(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(manifest)
        );
    }
}
