using System;
using System.Threading.Tasks;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace Relisten.Normalization.Temporal;

/// A normalization workflow of that id is already running.
public sealed class NormalizationAlreadyRunning(string workflowId)
    : Exception($"{workflowId} is already running")
{
    public string WorkflowId { get; } = workflowId;
}

/// Starts normalization workflows (operator commands; later, schedules).
public sealed class NormalizationClient(ITemporalClient client)
{
    /// Starts the LLM song catalogue for one artist. Its workflow id is separate from imports,
    /// so a long run never blocks the artist's import; one run per artist at a time.
    public async Task<string> BuildSongCatalogueAsync(BuildKbCatalogue request)
    {
        var workflowId = "kb/catalogue/" + request.Artist;
        try
        {
            var handle = await client.StartWorkflowAsync(
                (KbCatalogueWorkflow workflow) => workflow.RunAsync(request),
                new WorkflowOptions(workflowId, NormalizationQueues.Workflows)
                {
                    ExecutionTimeout = TimeSpan.FromDays(7),
                    IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
                }
            );
            return handle.Id;
        }
        catch (WorkflowAlreadyStartedException)
        {
            throw new NormalizationAlreadyRunning(workflowId);
        }
    }
}
