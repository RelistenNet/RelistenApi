namespace Relisten.Accounts.Contracts.Accounts;

public sealed record AccountProfileResponse(
    int ContractVersion,
    Guid UserUuid,
    string Username,
    long UsernameVersion,
    bool UsernameReviewNeeded,
    DateTimeOffset? UsernameReviewedAt,
    DateTimeOffset? UsernameChangeAvailableAt,
    Guid NativeSessionUuid);
