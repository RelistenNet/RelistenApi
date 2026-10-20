using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

public sealed partial class TestFileTitleReplayBatch
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task SavedReplayOnlySongDecisionChangesCurrentFilesAndWithdrawalRestoresTheBaseline(
        bool literalAlias
    )
    {
        var fixture = await Export(replayTitles: ["Playin", "Playing in the Band"]);
        var musicDirectory = Path.Combine(directory, "music-baseline");
        await new MusicRepertoireConsolidationBatch().RunAsync(
            fixture.Input.MusicInputManifest.Path,
            musicDirectory,
            fixture.Input.MusicInputManifest.Sha256
        );
        var musicPath = Path.Combine(musicDirectory, "manifest.json");
        var music = await ReviewedMusicProjection.OpenAsync(
            fixture.Input.MusicInputManifest.Path,
            fixture.Input.MusicInputManifest.Sha256,
            musicPath,
            await HashAsync(musicPath)
        );
        var replayDirectory = Path.Combine(directory, "replay-baseline");
        await new FileTitleReplayBatch().RunAsync(
            fixture.Manifest.Path,
            replayDirectory,
            fixture.Manifest.Sha256
        );
        var replayPath = Path.Combine(replayDirectory, "manifest.json");
        var projection = await ReviewedFileTitleProjection.OpenAsync(
            music,
            fixture.Manifest.Path,
            fixture.Manifest.Sha256,
            replayPath,
            await HashAsync(replayPath)
        );
        await using var source = File.OpenRead(fixture.Input.Titles.Path);
        await using var compressed = new GZipStream(source, CompressionMode.Decompress);
        using var reader = new StreamReader(compressed);
        var original = JsonSerializer.Deserialize<FileTitleReplayTitle>(
            (await reader.ReadLineAsync())!,
            Json
        )!;
        var target = ShadowRepertoireAdapter.ProvisionalEntityKey(
            original.ArtistUuid,
            "Playing in the Band"
        );
        var selector = literalAlias
            ? new ExpertKnowledgeSelector(
                "repertoire",
                Literal: "Playin",
                ArtistUuid: original.ArtistUuid
            )
            : new ExpertKnowledgeSelector(
                "repertoire",
                AccountStableKey: ReviewedRepertoireCorrespondences.StableKey(
                    original.Scope,
                    ShadowRepertoireAdapter.ProvisionalEntityKey(original.ArtistUuid, "Playin")
                ),
                ArtistUuid: original.ArtistUuid
            );
        var rule = new ExpertKnowledgeRule(
            Guid.NewGuid(),
            1,
            "accepted",
            new(
                "songs",
                selector,
                new(
                    "songs",
                    "repertoire",
                    ReviewedRepertoireCorrespondences.StableKey(original.Scope, target)
                ),
                [new(fixture.Manifest.Path, fixture.Manifest.Bytes, fixture.Manifest.Sha256)],
                "Synthetic reviewed equivalence between two retained file-title observations."
            ),
            Guid.NewGuid(),
            DateTime.UtcNow
        );
        var accepted = ExpertKnowledgeResolver.CreateSnapshot([rule]);
        var page = await projection.ReadAsync(accepted);
        page.Total.Should().Be(1);
        page.Items.Should().ContainSingle().Which.TitleId.Should().Be(1);
        page.Items.Single()
            .Correspondence.Targets.Should()
            .ContainSingle()
            .Which.Song.EntityKey.Should()
            .Be(target);
        page.KnowledgeSha256.Should().Be(accepted.Sha256);
        var withdrawn = ExpertKnowledgeResolver.CreateSnapshot([
            rule with
            {
                State = "retracted",
                Revision = 2,
            },
        ]);
        (await projection.ReadAsync(withdrawn)).Items.Should().BeEmpty();
        await File.AppendAllTextAsync(fixture.Input.Titles.Path, "changed");
        var unavailable = () => projection.ReadAsync(withdrawn);
        await unavailable.Should().ThrowAsync<IOException>();
    }
}
