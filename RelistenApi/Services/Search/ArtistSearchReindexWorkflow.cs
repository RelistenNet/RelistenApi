using System;
using System.Threading.Tasks;
using Relisten.Normalization.Temporal;
using Temporalio.Workflows;

namespace Relisten.Search;

[Workflow]
public sealed class ArtistSearchReindexWorkflow
{
    [WorkflowRun]
    public Task RunAsync(Guid artist) => SearchReindexStep.BestEffort(artist);

    internal static Task Start(Guid artist) =>
        Workflow.StartChildWorkflowAsync(
            (ArtistSearchReindexWorkflow child) => child.RunAsync(artist),
            // Wait for Temporal to start the child, then let the import return.
            new ChildWorkflowOptions { ParentClosePolicy = ParentClosePolicy.Abandon }
        );
}
