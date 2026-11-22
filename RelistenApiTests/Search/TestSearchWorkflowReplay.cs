using Relisten.Ingestion.Workflows;
using Relisten.Normalization.Temporal;
using Temporalio.Common;
using Temporalio.Worker;

namespace RelistenApiTests.Search;

[TestFixture]
public sealed class TestSearchWorkflowReplay
{
    [TestCase("artist-refresh-before-search.json", "test/a8ab235e-3582-4d0b-b1ff-563eabad18f0")]
    [TestCase("song-catalogue-before-search.json", "test/song-catalogue-before-search")]
    public async Task HistoryBeforeSearchReindexStillReplays(string fixture, string workflowId)
    {
        var json = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "workflows", fixture)
        );
        // These synthetic histories ran against the workflow code before the search patch.
        var history = WorkflowHistory.FromJson(workflowId, json);
        await new WorkflowReplayer(
            new WorkflowReplayerOptions()
                .AddWorkflow<ArtistRefreshWorkflow>()
                .AddWorkflow<SongCatalogueWorkflow>()
        ).ReplayWorkflowAsync(history);
    }
}
