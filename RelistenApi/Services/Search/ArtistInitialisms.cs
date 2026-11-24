using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace Relisten.Search;

internal static class ArtistInitialisms
{
    private const int MinLength = 3;
    private const int MaxLength = 6;
    internal const int MinimumSongShows = 25;

    // Keep frequent short English words literal even when the catalog has no
    // exact name or title with that spelling.
    private static readonly FrozenSet<string> CommonWords =
        "air all am an and any are as ask at away back bad be best big but by can car cat come cut day do down did end eye fan far feel find fire for free fun get go god good got had has hat have he her here him his hit home hop hot how ice in is it job keep key kid last let lie like live look lost love low man may me met mom more most move my near new no nor not now of off old on one or our out own pay pop put ran rat red run say see set she show sky so some song sun take the them then they this time to too top try two up us use want war was way we well went were what when who why will with word work yes yet you your"
            .Split(' ')
            .ToFrozenSet(StringComparer.Ordinal);

    // Ordinary shorthand with a meaning outside an artist's name.
    private static readonly FrozenSet<string> CommonInitialisms = new[]
    {
        "bff",
        "cats",
        "dnb",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static FrozenDictionary<string, SearchDocument> Build(
        IEnumerable<SearchDocument> artists,
        IEnumerable<string> catalogTerms,
        IReadOnlySet<string>? reservedInitialismKeys
    )
    {
        var guesses = artists
            .Select(artist => (Key: TitleInitials(artist.Name), Artist: artist))
            .Where(guess => IsShortWord(guess.Key))
            .GroupBy(guess => guess.Key, StringComparer.Ordinal)
            .Where(group => group.Count() == 1 && group.First().Artist.Popularity > 0)
            .ToDictionary(
                group => group.Key,
                group => group.First().Artist,
                StringComparer.Ordinal
            );

        // A whole name or title is stronger literal evidence than an initialism.
        // Imported tracks can contain band abbreviations inside a longer title.
        foreach (var term in catalogTerms)
        {
            guesses.Remove(term);
        }

        return guesses
            .Where(guess =>
                !IsCommonWord(guess.Key)
                && !CommonInitialisms.Contains(guess.Key)
                && reservedInitialismKeys?.Contains(guess.Key) != true
            )
            .ToFrozenDictionary(StringComparer.Ordinal);
    }

    public static bool IsCommonWord(string word) => CommonWords.Contains(word);

    public static void ReserveName(HashSet<string> names, string foldedName)
    {
        if (IsShortWord(foldedName))
        {
            names.Add(foldedName);
        }
    }

    private static bool IsShortWord(string word) =>
        word.Length is >= MinLength and <= MaxLength && word.All(char.IsAsciiLetterOrDigit);

    // Dropping the opening article and the connector "and the" keeps names
    // recognizable while preserving an internal article in a name such as
    // "Donna the Buffalo".
    internal static string TitleInitials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(
            words
                .Where(
                    (word, index) =>
                        word != "and"
                        && !(word == "the" && (index == 0 || words[index - 1] == "and"))
                )
                .Select(word => word[0])
        );
    }
}
