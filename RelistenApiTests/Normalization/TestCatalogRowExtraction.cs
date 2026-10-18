using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>Protects per-row extraction and the existing JSONL consumer contract without a database.</summary>
[TestFixture]
public sealed class TestCatalogRowExtraction
{
    private static readonly Guid Artist = Guid.Parse("4b50d2ec-6d47-4285-8a4d-b2a2aad9d1b0");
    private static readonly Guid Source = Guid.Parse("f43fd245-4241-4458-9308-cbac631ba3d7");
    private static readonly Guid File = Guid.Parse("bde14ada-a3b0-46ed-a633-d7bab48d7292");

    [Test]
    public void SourcePreservesActorAliasesCreditsAndExactUnicodeFieldEvidence()
    {
        const string description = "🎵\nRecorded by Zoë Vale AKA \"Night & Day\"";
        using var document = JsonDocument.Parse(SourceRow(description).GetRawText());
        var extractor = new CatalogRowExtraction();

        var result = extractor.Extract(document.RootElement, Artist);
        var repeated = extractor.Extract(document.RootElement, Artist);

        result.ResourceUuid.Should().Be(Source);
        result.ArtistUuid.Should().Be(Artist);
        result.Family.Should().Be("artist:1:show:25");
        result.Inputs["description"].Should().Be(description);
        result
            .CreditExtraction!.Credits.Should()
            .Contain(credit =>
                credit.Name == "Zoë Vale" && credit.Role == RecordingCreditRole.Taper
            );
        result
            .CreditExtraction.Credits.Should()
            .Contain(credit =>
                credit.Name == "Pat Reed" && credit.Role == RecordingCreditRole.Transferrer
            );
        foreach (var credit in result.CreditExtraction.Credits)
        {
            Slice(result.Inputs[credit.Evidence.Field]!, credit.Evidence)
                .Should()
                .Be(credit.Evidence.Quote);
        }
        var actor = result.SourceActorAliases!.Assertions.Should().ContainSingle().Subject;
        actor.Name.Should().Be("Zoë Vale");
        actor.Aliases.Should().ContainSingle().Which.Wording.Should().Be("Night & Day");
        repeated
            .SourceActorAliases!.Assertions.Single()
            .AssertionUuid.Should()
            .Be(actor.AssertionUuid);
        result.TitleExtraction.Should().BeNull();
    }

    [Test]
    public void CompoundFileKeepsRepeatedMentionsArtistScopeAndCapturedPlaybackContext()
    {
        var source = Guid.NewGuid();
        var set = Guid.NewGuid();
        var show = Guid.NewGuid();
        var row = TrackRow("Café > Fire > Café", source, set, show);

        var result = new CatalogRowExtraction().Extract(row, Artist);

        result.ResourceUuid.Should().Be(File);
        result.TrackContext.Should().Be(new NormalizationTrackInputContext(source, set, show));
        result.Inputs["title"].Should().Be("Café > Fire > Café");
        result.TitleExtraction!.ArtistUuid.Should().Be(Artist);
        result
            .TitleExtraction.Segments.Select(segment => segment.Title)
            .Should()
            .Equal("Café", "Fire", "Café");
        foreach (var segment in result.TitleExtraction.Segments)
        {
            Slice(result.TitleExtraction.OriginalTitle, segment.Evidence)
                .Should()
                .Be(segment.Evidence.Quote);
            segment.OccurrenceUuid.Should().BeNull();
            segment.StartMs.Should().BeNull();
            segment.EndMs.Should().BeNull();
        }
        result.CreditExtraction.Should().BeNull();
        result.SourceActorAliases.Should().BeNull();
    }

    [Test]
    public void ParentlessFileAndNullTitleReturnAnUnresolvedExtractionWithoutInventedContext()
    {
        var result = new CatalogRowExtraction().Extract(TrackRow(null), Artist);

        result.ResourceUuid.Should().Be(File);
        result.TrackContext.Should().Be(new NormalizationTrackInputContext(null, null, null));
        result.Inputs["title"].Should().BeNull();
        result.TitleExtraction!.IsResolved.Should().BeFalse();
        result.TitleExtraction.Reason.Should().Be("no_song_title_identified");
        result.TitleExtraction.Segments.Should().BeEmpty();
    }

    [Test]
    public void ReturnedJsonSurvivesDisposalOfTheRawRow()
    {
        JsonElement result;
        using (var document = JsonDocument.Parse(TrackRow("Café > Fire").GetRawText()))
        {
            result = new CatalogRowExtraction().ExtractJson(document.RootElement, Artist);
        }

        result.GetProperty("resourceUuid").GetGuid().Should().Be(File);
        var segments = result
            .GetProperty("titleExtraction")
            .GetProperty("segments")
            .EnumerateArray()
            .ToArray();
        segments
            .Select(segment => segment.GetProperty("title").GetString())
            .Should()
            .Equal("Café", "Fire");
    }

