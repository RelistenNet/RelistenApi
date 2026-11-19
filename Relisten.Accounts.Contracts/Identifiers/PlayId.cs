using System.Text.Json.Serialization;
using Relisten.Identifiers;

namespace Relisten.Accounts.Contracts.Identifiers;

[JsonConverter(typeof(AccountIdJsonConverter<PlayId>))]
public readonly record struct PlayId : IAccountId<PlayId>
{
    private readonly string? _value;
    public string Value =>
        _value ?? throw new InvalidOperationException("An uninitialized play_ ID has no value.");
    public bool IsEmpty => _value is null;
    public static string Prefix => "play";

    private PlayId(string value) => _value = value;

    public static PlayId Parse(string value) =>
        TryParse(value, out var id)
            ? id
            : throw new FormatException("Expected a canonical play_ UUIDv7 ID.");

    public static bool TryParse(string? value, out PlayId id)
    {
        var valid = AccountIdValidation.IsValid(value, Prefix);
        id = valid ? new(value!) : default;
        return valid;
    }

    public static PlayId FromUuid(Guid value) => Parse(UuidTypeId.Format(Prefix, value));

    public static PlayId New() => FromUuid(Guid.CreateVersion7());

    public override string ToString() => Value;
}
