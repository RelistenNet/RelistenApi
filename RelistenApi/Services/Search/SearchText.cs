using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Relisten.Search;

internal static partial class SearchText
{
    public static string Clean(string text) =>
        ControlCharacters()
            .Replace(WebUtility.HtmlDecode(HtmlTags().Replace(text, " ")), "")
            .Trim();

    public static string Fold(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var decomposed = text.Normalize(NormalizationForm.FormKD).ToLowerInvariant();
        var folded = Marks()
            .Replace(decomposed, "")
            .Replace("ß", "ss", StringComparison.Ordinal)
            .Replace('ς', 'σ')
            .Replace("&", " and ", StringComparison.Ordinal)
            .Replace("'", "", StringComparison.Ordinal)
            .Replace("’", "", StringComparison.Ordinal)
            .Replace("‘", "", StringComparison.Ordinal);
        return Separators().Replace(folded, " ").Trim();
    }

    public static string Join(params string[] parts) =>
        string.Join(
            ' ',
            parts.Select(Fold).Where(part => part.Length > 0).Distinct(StringComparer.Ordinal)
        );

    public static string RawTitle(string title) =>
        AudioSuffix().IsMatch(title)
            ? TrackNumber().Replace(AudioSuffix().Replace(title, "").Trim(), "")
            : title.Trim();

    [GeneratedRegex("<[^>]{1,300}>")]
    private static partial Regex HtmlTags();

    [GeneratedRegex("[\\x00-\\x08\\x0b\\x0c\\x0e-\\x1f]")]
    private static partial Regex ControlCharacters();

    [GeneratedRegex(@"\p{M}+")]
    private static partial Regex Marks();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex Separators();

    [GeneratedRegex(
        @"\.(?:mp3|flac|shn|wav|m4a|ogg)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex AudioSuffix();

    [GeneratedRegex(
        @"^(?:d\d+t\d+|\d{1,3})[\s._:-]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex TrackNumber();

    [GeneratedRegex(
        @"^\s*\((?:check|soundcheck)\)\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    public static partial Regex Soundcheck();

    [GeneratedRegex(@"(?:^|\.)([0-9]{4,9})(?=\.|$)")]
    public static partial Regex ArchiveNumber();

    [GeneratedRegex(@"\bmatrix\b|\.mtx\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex Matrix();
}
