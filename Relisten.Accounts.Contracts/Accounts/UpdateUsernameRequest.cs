namespace Relisten.Accounts.Contracts.Accounts;

public sealed record UpdateUsernameRequest(
    int ContractVersion,
    Guid ClientCommandUuid,
    long ExpectedUsernameVersion,
    string Username);
