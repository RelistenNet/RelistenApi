using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Relisten.Data;

namespace Relisten.Ingestion.Providers.TapersSection;

[JsonConverter(typeof(JsonStringEnumConverter<TapersSectionReason>))]
public enum TapersSectionReason
{
    [JsonStringEnumMemberName("missing_creator")]
    MissingCreator,

    [JsonStringEnumMemberName("ambiguous_creator")]
    AmbiguousCreator,

    [JsonStringEnumMemberName("unapproved_artist")]
    UnapprovedArtist,

    [JsonStringEnumMemberName("existing_artist")]
    ExistingArtist,

    [JsonStringEnumMemberName("multiple_performers")]
    MultiplePerformers,

    [JsonStringEnumMemberName("compilation")]
    Compilation,

    [JsonStringEnumMemberName("tribute_context")]
    TributeContext,

    [JsonStringEnumMemberName("missing_date")]
    MissingDate,

    [JsonStringEnumMemberName("conflicting_date")]
    ConflictingDate,

    [JsonStringEnumMemberName("ambiguous_date")]
    AmbiguousDate,

    [JsonStringEnumMemberName("no_vbr_mp3")]
    NoVbrMp3,

    [JsonStringEnumMemberName("upstream_unavailable")]
    UpstreamUnavailable,

    [JsonStringEnumMemberName("published_assignment_conflict")]
    PublishedAssignmentConflict,

    [JsonStringEnumMemberName("catalog_identity_missing")]
    CatalogIdentityMissing,

    [JsonStringEnumMemberName("invalid_metadata")]
    InvalidMetadata,
}

/// <summary>Reviewed intake vocabulary and current catalog matching, supplied by the database owner.</summary>
public sealed record TapersSectionCandidateIdentity(
    Guid CandidateId,
    bool Approved,
    string[] ReviewedCreatorKeys,
    Guid? ArtistUuid = null,
    bool ExistingArtist = false,
    bool AmbiguousIdentity = false
);

public sealed record TapersSectionPublishedAssignment(Guid ArtistUuid, string DisplayDate);

/// <summary>Identifies the exact retained field and rule that supplied an interpretation.</summary>
public sealed record TapersSectionEvidence(
    string Locator,
    string Value,
    string Rule,
    int? Start = null,
    int? Length = null
);

/// <summary>
/// A deterministic proposal, never authorization for a catalog write. Publication must recheck
/// approval, evidence freshness and the existing assignment under database row locks.
/// </summary>
public sealed record TapersSectionInterpretation(
    int RulesVersion,
    string[] CreatorKeys,
    Guid? CandidateId,
    string? DisplayDate,
    TapersSectionEvidence[] Evidence,
    TapersSectionReason[] Reasons
)
{
    public bool IsEligible => Reasons.Length == 0;
    public bool IsUnavailable =>
        Reasons.Contains(TapersSectionReason.UpstreamUnavailable)
        || Reasons.Contains(TapersSectionReason.NoVbrMp3);
}

/// <summary>
/// Applies the first-release attribution rules. Structured creators remain whole labels;
/// descriptions can prevent an unsafe assignment but cannot supply its performer or date.
/// </summary>
public sealed class TapersSectionEligibilityEvaluator
{
    public const int RulesVersion = 1;

