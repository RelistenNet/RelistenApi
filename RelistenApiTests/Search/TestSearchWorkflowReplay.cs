using Relisten.Ingestion.Workflows;
using Temporalio.Common;
using Temporalio.Worker;

namespace RelistenApiTests.Search;

[TestFixture]
public sealed class TestSearchWorkflowReplay
{
    [Test]
    public async Task ArtistImportHistoryBeforeSearchReindexStillReplays()
    {
        // Captured from the synthetic Archive import tests before the search patch.
        var json = await File.ReadAllTextAsync(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "workflows",
                "artist-refresh-before-search.json"
            )
        );
        // Provider child IDs are derived from the recorded workflow's ID.
        var history = WorkflowHistory.FromJson("test/a8ab235e-3582-4d0b-b1ff-563eabad18f0", json);
        await new WorkflowReplayer(
            new WorkflowReplayerOptions().AddWorkflow<ArtistRefreshWorkflow>()
        ).ReplayWorkflowAsync(history);
    }
}
