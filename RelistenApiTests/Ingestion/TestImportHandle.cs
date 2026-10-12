using FluentAssertions;
using Relisten.Ingestion;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace RelistenApiTests.Ingestion;

/// <summary>Checks the SDK run-chain semantics used by handles after history rollover and ID reuse.</summary>
[TestFixture, NonParallelizable]
public class TestImportHandle
{
    private WorkflowEnvironment environment = null!;

    [OneTimeSetUp]
    public async Task StartServer() => environment = await WorkflowEnvironment.StartLocalAsync();

    [OneTimeTearDown]
    public async Task StopServer() => await environment.DisposeAsync();

    [Test]
    public async Task ResultAndDescribeFollowContinuationWithoutFollowingALaterOperation()
    {
        var queue = "handle/" + Guid.NewGuid();
        using var worker = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(queue).AddWorkflow<HandleChainWorkflow>()
        );
        await worker.ExecuteAsync(async () =>
        {
            var id = "handle/" + Guid.NewGuid();
            var admitted = await environment.Client.StartWorkflowAsync(
                (HandleChainWorkflow workflow) => workflow.RunAsync(1),
                new WorkflowOptions(id, queue)
            );
            var handle = new ImportHandle<int>(admitted);
            var running = await WaitForContinuation(handle);
            running.RunId.Should().NotBe(handle.ResultRunId);
            await admitted.SignalAsync(workflow => workflow.FinishAsync());
            (await handle.GetResultAsync()).Should().Be(42);
            (await handle.DescribeAsync()).Status.Should().Be(WorkflowExecutionStatus.Completed);

            var later = await environment.Client.StartWorkflowAsync(
                (HandleChainWorkflow workflow) => workflow.RunAsync(0),
                new WorkflowOptions(id, queue)
            );
            try
            {
                var original = await handle.DescribeAsync();
                original.RunId.Should().Be(running.RunId);
                original.Status.Should().Be(WorkflowExecutionStatus.Completed);
                (await handle.GetResultAsync()).Should().Be(42);

                // An old handle may report the original operation already completed or
                // a run mismatch. Neither response may cancel the later operation.
                try
                {
                    await handle.CancelAsync();
                }
                catch (RpcException error)
                    when (error.Code
                            is RpcException.StatusCode.NotFound
                                or RpcException.StatusCode.FailedPrecondition
                    ) { }
                (await later.DescribeAsync()).Status.Should().Be(WorkflowExecutionStatus.Running);
            }
            finally
            {
                await later.SignalAsync(workflow => workflow.FinishAsync());
                await later.GetResultAsync();
            }
        });
    }

    [Test]
    public async Task CancelFollowsTheAdmittedChainAfterContinuation()
    {
        var queue = "handle/" + Guid.NewGuid();
        using var worker = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(queue).AddWorkflow<HandleChainWorkflow>()
        );
        await worker.ExecuteAsync(async () =>
        {
            var admitted = await environment.Client.StartWorkflowAsync(
                (HandleChainWorkflow workflow) => workflow.RunAsync(1),
                new WorkflowOptions("handle/" + Guid.NewGuid(), queue)
            );
            var handle = new ImportHandle<int>(admitted);
            await WaitForContinuation(handle);
            await handle.CancelAsync();
            Func<Task> result = async () => await handle.GetResultAsync();
            var failure = await result.Should().ThrowAsync<WorkflowFailedException>();
            failure.Which.InnerException.Should().BeOfType<CanceledFailureException>();
            (await handle.DescribeAsync()).Status.Should().Be(WorkflowExecutionStatus.Canceled);
        });
    }

    private static async Task<WorkflowExecutionDescription> WaitForContinuation(
        ImportHandle<int> handle
    )
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var description = await handle.DescribeAsync();
            if (
                description.RunId != handle.ResultRunId
                && description.Status == WorkflowExecutionStatus.Running
            )
                return description;
            await Task.Delay(25, timeout.Token);
        }
    }

    [Workflow]
    public class HandleChainWorkflow
    {
        private bool finished;

        [WorkflowRun]
        public async Task<int> RunAsync(int continuations)
        {
            if (continuations > 0)
                throw Workflow.CreateContinueAsNewException(
                    (HandleChainWorkflow workflow) => workflow.RunAsync(continuations - 1)
                );
            await Workflow.WaitConditionAsync(() => finished);
            return 42;
        }

        [WorkflowSignal]
        public Task FinishAsync()
        {
            finished = true;
            return Task.CompletedTask;
        }
    }
}
