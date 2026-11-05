using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Identifiers;

namespace Relisten.Accounts.Contracts.Identifiers;

/// <summary>Application-owned UUIDv7 identity stored and compared as its complete typed string.</summary>
public interface IAccountId<TSelf>
    where TSelf : struct, IAccountId<TSelf>
{
    static abstract string Prefix { get; }
    static abstract TSelf Parse(string value);
    static abstract TSelf New();
    string Value { get; }
}

internal static class AccountIdValidation
{
    public static bool IsValid(string? value, string prefix) =>
        UuidTypeId.TryParse(value, prefix, out var uuid) && uuid.Version == 7;
}

/// <summary>Reads canonical typed strings only. Raw UUID credentials are deliberately not accepted.</summary>
public sealed class AccountIdJsonConverter<T> : JsonConverter<T>
    where T : struct, IAccountId<T>
{
    public override T Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected a {T.Prefix}_ typed ID.");
        }
        try
        {
            return T.Parse(reader.GetString()!);
        }
        catch (FormatException exception)
        {
            throw new JsonException(exception.Message, exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

[JsonConverter(typeof(AccountIdJsonConverter<UserId>))]
public readonly record struct UserId : IAccountId<UserId>
{
    private readonly string? _value;
    public string Value =>
        _value ?? throw new InvalidOperationException("An uninitialized user_ ID has no value.");
    public static string Prefix => "user";

    private UserId(string value) => _value = value;

    public static UserId Parse(string value) =>
        TryParse(value, out var id)
            ? id
            : throw new FormatException("Expected a canonical user_ UUIDv7 ID.");

    public static bool TryParse(string? value, out UserId id)
    {
        var valid = AccountIdValidation.IsValid(value, Prefix);
        id = valid ? new(value!) : default;
        return valid;
    }

    public static UserId FromUuid(Guid value) => Parse(UuidTypeId.Format(Prefix, value));

    public static UserId New() => FromUuid(Guid.CreateVersion7());

    public override string ToString() => Value;
}

[JsonConverter(typeof(AccountIdJsonConverter<SessionId>))]
public readonly record struct SessionId : IAccountId<SessionId>
{
    private readonly string? _value;
    public string Value =>
        _value ?? throw new InvalidOperationException("An uninitialized session_ ID has no value.");
    public bool IsEmpty => _value is null;
    public static string Prefix => "session";

    private SessionId(string value) => _value = value;

    public static SessionId Parse(string value) =>
        TryParse(value, out var id)
            ? id
            : throw new FormatException("Expected a canonical session_ UUIDv7 ID.");

    public static bool TryParse(string? value, out SessionId id)
    {
        var valid = AccountIdValidation.IsValid(value, Prefix);
        id = valid ? new(value!) : default;
        return valid;
    }

    public static SessionId FromUuid(Guid value) => Parse(UuidTypeId.Format(Prefix, value));

    public static SessionId New() => FromUuid(Guid.CreateVersion7());

    public override string ToString() => Value;
}

[JsonConverter(typeof(AccountIdJsonConverter<FavoriteId>))]
public readonly record struct FavoriteId : IAccountId<FavoriteId>
{
    private readonly string? _value;
    public string Value =>
        _value
        ?? throw new InvalidOperationException("An uninitialized favorite_ ID has no value.");
    public bool IsEmpty => _value is null;
    public static string Prefix => "favorite";

    private FavoriteId(string value) => _value = value;

    public static FavoriteId Parse(string value) =>
        TryParse(value, out var id)
            ? id
            : throw new FormatException("Expected a canonical favorite_ UUIDv7 ID.");

    public static bool TryParse(string? value, out FavoriteId id)
    {
        var valid = AccountIdValidation.IsValid(value, Prefix);
        id = valid ? new(value!) : default;
        return valid;
    }

    public static FavoriteId FromUuid(Guid value) => Parse(UuidTypeId.Format(Prefix, value));

    public static FavoriteId New() => FromUuid(Guid.CreateVersion7());

    public override string ToString() => Value;
}

[JsonConverter(typeof(AccountIdJsonConverter<MutationId>))]
public readonly record struct MutationId : IAccountId<MutationId>
{
    private readonly string? _value;
    public string Value =>
        _value
        ?? throw new InvalidOperationException("An uninitialized mutation_ ID has no value.");
    public bool IsEmpty => _value is null;
    public static string Prefix => "mutation";

    private MutationId(string value) => _value = value;

    public static MutationId Parse(string value) =>
        TryParse(value, out var id)
            ? id
            : throw new FormatException("Expected a canonical mutation_ UUIDv7 ID.");

    public static bool TryParse(string? value, out MutationId id)
    {
        var valid = AccountIdValidation.IsValid(value, Prefix);
        id = valid ? new(value!) : default;
        return valid;
    }

    public static MutationId FromUuid(Guid value) => Parse(UuidTypeId.Format(Prefix, value));

    public static MutationId New() => FromUuid(Guid.CreateVersion7());

    public override string ToString() => Value;
}

[JsonConverter(typeof(AccountIdJsonConverter<LibraryChangeId>))]
public readonly record struct LibraryChangeId : IAccountId<LibraryChangeId>
{
    private readonly string? _value;
    public string Value =>
        _value
        ?? throw new InvalidOperationException("An uninitialized library_change_ ID has no value.");
    public static string Prefix => "library_change";

    private LibraryChangeId(string value) => _value = value;

    public static LibraryChangeId Parse(string value) =>
        TryParse(value, out var id)
            ? id
            : throw new FormatException("Expected a canonical library_change_ UUIDv7 ID.");

    public static bool TryParse(string? value, out LibraryChangeId id)
    {
        var valid = AccountIdValidation.IsValid(value, Prefix);
        id = valid ? new(value!) : default;
        return valid;
    }

    public static LibraryChangeId FromUuid(Guid value) => Parse(UuidTypeId.Format(Prefix, value));

    public static LibraryChangeId New() => FromUuid(Guid.CreateVersion7());

    public override string ToString() => Value;
}

[JsonConverter(typeof(AccountIdJsonConverter<CommandId>))]
public readonly record struct CommandId : IAccountId<CommandId>
{
    private readonly string? _value;
    public string Value =>
        _value ?? throw new InvalidOperationException("An uninitialized command_ ID has no value.");
    public bool IsEmpty => _value is null;
    public static string Prefix => "command";

    private CommandId(string value) => _value = value;

    public static CommandId Parse(string value) =>
        TryParse(value, out var id)
            ? id
            : throw new FormatException("Expected a canonical command_ UUIDv7 ID.");

    public static bool TryParse(string? value, out CommandId id)
    {
        var valid = AccountIdValidation.IsValid(value, Prefix);
        id = valid ? new(value!) : default;
        return valid;
    }

    public static CommandId FromUuid(Guid value) => Parse(UuidTypeId.Format(Prefix, value));

    public static CommandId New() => FromUuid(Guid.CreateVersion7());

    public override string ToString() => Value;
}
