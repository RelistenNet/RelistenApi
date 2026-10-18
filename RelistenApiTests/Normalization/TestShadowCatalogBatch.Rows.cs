using System.Text.Json;
using FluentAssertions;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

public sealed partial class TestShadowCatalogBatch
{
    [Test]
    public async Task OrdinaryLocalRowsExtractCreditsAndTitlesAndPreservePlaybackOwnership()
    {
        var fixture = await LocalRows();

        await new ShadowCatalogBatch([
            new ShadowPeopleAdapter(),
            new ShadowPerformanceAdapter(),
        ]).RunRowsInlineAsync(fixture.Snapshot, fixture.Output);

        var results = ReadResults(fixture.Output);
        results.Should().HaveCount(5);
        results.Should().OnlyContain(result => Text(result, "outcome") == "proposed");
        results
            .Should()
            .OnlyContain(result =>
                result.GetProperty("artistUuid").GetGuid() == fixture.ArtistUuid
            );
        var credit = results
            .Single(result => Text(result, "domain") == "credits")
            .GetProperty("proposal")
            .GetProperty("credits")
            .EnumerateArray()
            .Single();
        credit.GetProperty("sourceUuid").GetGuid().Should().Be(fixture.SourceUuid);

        var file = results
            .Single(result => Text(result, "domain") == "file_contents")
            .GetProperty("proposal");
        var playback = file.GetProperty("playback");
        playback.GetProperty("fileUuid").GetGuid().Should().Be(fixture.FileUuid);
        playback.GetProperty("sourceUuid").GetGuid().Should().Be(fixture.SourceUuid);
        playback.GetProperty("showUuid").GetGuid().Should().Be(fixture.ShowUuid);
        Text(playback, "mp3Url").Should().Be(fixture.Url);
        Text(file.GetProperty("titleInterpretation"), "originalTitle").Should().Be(fixture.Title);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task OrdinaryRowsResumeKeepsResultsAndRejectsChangedInputBytes(bool changeProvider)
    {
        var fixture = await LocalRows();
        var providers = Path.Combine(directory, "provider-inputs.jsonl");
        await File.WriteAllTextAsync(providers, "");
        await new ShadowCatalogBatch([new ShadowPerformanceAdapter()]).RunRowsInlineAsync(
            fixture.Snapshot,
            fixture.Output,
            providerInputsPath: providers
        );
        await new ShadowCatalogBatch([new ShadowPerformanceAdapter()]).RunRowsInlineAsync(
            fixture.Snapshot,
            fixture.Output,
            resume: true,
            providerInputsPath: providers
        );
        var originalResults = ReadResults(fixture.Output);
        originalResults.Should().HaveCount(3);

        await File.AppendAllTextAsync(changeProvider ? providers : fixture.Snapshot, "\n");
        var changed = () =>
            new ShadowCatalogBatch([new ShadowPerformanceAdapter()]).RunRowsInlineAsync(
                fixture.Snapshot,
                fixture.Output,
                resume: true,
                providerInputsPath: providers
            );

        await changed.Should().ThrowAsync<InvalidDataException>();
        ReadResults(fixture.Output)
            .Select(result => result.GetRawText())
            .Should()
            .Equal(originalResults.Select(result => result.GetRawText()));
    }

    [Test]
    public async Task OrdinaryRowsKeepPlayableFilesWhenCapturedSourceCoordinatesConflict()
    {
        var fixture = await LocalRows(conflictingFileSource: true);

        await new ShadowCatalogBatch([new ShadowPerformanceAdapter()]).RunRowsInlineAsync(
            fixture.Snapshot,
            fixture.Output
        );

        var results = ReadResults(fixture.Output);
        var performance = results.Single(result =>
            Text(result, "domain") == "performances"
            && result.GetProperty("inputUuid").GetGuid() == fixture.FileUuid
        );
        Text(performance, "outcome").Should().Be("unresolved");
        performance
            .GetProperty("proposal")
            .GetProperty("unresolved")
            .EnumerateArray()
            .Select(value => value.GetString())
            .Should()
            .Contain("file_source_context_mismatch");
        var file = results.Single(result => Text(result, "domain") == "file_contents");
        Text(file, "outcome").Should().Be("proposed");
        var playback = file.GetProperty("proposal").GetProperty("playback");
        playback.GetProperty("fileUuid").GetGuid().Should().Be(fixture.FileUuid);
        Text(playback, "mp3Url").Should().Be(fixture.Url);
        playback.GetProperty("sourceUuid").ValueKind.Should().Be(JsonValueKind.Null);
        playback.GetProperty("showUuid").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private async Task<LocalRowFiles> LocalRows(bool conflictingFileSource = false)
    {
        var artistUuid = Guid.NewGuid();
        var sourceUuid = Guid.NewGuid();
        var showUuid = Guid.NewGuid();
        var fileUuid = Guid.NewGuid();
        var setUuid = Guid.NewGuid();
        const string title = "Scarlet Begonias";
        const string url = "https://example.test/retained-audio.mp3";
        var source = new
        {
            id = 11,
            uuid = sourceUuid,
            artist_id = 1,
            show_id = 101,
            display_date = "1977-05-08",
            taper = "Charlie Miller",
        };
        var show = new
        {
            id = 101,
            uuid = showUuid,
            artist_id = 1,
            date = "1977-05-08",
            display_date = "1977-05-08",
            performance_type = "unknown",
        };
        var rows = new object[]
        {
            new
            {
                kind = "artist",
                row = new
                {
                    id = 1,
                    uuid = artistUuid,
                    name = "Local Artist",
                },
                configured_provider_mappings = Array.Empty<object>(),
            },
            new
            {
                kind = "source_sample",
                row = source,
                show_context = show,
            },
            new
            {
                kind = "track_sample",
                row = new
                {
                    id = 21,
                    uuid = fileUuid,
                    artist_id = 1,
                    source_id = conflictingFileSource ? 999 : 11,
                    source_set_id = 31,
                    title,
                    mp3_url = url,
                },
                source_context = source,
                show_context = show,
                set_context = new
                {
                    id = 31,
                    uuid = setUuid,
                    source_id = 11,
                },
            },
        };
        var snapshot = Path.Combine(directory, "local-rows.jsonl");
        await File.WriteAllLinesAsync(
            snapshot,
            rows.Select(value => JsonSerializer.Serialize(value))
        );
        return new(
            snapshot,
            Path.Combine(directory, "local-output"),
            artistUuid,
            sourceUuid,
            showUuid,
            fileUuid,
            title,
            url
        );
    }

    private sealed record LocalRowFiles(
        string Snapshot,
        string Output,
        Guid ArtistUuid,
        Guid SourceUuid,
        Guid ShowUuid,
        Guid FileUuid,
        string Title,
        string Url
    );
}
