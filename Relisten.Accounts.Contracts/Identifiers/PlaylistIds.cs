using System.Text.Json.Serialization;
using Relisten.Identifiers;

namespace Relisten.Accounts.Contracts.Identifiers;

[JsonConverter(typeof(AccountIdJsonConverter<PlaylistId>))]
public readonly record struct PlaylistId : IAccountId<PlaylistId>
{
    private readonly string? _value;
    public string Value =>
        _value
        ?? throw new InvalidOperationException("An uninitialized playlist_ ID has no value.");
    public bool IsEmpty => _value is null;
    public static string Prefix => "playlist";

    private PlaylistId(string value) => _value = value;

    public static PlaylistId Parse(string value) =>
        TryParse(value, out var id)
            ? id
            : throw new FormatException("Expected a canonical playlist_ UUIDv7 ID.");

    public static bool TryParse(string? value, out PlaylistId id)
    {
        var valid = AccountIdValidation.IsValid(value, Prefix);
        id = valid ? new(value!) : default;
        return valid;
    }

    public static PlaylistId FromUuid(Guid value) => Parse(UuidTypeId.Format(Prefix, value));

    public static PlaylistId New() => FromUuid(Guid.CreateVersion7());

    public override string ToString() => Value;
}

[JsonConverter(typeof(AccountIdJsonConverter<PlaylistSegmentId>))]
public readonly record struct PlaylistSegmentId : IAccountId<PlaylistSegmentId>
{
    private readonly string? _value;
    public string Value =>
        _value
        ?? throw new InvalidOperationException(
            "An uninitialized playlist_segment_ ID has no value."
        );
    public bool IsEmpty => _value is null;
    public static string Prefix => "playlist_segment";

    private PlaylistSegmentId(string value) => _value = value;

    public static PlaylistSegmentId Parse(string value) =>
        TryParse(value, out var id)
            ? id
            : throw new FormatException("Expected a canonical playlist_segment_ UUIDv7 ID.");

    public static bool TryParse(string? value, out PlaylistSegmentId id)
    {
        var valid = AccountIdValidation.IsValid(value, Prefix);
        id = valid ? new(value!) : default;
        return valid;
    }

    public static PlaylistSegmentId FromUuid(Guid value) => Parse(UuidTypeId.Format(Prefix, value));

    public static PlaylistSegmentId New() => FromUuid(Guid.CreateVersion7());

    public override string ToString() => Value;
}

[JsonConverter(typeof(AccountIdJsonConverter<PlaylistEntryId>))]
public readonly record struct PlaylistEntryId : IAccountId<PlaylistEntryId>
{
    private readonly string? _value;
    public string Value =>
        _value
        ?? throw new InvalidOperationException("An uninitialized playlist_entry_ ID has no value.");
    public bool IsEmpty => _value is null;
    public static string Prefix => "playlist_entry";

    private PlaylistEntryId(string value) => _value = value;

    public static PlaylistEntryId Parse(string value) =>
        TryParse(value, out var id)
            ? id
            : throw new FormatException("Expected a canonical playlist_entry_ UUIDv7 ID.");

    public static bool TryParse(string? value, out PlaylistEntryId id)
    {
        var valid = AccountIdValidation.IsValid(value, Prefix);
        id = valid ? new(value!) : default;
        return valid;
    }

    public static PlaylistEntryId FromUuid(Guid value) => Parse(UuidTypeId.Format(Prefix, value));

    public static PlaylistEntryId New() => FromUuid(Guid.CreateVersion7());

    public override string ToString() => Value;
}

[JsonConverter(typeof(AccountIdJsonConverter<PlaylistOperationId>))]
public readonly record struct PlaylistOperationId : IAccountId<PlaylistOperationId>
{
    private readonly string? _value;
    public string Value =>
        _value
        ?? throw new InvalidOperationException(
            "An uninitialized playlist_operation_ ID has no value."
        );
    public bool IsEmpty => _value is null;
    public static string Prefix => "playlist_operation";

    private PlaylistOperationId(string value) => _value = value;

    public static PlaylistOperationId Parse(string value) =>
        TryParse(value, out var id)
            ? id
            : throw new FormatException("Expected a canonical playlist_operation_ UUIDv7 ID.");

    public static bool TryParse(string? value, out PlaylistOperationId id)
    {
        var valid = AccountIdValidation.IsValid(value, Prefix);
        id = valid ? new(value!) : default;
        return valid;
    }

    public static PlaylistOperationId FromUuid(Guid value) =>
        Parse(UuidTypeId.Format(Prefix, value));

    public static PlaylistOperationId New() => FromUuid(Guid.CreateVersion7());

    public override string ToString() => Value;
}