    private static readonly Regex IsoDate = new(
        @"(?<!\d)(?<date>\d{4}-\d{2}-\d{2})(?!\d)",
        RegexOptions.CultureInvariant
    );
    private static readonly Regex WrittenDate = new(
        @"\b(?<date>(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2},?\s+\d{4}|\d{1,2}[ -](?:January|February|March|April|May|June|July|August|September|October|November|December)[ -]\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );
    private static readonly Regex Tribute = new(
        @"\b(?:tribute|memorial|in memory of|dear jerry|celebrat(?:ion|ing)\s+(?:of\s+)?(?:the\s+)?(?:life|music|jerry)|the songs of)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );
    private static readonly Regex Compilation = new(
        @"\b(?:compilations?|best of|remaster[- ]only|recordings from (?:multiple|various|several))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );
    private static readonly Regex MultiAct = new(
        @"\b(?:various artists|multi[- ](?:act|artist)|co[- ]headlin\w*|double bill|joint performance|collaboration)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );
    private static readonly Regex AmbiguousSet = new(
        @"\b(?:early\s*(?:and|/|&)\s*late|late\s*(?:and|/|&)\s*early)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    public TapersSectionInterpretation Evaluate(
        ArchiveObservationProjection observation,
        IReadOnlyList<TapersSectionCandidateIdentity> candidates,
        TapersSectionPublishedAssignment? published = null
    )
    {
        var root = ArchiveObservationProjector.ParseObject(observation.PayloadJson);
        var metadata = root["metadata"] as JObject ?? new JObject();
        var reasons = new List<TapersSectionReason>();
        var evidence = new List<TapersSectionEvidence>();
        var creators = Strings(metadata["creator"])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(ArtistRegistryService.NormalizeIdentityKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var candidate = MatchCreator(metadata["creator"], creators, candidates, reasons);

        CheckContext(metadata, reasons, evidence);
        CheckTitlePerformer(metadata, candidate, reasons, evidence);
        var selectedDate = SelectDate(metadata, observation.Item, reasons, evidence);
        if (observation.IsAvailabilityOnly)
        {
            reasons.Add(TapersSectionReason.UpstreamUnavailable);
        }
        else if (!Strings(metadata["mediatype"]).SequenceEqual(["audio"]))
        {
            reasons.Add(TapersSectionReason.InvalidMetadata);
        }

        CheckPlayableFiles(root, reasons);

        if (
            published != null
            && (
                candidate?.ArtistUuid != published.ArtistUuid
                || selectedDate != published.DisplayDate
            )
        )
        {
            reasons.Add(TapersSectionReason.PublishedAssignmentConflict);
        }

        return new TapersSectionInterpretation(
            RulesVersion,
            creators,
            candidate?.CandidateId,
            selectedDate,
            evidence.ToArray(),
            reasons.Distinct().ToArray()
        );
    }

    private static TapersSectionCandidateIdentity? MatchCreator(
        JToken? creator,
        string[] creatorKeys,
        IReadOnlyList<TapersSectionCandidateIdentity> candidates,
        List<TapersSectionReason> reasons
    )
    {
        if (creatorKeys.Length == 0)
        {
            reasons.Add(TapersSectionReason.MissingCreator);
            return null;
        }

        if (creatorKeys.Length != 1 || !HasOnlyStrings(creator))
        {
            reasons.Add(TapersSectionReason.AmbiguousCreator);
            return null;
        }

        var matches = candidates
            .Where(candidate =>
                candidate.ReviewedCreatorKeys.Any(key =>
                    ArtistRegistryService.NormalizeIdentityKey(key) == creatorKeys[0]
                )
            )
            .ToArray();
        if (matches.Length > 1)
        {
            reasons.Add(TapersSectionReason.AmbiguousCreator);
            return null;
        }

        if (matches.Length == 0)
        {
            reasons.Add(TapersSectionReason.UnapprovedArtist);
            return null;
        }

        var selected = matches[0];
        if (selected.AmbiguousIdentity)
        {
            reasons.Add(TapersSectionReason.AmbiguousCreator);
        }
        else if (selected.ExistingArtist)
        {
            reasons.Add(TapersSectionReason.ExistingArtist);
        }
        else if (!selected.Approved)
        {
            reasons.Add(TapersSectionReason.UnapprovedArtist);
        }

        return selected;
    }

    private static void CheckPlayableFiles(JObject root, List<TapersSectionReason> reasons)
    {
        var files = root["files"] as JArray;
        if (files?.Any(file => ArchiveObservationProjector.IsTrue(file["private"])) == true)
        {
            reasons.Add(TapersSectionReason.UpstreamUnavailable);
        }

        // Keep the legacy writer's exact VBR format selection and its missing-name/length
        // exclusions. Reject an empty or unparseable result before it can create a source.
        var playable =
            files
                ?.Where(file =>
                    file["format"]?.Type == JTokenType.String
                    && (string?)file["format"] == "VBR MP3"
                    && file["name"]?.Type == JTokenType.String
                    && !string.IsNullOrWhiteSpace((string?)file["name"])
                    && file["length"] != null
                    && file["length"]!.Type != JTokenType.Null
                )
                .ToArray()
            ?? [];
        if (playable.Length == 0 || playable.Any(file => !IsUsableDuration(file["length"]!)))
        {
            reasons.Add(TapersSectionReason.NoVbrMp3);
        }
    }

    private static bool IsUsableDuration(JToken token)
    {
        if (token.Type is not (JTokenType.String or JTokenType.Float or JTokenType.Integer))
        {
            return false;
        }

        var components = token.ToString().Split(':').Reverse().ToArray();
        double total = 0;
        for (var index = 0; index < components.Length; index++)
        {
            if (
                !double.TryParse(
                    components[index],
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out var value
                )
                || !double.IsFinite(value)
                || value < 0
            )
            {
                return false;
            }

            // Match the existing writer's duration calculation, including its hour behavior.
            var part = Math.Round(Math.Max(1, 60 * index) * value);
            total += part;
            if (part > int.MaxValue || total > int.MaxValue)
            {
                return false;
            }
        }

        return true;
    }

    private static void CheckContext(
        JObject metadata,
        List<TapersSectionReason> reasons,
        List<TapersSectionEvidence> evidence
    )
    {
        foreach (var field in new[] { "title", "description", "creator" })
        {
            var values = Strings(metadata[field]).ToArray();
            for (var index = 0; index < values.Length; index++)
            {
                var text = values[index];
                foreach (
                    var (pattern, reason, rule) in new[]
                    {
                        (Tribute, TapersSectionReason.TributeContext, "explicit_tribute_context"),
                        (
                            Compilation,
                            TapersSectionReason.Compilation,
                            "explicit_compilation_context"
                        ),
                        (
                            MultiAct,
                            TapersSectionReason.MultiplePerformers,
                            "explicit_multi_act_context"
                        ),
                        (
                            AmbiguousSet,
                            TapersSectionReason.AmbiguousDate,
                            "ambiguous_early_late_billing"
                        ),
                    }
                )
                {
                    var match = pattern.Match(text);
                    if (match.Success)
                    {
                        reasons.Add(reason);
                        evidence.Add(
                            new TapersSectionEvidence(
                                Locator(metadata[field], field, index),
                                match.Value,
                                rule,
                                match.Index,
                                match.Length
                            )
                        );
                    }
                }
            }
        }
    }

    private static void CheckTitlePerformer(
        JObject metadata,
        TapersSectionCandidateIdentity? candidate,
        List<TapersSectionReason> reasons,
        List<TapersSectionEvidence> evidence
    )
    {
        if (candidate == null)
        {
            return;
        }

        var titles = Strings(metadata["title"]).ToArray();
        for (var index = 0; index < titles.Length; index++)
        {
            var match = Regex.Match(
                titles[index],
                @"^(?<performer>.+?)\s+Live at\s+",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            );
            if (
                match.Success
                && !candidate.ReviewedCreatorKeys.Any(key =>
                    ArtistRegistryService.NormalizeIdentityKey(key)
                    == ArtistRegistryService.NormalizeIdentityKey(match.Groups["performer"].Value)
                )
            )
            {
                reasons.Add(TapersSectionReason.AmbiguousCreator);
                evidence.Add(
                    new TapersSectionEvidence(
                        Locator(metadata["title"], "title", index),
                        match.Groups["performer"].Value,
                        "title_performer_conflict",
                        match.Groups["performer"].Index,
                        match.Groups["performer"].Length
                    )
                );
            }
        }
    }

    private static string? SelectDate(
        JObject metadata,
        ArchiveItemId item,
        List<TapersSectionReason> reasons,
        List<TapersSectionEvidence> evidence
    )
    {
        var dates = new List<string>();
        var structured = Strings(metadata["date"]).ToArray();
        if (HasSuppliedValue(metadata["date"]) && !HasOnlyStrings(metadata["date"]))
        {
            reasons.Add(TapersSectionReason.AmbiguousDate);
        }
        for (var index = 0; index < structured.Length; index++)
        {
            // Archive's structured date may carry a midnight timestamp. Require the complete
            // calendar date; no current-year or two-digit-year inference is allowed.
            var value = structured[index];
            if (
                Regex.IsMatch(
                    value,
                    @"^\d{4}-\d{2}-\d{2}(?:T00:00:00(?:\.000)?Z)?$",
                    RegexOptions.CultureInvariant
                ) && TryDate(value[..10], out var date)
            )
            {
                var locator = Locator(metadata["date"], "date", index);
                dates.Add(date);
                evidence.Add(new TapersSectionEvidence(locator, value, "complete_structured_date"));
            }
            else
            {
                reasons.Add(TapersSectionReason.AmbiguousDate);
            }
        }

        var titleDates = new List<string>();
        var titles = Strings(metadata["title"]).ToArray();
        for (var index = 0; index < titles.Length; index++)
        {
            foreach (var date in ExplicitDates(titles[index]))
            {
                titleDates.Add(date.Date);
                evidence.Add(
                    new TapersSectionEvidence(
                        Locator(metadata["title"], "title", index),
                        date.Raw,
                        "full_year_title_date"
                    )
                );
            }
        }

        var identifierDates = ExplicitDates(item.Value)
            .Select(date => date.Date)
            .Distinct()
            .ToArray();
        foreach (var date in identifierDates)
        {
            evidence.Add(new TapersSectionEvidence("item_identifier", date, "identifier_iso_date"));
        }

        var descriptionDates = new List<string>();
        var descriptions = Strings(metadata["description"]).ToArray();
        for (var index = 0; index < descriptions.Length; index++)
        {
            var labels = Regex.Matches(
                descriptions[index],
                @"(?:^|[\r\n]|<br\s*/?>|<div>|<p>)\s*(?:performance date|show date|date)\s*:\s*(?<value>[^<\r\n]+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            );
            foreach (Match label in labels)
            {
                foreach (var date in ExplicitDates(label.Groups["value"].Value))
                {
                    descriptionDates.Add(date.Date);
                    evidence.Add(
                        new TapersSectionEvidence(
                            Locator(metadata["description"], "description", index),
                            date.Raw,
                            "explicit_description_date"
                        )
                    );
                }
            }
        }

        var allDates = dates
            .Concat(titleDates)
            .Concat(identifierDates)
            .Concat(descriptionDates)
            .Distinct()
            .ToArray();
        if (allDates.Length > 1)
        {
            reasons.Add(TapersSectionReason.ConflictingDate);
        }

        if (
            allDates.Length == 1
            && Strings(metadata["year"])
                .Any(year =>
                    Regex.IsMatch(year, @"^\d{4}$", RegexOptions.CultureInvariant)
                    && !allDates[0].StartsWith(year + "-", StringComparison.Ordinal)
                )
        )
        {
            reasons.Add(TapersSectionReason.ConflictingDate);
            evidence.Add(
                new TapersSectionEvidence(
                    "metadata.year",
                    metadata["year"]!.ToString(),
                    "structured_year_conflict"
                )
            );
        }

        if (dates.Count > 0)
        {
            return dates[0];
        }

        if (
            structured.Length == 0
            && !HasSuppliedValue(metadata["date"])
            && titleDates.Distinct().Count() == 1
            && titles.Any(title => IsPerformanceTitle(title, metadata["creator"]))
            && titles.All(title =>
                !Regex.IsMatch(
                    title,
                    @"\b(?:upload(?:ed)?|remaster(?:ed)?|published|release date)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                )
            )
        )
        {
            return titleDates[0];
        }

        reasons.Add(TapersSectionReason.MissingDate);
        return null;
    }

    private static bool IsPerformanceTitle(string title, JToken? creator)
    {
        if (
            Regex.IsMatch(
                title,
                @"\blive at\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        )
        {
            return true;
        }

        // Without "Live at", accept only a reviewed artist/date heading. An arbitrary
        // date elsewhere in an album, upload or descriptive title is not a performance date.
        var date = ExplicitDates(title).FirstOrDefault();
        if (date.Raw == null)
        {
            return false;
        }

        var offset = title.IndexOf(date.Raw, StringComparison.Ordinal);
        var prefix = title[..offset].Trim().TrimEnd('-', ':').Trim();
        var identity =
            prefix.Length > 0
                ? prefix
                : title[(offset + date.Raw.Length)..].Trim().TrimStart('-', ':').Trim();
        var key = ArtistRegistryService.NormalizeIdentityKey(identity);
        return Strings(creator)
            .Any(label => ArtistRegistryService.NormalizeIdentityKey(label) == key);
    }

    private static IEnumerable<(string Date, string Raw)> ExplicitDates(string text)
    {
        foreach (
            var match in IsoDate
                .Matches(text)
                .Cast<Match>()
                .Concat(WrittenDate.Matches(text).Cast<Match>())
        )
        {
            if (TryDate(match.Groups["date"].Value, out var date))
            {
                yield return (date, match.Value);
            }
        }
    }

    private static bool TryDate(string value, out string date)
    {
        var formats = new[]
        {
            "yyyy-MM-dd",
            "MMMM d yyyy",
            "MMMM d, yyyy",
            "d MMMM yyyy",
            "d-MMMM-yyyy",
        };
        if (
            DateOnly.TryParseExact(
                value,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed
            )
            && parsed.Year >= 1000
        )
        {
            date = parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }

        date = "";
        return false;
    }

    private static IEnumerable<string> Strings(JToken? token) =>
        token switch
        {
            { Type: JTokenType.String } => [token.Value<string>()!],
            JArray array => array
                .Where(value => value.Type == JTokenType.String)
                .Select(value => value.Value<string>()!),
            _ => [],
        };

    private static bool HasOnlyStrings(JToken? token) =>
        token?.Type == JTokenType.String
        || token is JArray array && array.All(value => value.Type == JTokenType.String);

    private static bool HasSuppliedValue(JToken? token) =>
        token != null
        && token.Type != JTokenType.Null
        && (token is not JArray array || array.Count != 0)
        && (token.Type != JTokenType.String || !string.IsNullOrWhiteSpace(token.Value<string>()));

    private static string Locator(JToken? token, string field, int index) =>
        token is JArray ? $"metadata.{field}[{index}]" : $"metadata.{field}";
}
