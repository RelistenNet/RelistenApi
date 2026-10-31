using Relisten.Accounts.Contracts.Identifiers;

namespace Relisten.Accounts.Contracts.Accounts;

public sealed record UpdateUsernameRequest(
    int ContractVersion,
    CommandId CommandId,
    long ExpectedUsernameVersion,
    string Username
);
