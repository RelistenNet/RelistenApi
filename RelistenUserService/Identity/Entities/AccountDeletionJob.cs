namespace RelistenUserService.Identity.Entities;

/// <summary>Records a deletion request and its completion, including after the user is gone.</summary>
public sealed class AccountDeletionJob
{
    public Guid Id { get; init; }
    public UserId UserId { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}
