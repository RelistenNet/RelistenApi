using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>
/// Shared boundaries for recording bylines and role-free aliases. Recognizing an action does
/// not establish a recording role: unsupported actions keep their literal wording as Other.
/// Names and attachments remain the actor parser's responsibility.
/// </summary>
internal static class RecordingBylineGrammar
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private const string Verb =
        @"(?:recorded|taped|transferred|remastered|mastered|uploaded|collected|matrixed|mixed|edited|engineered|tracked|seeded|posted|provided|supplied|renamed|tagged)";
    private const string Action =
        @"(?:" + Verb + @"|recording|transfer|mastering|matrix|mixing|editing)";
    private const string Modifier = @"(?:[\p{L}]+ly\s+)?";
    private const string Operation = Modifier + Action;
    private const string VerbOperation = Modifier + Verb;
    private const string Coordination = @"(?:\s*,\s*(?:(?:and|&)\s+)?|\s+(?:and|&)\s+)";

    // Adjacent verbs occur in real bylines ("recorded transferred and mastered"). Nouns
    // require coordination: a "recording supplied by Pat" does not say Pat recorded it.
    private const string Actions =
        @"(?:"
        + VerbOperation
        + @"(?:"
        + Coordination
        + Operation
        + @"|\s+"
        + VerbOperation
        + @"){0,7}|"
        + Operation
        + @"(?:"
        + Coordination
        + Operation
        + @"){0,7})";

    // A tool qualifier cannot swallow another action's actor: "recorded using microphones
    // supplied by Pat" credits Pat for supplying, not recording.
    private const string ToolQualifier =
        @"(?:\s+using\s+(?:(?!\b(?:" + Action + @"|by|via)\b)[^;,]){1,100}?)?";
    private const string Header =
        @"(?:"
        + Actions
        + ToolQualifier
        + @"\s+by\b|(?:tapers?|transferrers?|recording\s+engineer|mastering\s+engineer|matrix\s+builder|uploader|collector|musicians?)\b(?=\s*:))";

    internal static readonly Regex Credits = new(
        @"\b(?<role>"
            + Header
            + @")(?:\s*:\s*|\s+)(?<name>.+?)(?=;(?![^()]*\))|$|\s+(?:"
            + Header
            + @"|(?:from|using|via|with|included)\b|please\s+contact\b|thanks\s+to\b|in\s+(?:Wavelab|Adobe\s+Audition|Sound\s+Forge)\b|\d{1,4}[/.-]\d{1,2}[/.-]\d{1,4}\b|(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2},?\s+\d{4}\b))",
        Options,
        Timeout
    );

    internal static readonly Regex LeadingByline = new(
        @"^" + Header + @"(?:\s*:\s*|\s+)(?<actor>.+)$",
        Options,
        Timeout
    );

    internal static string TrimActorList(string text) =>
        Regex.Replace(text, @"(?:\s*(?:,|&|\band\b))+\s*$", "", Options, Timeout);

    internal static bool IsNonActorFragment(string text) =>
        Regex.IsMatch(
            text,
            @"^(?:"
                + Action
                + @"|FOB|DFC)$|^(?:(?:bottom|top)\s+of\s+(?:the\s+)?hill\b|on\s+the\s+grass\b|stand\s+\d)",
            Options,
            Timeout
        );

    internal static IEnumerable<RecordingCreditRole> Roles(string wording)
    {
        if (Regex.IsMatch(wording, @"^(?:recording|mastering)\s+engineer\b", Options, Timeout))
        {
            yield return wording.StartsWith("mastering", StringComparison.OrdinalIgnoreCase)
                ? RecordingCreditRole.MasteringEngineer
                : RecordingCreditRole.RecordingEngineer;
            yield break;
        }

        var roles = new HashSet<RecordingCreditRole>();
        // Do not classify software names in an optional "using ... by" qualifier as actions.
        var actions = Regex.Split(wording, @"\s+using\s+", Options, Timeout)[0];
        foreach (Match action in Regex.Matches(actions, @"\b" + Action + @"\b", Options, Timeout))
        {
            var role = action.Value.ToLowerInvariant() switch
            {
                "recorded" or "recording" or "taped" => RecordingCreditRole.Taper,
                "transferred" or "transfer" => RecordingCreditRole.Transferrer,
                "mastered" or "remastered" or "mastering" => RecordingCreditRole.MasteringEngineer,
                "matrixed" or "matrix" => RecordingCreditRole.MatrixBuilder,
                "uploaded" => RecordingCreditRole.Uploader,
                "collected" => RecordingCreditRole.Collector,
                _ => RecordingCreditRole.Other,
            };
            if (roles.Add(role))
            {
                yield return role;
            }
        }
        if (roles.Count > 0)
        {
            yield break;
        }

        yield return wording.ToLowerInvariant() switch
        {
            var label when label.StartsWith("taper") => RecordingCreditRole.Taper,
            var label when label.StartsWith("transferrer") => RecordingCreditRole.Transferrer,
            var label when label.StartsWith("matrix") => RecordingCreditRole.MatrixBuilder,
            var label when label.StartsWith("uploader") => RecordingCreditRole.Uploader,
            var label when label.StartsWith("collector") => RecordingCreditRole.Collector,
            var label when label.StartsWith("musician") => RecordingCreditRole.Musician,
            _ => RecordingCreditRole.Other,
        };
    }
}
