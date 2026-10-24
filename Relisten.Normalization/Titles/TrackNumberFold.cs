using System;
using System.Text.RegularExpressions;

namespace Relisten.Normalization.Titles;

/// Tapers often number tracks across a whole show while Relisten's positions restart per disc,
/// so a leading number can survive parsing: "10 waiting for the punchline". Whether a number
/// is part of the name ("46 Days", "18 Wheels of Love") can't be told from one title, but the
/// artist's own spellings tell it: if what follows the number is itself a known spelling, the
/// number was a track number.
public static partial class TrackNumberFold
{
    public static string Fold(string key, Func<string, bool> isKnown)
    {
        var match = NumberPrefix().Match(key);
        return match.Success && isKnown(match.Groups[1].Value) ? match.Groups[1].Value : key;
    }

    [GeneratedRegex(@"^\d{1,3} (.+)$")]
    private static partial Regex NumberPrefix();
}
