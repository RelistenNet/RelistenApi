namespace Relisten.Accounts.Contracts.Deletion;

/// <summary>The durable input shared by the User Service and the account deletion worker.</summary>
public sealed record AccountDeletionInput(Guid JobId, string UserId)
{
    public const string WorkflowName = "AccountDeletion";
    public const string TaskQueue = "user-data";

    public static string WorkflowId(Guid jobId) => $"account-deletion-{jobId}";
}
