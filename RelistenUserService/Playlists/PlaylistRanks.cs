namespace RelistenUserService.Playlists;

/// <summary>
/// Default base-62 generateKeyBetween from Rocicorp fractional-indexing 4.0.0 (CC0).
/// https://github.com/rocicorp/fractional-indexing/blob/v4.0.0/src/index.js
/// Inputs are server-generated keys, never client ranks. Ordering is ordinal/SQL COLLATE "C".
/// </summary>
public static class PlaylistRanks
{
    public const int MaximumLength = 48;
    private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const string Heads = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private static readonly string Smallest = "A" + new string('0', 26);

    public static string Between(string? lower, string? upper)
    {
        if (lower is not null && upper is not null && string.CompareOrdinal(lower, upper) >= 0)
        {
            throw new ArgumentException("Rank bounds must be strictly ordered.");
        }

        if (lower is null)
        {
            if (upper is null)
            {
                return "a0";
            }
            var integer = IntegerPart(upper);
            if (integer == Smallest)
            {
                return integer + Midpoint("", upper[integer.Length..]);
            }
            return string.CompareOrdinal(integer, upper) < 0 ? integer : StepInteger(integer, -1)!;
        }

        var lowerInteger = IntegerPart(lower);
        var lowerFraction = lower[lowerInteger.Length..];
        var increment = StepInteger(lowerInteger, 1);
        if (upper is null)
        {
            return increment ?? lowerInteger + Midpoint(lowerFraction, null);
        }

        var upperInteger = IntegerPart(upper);
        if (lowerInteger == upperInteger)
        {
            return lowerInteger + Midpoint(lowerFraction, upper[upperInteger.Length..]);
        }
        return increment is not null && string.CompareOrdinal(increment, upper) < 0
            ? increment
            : lowerInteger + Midpoint(lowerFraction, null);
    }

    // Balanced allocation avoids growing a rank once per entry when inserting a long contiguous run.
    public static string[] Between(string? lower, string? upper, int count)
    {
        if (count == 0)
        {
            return [];
        }
        if (lower is null || upper is null)
        {
            var result = new string[count];
            for (var i = 0; i < count; i++)
            {
                var key = Between(lower, upper);
                result[upper is null ? i : count - 1 - i] = key;
                if (upper is null)
                {
                    lower = key;
                }
                else
                {
                    upper = key;
                }
            }
            return result;
        }

        var middle = Between(lower, upper);
        var leftCount = count / 2;
        return
        [
            .. Between(lower, middle, leftCount),
            middle,
            .. Between(middle, upper, count - leftCount - 1),
        ];
    }

    private static int IntegerLength(char head) => head >= 'a' ? head - 'a' + 2 : 'Z' - head + 2;

    private static string IntegerPart(string key) => key[..IntegerLength(key[0])];

    private static string? StepInteger(string integer, int direction)
    {
        var chars = integer[1..].ToCharArray();
        for (var index = chars.Length - 1; index >= 0; index--)
        {
            var digit = Digits.IndexOf(chars[index]) + direction;
            if (digit >= 0 && digit < Digits.Length)
            {
                chars[index] = Digits[digit];
                return integer[0] + new string(chars);
            }
            chars[index] = direction == 1 ? '0' : 'z';
        }

        var headIndex = Heads.IndexOf(integer[0]) + direction;
        if (headIndex < 0 || headIndex == Heads.Length)
        {
            return null;
        }
        return Heads[headIndex]
            + new string(direction == 1 ? '0' : 'z', IntegerLength(Heads[headIndex]) - 1);
    }

    private static string Midpoint(string lower, string? upper)
    {
        if (upper is not null)
        {
            var common = 0;
            while ((common < lower.Length ? lower[common] : '0') == upper[common])
            {
                common++;
            }
            if (common > 0)
            {
                return upper[..common]
                    + Midpoint(lower.Length > common ? lower[common..] : "", upper[common..]);
            }
        }

        var low = lower.Length == 0 ? 0 : Digits.IndexOf(lower[0]);
        var high = upper is null ? Digits.Length : Digits.IndexOf(upper[0]);
        if (high - low > 1)
        {
            return Digits[(low + high + 1) / 2].ToString();
        }
        return upper is { Length: > 1 }
            ? upper[..1]
            : Digits[low] + Midpoint(lower.Length == 0 ? "" : lower[1..], null);
    }
}
