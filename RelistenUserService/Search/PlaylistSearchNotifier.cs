using Relisten.Accounts.Contracts.Search;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;

namespace RelistenUserService.Search;

/// <summary>Wakes playlist indexing after a successful database commit.</summary>
public sealed class PlaylistSearchNotifier(
    ITemporalClient client,
    ILogger<PlaylistSearchNotifier> logger
)
{
    public async Task Changed(PlaylistId id)
    {
        // The write already committed. Give this best-effort RPC its own short deadline.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await client.Connection.ConnectAsync().WaitAsync(deadline.Token);
            await client.StartWorkflowAsync(
                PlaylistSearchJob.SyncWorkflow,
                [id.Value],
                new WorkflowOptions(PlaylistSearchJob.SyncId(id.Value), PlaylistSearchJob.Queue)
                {
                    IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
                    IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
                    StartSignal = PlaylistSearchJob.ChangedSignal,
                    Rpc = new RpcOptions { CancellationToken = deadline.Token },
                }
            );
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Could not wake playlist search indexing");
        }
    }
}
