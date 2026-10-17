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
    TapersSectionReason[] Reasons,
    TapersSectionDateDetails? DateDetails = null
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
    public const int RulesVersion = 5;

    // Event wording can hold attribution. A venue's "Memorial" name alone cannot.
    private static readonly Regex Tribute = new(
        @"\b(?:tribute|memorial\s+(?:concert|show|performance|tribute|benefit)|in memory of|dear jerry|celebrat(?:ion|ing)\s+(?:of\s+)?(?:the\s+)?(?:life|music|jerry)|the songs of)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );
    private const string CompilationPattern =
        @"\b(?:compilations?|best[- ]of\s+(?:recordings|performances|shows|concerts)|remaster[- ]only|recordings from (?:multiple|various|several))\b";
    private static readonly Regex Compilation = new(
        CompilationPattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    // "Best of" labels a title; description prose needs a recording-related object
    // so transfer notes such as "the best of my abilities" do not hold publication.
    private static readonly Regex CompilationTitle = new(
        CompilationPattern + @"|\bbest[- ]of\b",
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
        var date = new TapersSectionDateResolver().Resolve(
            metadata,
            observation.Item,
            reasons,
            evidence
        );
        if (observation.IsAvailabilityOnly)
        {
            reasons.Add(TapersSectionReason.UpstreamUnavailable);
        }
        else if (!Strings(metadata["mediatype"]).SequenceEqual(["audio"]))
        {
            reasons.Add(TapersSectionReason.InvalidMetadata);
        }

        CheckPlayableFiles(root, reasons);

        if (published != null && candidate?.ArtistUuid != published.ArtistUuid)
        {
            reasons.Add(TapersSectionReason.PublishedAssignmentConflict);
        }

        return new TapersSectionInterpretation(
            RulesVersion,
            creators,
            candidate?.CandidateId,
            date.DisplayDate,
            evidence.ToArray(),
            reasons.Distinct().ToArray(),
            date.Details
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
                            field == "title" ? CompilationTitle : Compilation,
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

    private static string Locator(JToken? token, string field, int index) =>
        token is JArray ? $"metadata.{field}[{index}]" : $"metadata.{field}";
}
