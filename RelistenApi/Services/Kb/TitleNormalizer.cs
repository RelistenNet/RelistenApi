using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Relisten.Kb;

/// The normalisation every title lookup uses (string-theory docs/matching.md T1 step 2):
/// accents folded, lowercase, `&` → and, apostrophes dropped, other punctuation to spaces,
/// and a small token map (st → saint). kb.song_aliases.normalized holds
/// its output, so changing it means bumping Version and recomputing that column.
public static class TitleNormalizer
{
    public const string Version = "title-norm/1";

    private static readonly Dictionary<string, string> Tokens = new()
    {
        ["st"] = "saint",
        ["mt"] = "mount",
        ["n"] = "and",
        ["w"] = "with",
    };

    public static string Normalize(string text)
    {
        var folded = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            switch (c)
            {
                case '\'' or '’' or '‘' or '`':
                    break;
                case '&' or '+':
                    folded.Append(" and ");
                    break;
                default:
                    folded.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
                    break;
            }
        }

        return string.Join(
            ' ',
            folded
                .ToString()
                .Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
                .Select(t => Tokens.GetValueOrDefault(t, t))
        );
    }

    /// A URL slug for a display name. Slugs are display strings, never ids.
    public static string Slug(string name)
    {
        var slug = Normalize(name).Replace(' ', '-');
        return slug.Length == 0 ? "untitled" : slug;
    }
}
