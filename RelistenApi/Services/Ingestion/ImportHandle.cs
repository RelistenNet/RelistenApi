using System.Linq;
using System.Threading.Tasks;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;

namespace Relisten.Ingestion;

/// <summary>
/// Refers to the execution admitted by one request. Reusing an artist workflow ID
/// later must not make this handle read or cancel that later import.
/// </summary>
public sealed class ImportHandle<TResult>(WorkflowHandle admitted)
{
    public string WorkflowId => admitted.Id;
    public string FirstExecutionRunId => admitted.FirstExecutionRunId!;
    public string ResultRunId => admitted.ResultRunId!;

    public Task<TResult> GetResultAsync() => admitted.GetResultAsync<TResult>();

    public Task CancelAsync() => admitted.CancelAsync();

    /// <summary>Follows recorded continuation links, never the latest unrelated execution of this ID.</summary>
    public async Task<WorkflowExecutionDescription> DescribeAsync()
    {
        var runId = ResultRunId;
        while (true)
        {
            var run = admitted.Client.GetWorkflowHandle(WorkflowId, runId);
            var description = await run.DescribeAsync();
            if (description.Status != WorkflowExecutionStatus.ContinuedAsNew)
            {
                return description;
            }

            var history = await run.FetchHistoryAsync();
            runId = history
                .Events.Last(e => e.WorkflowExecutionContinuedAsNewEventAttributes != null)
                .WorkflowExecutionContinuedAsNewEventAttributes.NewExecutionRunId;
        }
    }
}
