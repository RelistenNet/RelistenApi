using System.Text.Json.Serialization;
using Relisten.Accounts.Contracts.Identifiers;

namespace Relisten.Accounts.Contracts.Accounts;

public sealed record AccountProfileResponse(
    int ContractVersion,
    UserId UserId,
    string Username,
    long UsernameVersion,
    bool UsernameReviewNeeded,
    DateTimeOffset? UsernameReviewedAt,
    DateTimeOffset? UsernameChangeAvailableAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        SessionId? NativeSessionId
)
{
    /// <summary>True when the iOS deletion flow can ask this account to reauthorize with Apple.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AppleNativeRevocationAvailable { get; init; }
}
