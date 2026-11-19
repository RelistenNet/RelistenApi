using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace RelistenUserService.Playlists;

/// <summary>
/// The server's half of keeping guide descriptions to the allowed Markdown subset. See
/// "Accepting and sanitizing descriptions" in the guide content design doc.
/// </summary>
/// <remarks>
/// This only refuses. It never rewrites, because mobile's optimistic edits and receipt hashes
/// depend on the stored text matching the request. Clients still sanitize before rendering:
/// their renderers can read edge cases differently from this parser, and rows can predate a
/// rule. Parsing as CommonMark, rather than matching patterns, keeps code spans and escaped
/// characters from being mistaken for images or HTML.
/// </remarks>
public static partial class GuideMarkdownPolicy
{
    public const string HtmlRefusal = "description cannot contain HTML.";
    public const string ImageRefusal = "description cannot contain images.";
    public const string NestingRefusal = "description is nested too deeply.";
    public const string LinkRefusal =
        "description links must use https: or a #entry=…&t=… or #t=… timestamp.";

    private static readonly MarkdownPipeline CommonMark = new MarkdownPipelineBuilder().Build();

    /// <summary>Why the description is refused, or null when it is acceptable.</summary>
    /// <remarks>
    /// Reference links arrive as <see cref="LinkInline"/> with their definition's URL, so
    /// they are checked like inline links. An unused definition is never checked: it can't
    /// become a link, and prose such as <c>[Encore]: Brokedown</c> parses as one.
    /// </remarks>
    public static string? Violation(string description)
    {
        MarkdownDocument document;
        try
        {
            document = Markdown.Parse(description, CommonMark);
        }
        catch (ArgumentException)
        {
            // Markdig stops past its nesting limit, far beyond anything a guide needs.
            return NestingRefusal;
        }

        foreach (var node in document.Descendants())
        {
            var violation = node switch
            {
                HtmlBlock or HtmlInline => HtmlRefusal,
                LinkInline { IsImage: true } => ImageRefusal,
                LinkInline link when !IsAllowedDestination(link.Url) => LinkRefusal,
                AutolinkInline autolink
                    when autolink.IsEmail || !IsAllowedDestination(autolink.Url) => LinkRefusal,
                _ => null,
            };

            if (violation is not null)
            {
                return violation;
            }
        }

        return null;
    }

    /// <summary>
    /// An https URL with no whitespace or control characters, or a timestamp link. Mobile's
    /// <c>isAllowedGuideLink</c> applies the same rule. JavaScript's <c>\s</c> includes U+FEFF,
    /// which <c>char.IsWhiteSpace</c> doesn't, so it is listed here.
    /// </summary>
    private static bool IsAllowedDestination(string? url) =>
        url is not null
        && (IsHttps(url) || EntryTimestampLink().IsMatch(url) || OwnTimestampLink().IsMatch(url));

    private static bool IsHttps(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        && url.Length > "https://".Length
        && !url.Any(character =>
            char.IsWhiteSpace(character) || char.IsControl(character) || character == '﻿'
        );

    /// <summary>A timestamp in another entry, by entry ID. Allowed in any description.</summary>
    [GeneratedRegex(
        @"^#entry=playlist_entry_[0-9a-z]{26}&t=(?:[0-9]+:[0-5][0-9]:[0-5][0-9]|[0-9]+:[0-5][0-9])\z",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex EntryTimestampLink();

    /// <summary>
    /// A timestamp in the description's own track. Accepted in any description, because
    /// segments and text blocks render it as text rather than refusing the write.
    /// </summary>
    [GeneratedRegex(
        @"^#t=(?:[0-9]+:[0-5][0-9]:[0-5][0-9]|[0-9]+:[0-5][0-9])\z",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex OwnTimestampLink();
}
