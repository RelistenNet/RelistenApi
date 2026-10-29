using System.Buffers.Binary;

namespace Relisten.Identifiers;

/// <summary>
/// Losslessly encodes UUID bits in the 26-character lowercase TypeID suffix.
/// Catalog UUIDs can be hash-derived, so parsing does not impose UUID version or variant bits.
/// Host projects own JSON, route validation and database binding.
/// </summary>
public static class UuidTypeId
{
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    public static string Encode(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        var number = BinaryPrimitives.ReadUInt128BigEndian(bytes);
        Span<char> suffix = stackalloc char[26];
        for (var i = suffix.Length - 1; i >= 0; i--)
        {
            suffix[i] = Alphabet[(int)(number & 31)];
            number >>= 5;
        }

        return new string(suffix);
    }

    public static Guid Decode(string suffix)
    {
        if (!TryDecode(suffix, out var value))
        {
            throw new FormatException("Expected a lowercase 128-bit TypeID suffix.");
        }

        return value;
    }

    /// <summary>Formats a UUID with its resource prefix, excluding the final underscore.</summary>
    public static string Format(string prefix, Guid value) => $"{prefix}_{Encode(value)}";

    /// <summary>Parses only the expected resource kind, including multi-underscore prefixes.</summary>
    public static Guid Parse(string value, string expectedPrefix)
    {
        if (!TryParse(value, expectedPrefix, out var uuid))
        {
            throw new FormatException($"Expected a {expectedPrefix} UUID-backed typed ID.");
        }

        return uuid;
    }

    public static bool TryParse(string? value, string expectedPrefix, out Guid uuid)
    {
        uuid = default;
        return value is not null
            && value.Length == expectedPrefix.Length + 27
            && value.StartsWith(expectedPrefix, StringComparison.Ordinal)
            && value[expectedPrefix.Length] == '_'
            && TryDecode(value.AsSpan(expectedPrefix.Length + 1), out uuid);
    }

    private static bool TryDecode(ReadOnlySpan<char> suffix, out Guid value)
    {
        value = default;
        if (suffix.Length != 26 || suffix[0] < '0' || suffix[0] > '7')
        {
            return false;
        }

        UInt128 number = 0;
        foreach (var character in suffix)
        {
            var digit = Alphabet.IndexOf(character);
            if (digit < 0)
            {
                return false;
            }

            number = (number << 5) | (uint)digit;
        }

        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(bytes, number);
        value = new Guid(bytes, bigEndian: true);
        return true;
    }
}
