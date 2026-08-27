namespace Relisten.Accounts.Contracts.Accounts;

public sealed record BrowserAccountProfileResponse(
    int ContractVersion,
    Guid UserUuid,
    string Username,
    long UsernameVersion,
    bool UsernameReviewNeeded,
    DateTimeOffset? UsernameReviewedAt,
    DateTimeOffset? UsernameChangeAvailableAt);