    [Test]
    public void BadRowFailsWithoutContaminatingTheNextExtraction()
    {
        var invalid = JsonSerializer.SerializeToElement(
            new
            {
                kind = "track_sample",
                row = new
                {
                    id = 20,
                    uuid = "not-a-uuid",
                    artist_id = 1,
                    title = "Café",
                },
            }
        );
        var extractor = new CatalogRowExtraction();

        var run = () => extractor.Extract(invalid, Artist);
        run.Should().Throw<FormatException>();
        var next = extractor.Extract(TrackRow("Fire"), Artist);

        next.ResourceUuid.Should().Be(File);
        next.TitleExtraction!.Segments.Should().ContainSingle().Which.Title.Should().Be("Fire");
    }

    [Test]
    public void NonRecordingRowsAreExplicitlyUnsupported()
    {
        var row = JsonSerializer.SerializeToElement(
            new { kind = "setlist_sample", row = new { uuid = Guid.NewGuid() } }
        );
        var run = () => new CatalogRowExtraction().Extract(row, Artist);
        run.Should().Throw<NotSupportedException>();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task InlineJsonMatchesTheExistingPublishedBatchWireFormat(bool includeAliases)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "catalog-row-extraction-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        try
        {
            var rows = new[]
            {
                SourceRow("Recorded by Zoë Vale AKA Night Owl"),
                TrackRow("Café > Fire > Café"),
            };
            var input = Path.Combine(directory, "capture.jsonl");
            var capture = new List<string>
            {
                JsonSerializer.Serialize(
                    new
                    {
                        kind = "snapshot",
                        read_only = "on",
                        in_recovery = true,
                        snapshot = "test:1:",
                    }
                ),
                JsonSerializer.Serialize(
                    new { kind = "artist", row = new { id = 1, uuid = Artist } }
                ),
                JsonSerializer.Serialize(new { kind = "totals", rows = new { artists = 1 } }),
            };
            capture.AddRange(rows.Select(row => row.GetRawText()));
            capture.Add(
                JsonSerializer.Serialize(
                    new { kind = "snapshot_complete", completed_at = "2026-10-18T00:00:00Z" }
                )
            );
            await System.IO.File.WriteAllLinesAsync(input, capture);
            var output = Path.Combine(directory, "output");

            var report = await new NormalizationExtractionBatch(includeAliases).RunAsync(
                input,
                output
            );
            var written = await System.IO.File.ReadAllLinesAsync(
                Path.Combine(output, "extractions.jsonl")
            );
            var inline = new CatalogRowExtraction(includeAliases);

            report.State.Should().Be("completed");
            report.ExtractedRecords.Should().Be(2);
            written
                .Should()
                .Equal(rows.Select(row => inline.ExtractJson(row, Artist).GetRawText()));
            using var first = JsonDocument.Parse(written[0]);
            first
                .RootElement.TryGetProperty("sourceActorAliases", out _)
                .Should()
                .Be(includeAliases);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static JsonElement SourceRow(string description) =>
        JsonSerializer.SerializeToElement(
            new
            {
                kind = "source_sample",
                family = "artist:1:show:25",
                row = new
                {
                    id = 10,
                    uuid = Source,
                    artist_id = 1,
                    taper = "Zoë Vale",
                    transferrer = "Pat Reed",
                    description,
                    source = (string?)null,
                    lineage = (string?)null,
                    taper_notes = (string?)null,
                },
            }
        );

    private static JsonElement TrackRow(
        string? title,
        Guid? source = null,
        Guid? set = null,
        Guid? show = null
    ) =>
        JsonSerializer.SerializeToElement(
            new
            {
                kind = "track_sample",
                row = new
                {
                    id = 20,
                    uuid = File,
                    artist_id = 1,
                    title,
                },
                source_context = source is { } sourceUuid
                    ? new
                    {
                        uuid = sourceUuid,
                        source_context_projection = "catalog_coordinates_without_credit_prose_v1",
                    }
                    : null,
                set_context = set is { } setUuid ? new { uuid = setUuid } : null,
                show_context = show is { } showUuid ? new { uuid = showUuid } : null,
            }
        );

    private static string Slice(string text, NormalizationTextEvidence evidence) =>
        string.Concat(
            text.EnumerateRunes()
                .Skip(evidence.StartCodePoint)
                .Take(evidence.EndCodePoint - evidence.StartCodePoint)
                .Select(rune => rune.ToString())
        );
}
