using System;
using System.Data;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dapper;

namespace Relisten.Normalization;

/// The public id of a venue, song, or artist/venue relationship: a full-name prefix
/// and 8 random lowercase Crockford base32 characters, "venue_k3m9x2qa". Names and slugs
/// change; these stored identities do not. Artist/venue pairs keep their minted identity
/// across count refreshes and zero counts. 40 bits leave a collision about once in ten
/// million mints at our sizes. The venue/song writers reject a taken id, as does the
/// artist/venue refresh in SQL. Only conflicts on a relationship's natural pair are benign.
/// An id collision fails the write, so a retry mints fresh ids rather than landing on the
/// row that holds the id.
///
/// Each kind is its own type, so a song's id can't stand in for a venue's, and a value is
/// checked against its prefix when it is made. The database and Temporal see the string.
public interface INormId<TSelf>
    where TSelf : struct, INormId<TSelf>
{
    static abstract string Prefix { get; }

    string Value { get; }

    /// Wraps a stored or received id; throws when it isn't one of this kind.
    static abstract TSelf Parse(string value);
}

public static partial class NormIds
{
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    /// A new random id of this kind.
    public static T New<T>()
        where T : struct, INormId<T> =>
        T.Parse($"{T.Prefix}_{RandomNumberGenerator.GetString(Alphabet, 8)}");

    internal static string Checked<T>(string value)
        where T : struct, INormId<T>
    {
        if (
            value.Length != T.Prefix.Length + 9
            || !value.StartsWith(T.Prefix + "_", StringComparison.Ordinal)
            || !Shape().IsMatch(value.AsSpan(T.Prefix.Length + 1))
        )
        {
            throw new FormatException($"'{value}' is not a {T.Prefix} id");
        }

        return value;
    }

    [GeneratedRegex("^[0-9a-hjkmnp-tv-z]{8}$")]
    private static partial Regex Shape();

    // Dapper reads and binds the ids as text wherever they appear in a query: single
    // parameters, scalars, tuple and record members (nullable ones too). Array parameters pass
    // .Value, as Npgsql binds arrays itself. A module initializer, so every query sees them,
    // whichever entrypoint loads the library.
    //
    // To read a single nullable id (QuerySingleOrDefaultAsync<VenueId?>), query string? and
    // Parse it: for a Nullable<T> scalar Dapper passes DBNull to the handler, which must
    // return a T.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void RegisterDapperHandlers()
    {
        SqlMapper.AddTypeHandler(new DapperHandler<VenueId>());
        SqlMapper.AddTypeHandler(new DapperHandler<SongId>());
        SqlMapper.AddTypeHandler(new DapperHandler<ArtistVenueId>());
    }

    private sealed class DapperHandler<T> : SqlMapper.TypeHandler<T>
        where T : struct, INormId<T>
    {
        public override void SetValue(IDbDataParameter parameter, T value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = value.Value;
        }

        public override T Parse(object value) =>
            value is string id
                ? T.Parse(id)
                : throw new InvalidCastException(
                    $"A NULL {T.Prefix} id: read a single nullable id as string? and Parse it"
                );
    }

    internal sealed class JsonConverter<T> : System.Text.Json.Serialization.JsonConverter<T>
        where T : struct, INormId<T>
    {
        public override T Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options
        ) =>
            reader.GetString() is { } id
                ? T.Parse(id)
                : throw new JsonException($"A {T.Prefix} id can't be null here");

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}

[JsonConverter(typeof(NormIds.JsonConverter<VenueId>))]
public readonly record struct VenueId : INormId<VenueId>, IComparable<VenueId>
{
    private VenueId(string value) => Value = value;

    public static string Prefix => "venue";

    public string Value { get; }

    public static VenueId Parse(string value) => new(NormIds.Checked<VenueId>(value));

    public static VenueId New() => NormIds.New<VenueId>();

    public int CompareTo(VenueId other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;
}

[JsonConverter(typeof(NormIds.JsonConverter<SongId>))]
public readonly record struct SongId : INormId<SongId>, IComparable<SongId>
{
    private SongId(string value) => Value = value;

    public static string Prefix => "song";

    public string Value { get; }

    public static SongId Parse(string value) => new(NormIds.Checked<SongId>(value));

    public static SongId New() => NormIds.New<SongId>();

    public int CompareTo(SongId other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;
}

[JsonConverter(typeof(NormIds.JsonConverter<ArtistVenueId>))]
public readonly record struct ArtistVenueId : INormId<ArtistVenueId>, IComparable<ArtistVenueId>
{
    private ArtistVenueId(string value) => Value = value;

    public static string Prefix => "artist_venue";

    public string Value { get; }

    public static ArtistVenueId Parse(string value) => new(NormIds.Checked<ArtistVenueId>(value));

    public static ArtistVenueId New() => NormIds.New<ArtistVenueId>();

    public int CompareTo(ArtistVenueId other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;
}
