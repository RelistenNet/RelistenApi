using Relisten.Accounts.Contracts.Deletion;

namespace Relisten.Accounts.Contracts.Search;

/// <summary>The worker's playlist indexing addresses and default search alias.</summary>
public static class PlaylistSearchJob
{
    public const string Queue = AccountDeletionInput.TaskQueue;
    public const string SyncWorkflow = "PlaylistSearchSyncWorkflow";
    public const string RebuildId = "playlist-search-rebuild";
    public const string ChangedSignal = "playlist-changed";
    public const string DefaultAlias = "user_playlists";

    public static string SyncId(string playlistId) => $"playlist-search-sync-{playlistId}";
}
