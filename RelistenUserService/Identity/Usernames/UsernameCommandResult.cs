using RelistenUserService.Identity.Entities;

namespace RelistenUserService.Identity.Usernames;

public enum UsernameCommandStatus
{
    Success,
    IdempotencyConflict,
    InvalidUsername,
    UsernameChangeTooSoon,
    UsernameUnavailable,
    UsernameVersionStale
}

public sealed record UsernameCommandResult(
    UsernameCommandStatus Status,
    User User,
    DateTimeOffset? RetryAt = null);
