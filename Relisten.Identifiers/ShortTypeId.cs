namespace Relisten.Identifiers;

/// <summary>
/// Validates the existing eight-character normalized venue/song format without
/// translating those independent identities into UUID-backed catalog identities.
/// </summary>
public static class ShortTypeId
{
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    public static bool IsValid(string? value, string expectedPrefix)
    {
        if (
            value is null
            || value.Length != expectedPrefix.Length + 9
            || !value.StartsWith(expectedPrefix, StringComparison.Ordinal)
            || value[expectedPrefix.Length] != '_'
        )
        {
            return false;
        }

        foreach (var character in value.AsSpan(expectedPrefix.Length + 1))
        {
            if (!Alphabet.Contains(character))
            {
                return false;
            }
        }

        return true;
    }
}
