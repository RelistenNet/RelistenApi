using System;
using System.Threading.Tasks;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Relisten.Normalization.Temporal;

/// The nightly venue run, started by the schedule. A schedule's runs get their own ids, so the
/// run itself is started as a child under the one venue-run id (NormalizationClient.VenueRunId):
/// when a manual run holds it, tonight is skipped; a manual start while this child runs is
/// refused. Two venue runs never pay for the same spellings.
[Workflow]
public class NightlyVenueCatalogueWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(BuildVenueCatalogue request)
    {
        try
        {
            var result = await Workflow.ExecuteChildWorkflowAsync(
                (VenueCatalogueWorkflow workflow) => workflow.RunAsync(request),
                new ChildWorkflowOptions
                {
                    Id = NormalizationClient.VenueRunId,
                    TaskQueue = NormalizationQueues.Workflows,
                    ExecutionTimeout = TimeSpan.FromDays(1),
                }
            );
            return $"{result.Chunks} model calls, {result.RowsMapped} rows mapped, {result.VenuesNamed} venues named";
        }
        catch (WorkflowAlreadyStartedException)
        {
            return "skipped: a venue run is already running";
        }
    }
}
