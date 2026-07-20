namespace Relisten.Accounts.Contracts.Errors;

public static class AccountErrorCodes
{
    public const string IdempotencyConflict = "idempotency_conflict";
    public const string InvalidCommandUuid = "invalid_command_uuid";
    public const string InvalidContractVersion = "invalid_contract_version";
    public const string InvalidRequest = "invalid_request";
    public const string InvalidUsername = "invalid_username";
    public const string UsernameChangeTooSoon = "username_change_too_soon";
    public const string UsernameUnavailable = "username_unavailable";
    public const string UsernameVersionStale = "username_version_stale";
}
