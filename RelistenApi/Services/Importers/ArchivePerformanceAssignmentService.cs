using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Vendor.ArchiveOrg.Metadata;

namespace Relisten.Import;

/// <summary>
/// Uses explicit Archive title billing to distinguish performances. Ordinary set numbers
/// describe a recording's contents and never establish a separate concert. Accepted keys
/// survive corrected dates. Uncertain billing keeps audio on the default or existing
/// primary show without authorizing performance-specific enrichment.
/// </summary>
public sealed class ArchivePerformanceAssignmentService(DbService db, ShowPerformanceService shows)
{
    private static readonly Regex Billing = new(
        @"\b(?<type>early|late|afternoon)\s+(?:show|performance)\b|\b(?<type>matinee|matinée)(?:\s+(?:show|performance))?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );
    private static readonly Regex Combined = new(
        @"\b(?:early\s*(?:show\s*)?(?:and|&|/|\+)\s*late|late\s*(?:show\s*)?(?:and|&|/|\+)\s*early)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );
    private static readonly Regex Uncertain = new(
        @"\b(?:possibly|probably|maybe|unknown|uncertain)\s+(?:(?:the|an?)\s+)?(?:early|late|afternoon|matinee|matinée)\b|\b(?:early|late|afternoon|matinee|matinée)\s+(?:show|performance)\s*(?:\?|\((?:possibly|probably|maybe|uncertain)\))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );
    private static readonly Regex UnclassifiedBilling = new(
        @"\b(?:first|second|third|1st|2nd|3rd)\s+(?:show|performance)\b|\b(?:show|performance)\s*#?\s*\d+\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    /// <summary>Locks the artist before reading the source state used by this item write.</summary>
    internal async Task<Source?> LockAndReadSourceAsync(Artist artist, string identifier)
    {
        await shows.LockArtistAsync(artist);
        return await db.WithWriteConnection(connection =>
            connection.QuerySingleOrDefaultAsync<Source>(
                "SELECT * FROM sources WHERE artist_id=@artistId AND upstream_identifier=@identifier",
                new { artistId = artist.id, identifier }
            )
        );
    }

    /// <summary>
    /// Checks billing after LockAndReadSourceAsync and before any source, review, set or
    /// track writes. The caller owns the transaction through assessment and publication.
    /// </summary>
    internal async Task<ArchivePerformanceAssignment> AssessAsync(
        Artist artist,
        Source? source,
        Metadata metadata,
        string displayDate
    )
    {
        var billing = ParseTitle(metadata.title);
        // Description text may establish combined coverage without identifying a single
        // performance. Retain that uncertainty; single description labels require review.
        var combinedDescription = ParseTitle(metadata.description).Combined;
        if (combinedDescription)
        {
            billing = new(null, null, "multiple_performances_in_description", true);
        }
        var current =
            source == null
                ? null
                : await db.WithWriteConnection(connection =>
                    connection.QuerySingleOrDefaultAsync<CurrentPerformance>(
                        """
                        SELECT sh.id AS ShowId,sh.display_date AS DisplayDate,
                            sh.performance_key AS Key,sh.performance_type AS Type,
                            a.assignment_kind AS Kind,sh.performance_label AS Label,
                            a.evidence::text AS Evidence
                        FROM source_show_assignments a JOIN shows sh ON sh.id=a.show_id
                        WHERE a.source_id=@id
                        """,
                        new { source.id }
                    )
                );

        var priorEvidence = current == null ? null : JObject.Parse(current.Evidence);
        var hasAcceptedIdentity =
            current != null
            && (
                current.Kind == "accepted"
                || (string?)priorEvidence!["diagnostic"] == "performance_billing_changed"
                || priorEvidence!["reviewed_fields"] is JObject
            );
        var diagnostic = billing.Diagnostic;
        var reviewedEvidenceChanged =
            priorEvidence != null && ReviewedEvidenceChanged(priorEvidence, metadata);
        if (reviewedEvidenceChanged)
        {
            diagnostic = "reviewed_performance_evidence_changed";
        }

        if (
            source == null
            && billing.Type == null
            && !displayDate.Contains('X', StringComparison.OrdinalIgnoreCase)
        )
        {
            var ambiguousDate = await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<bool>(
                    "SELECT count(*)>1 FROM shows WHERE artist_id=@artistId AND display_date=@displayDate",
                    new { artistId = artist.id, displayDate }
                )
            );
            if (ambiguousDate)
            {
                diagnostic ??= "default_performance_on_split_date";
            }
        }

        if (hasAcceptedIdentity)
        {
            var acceptedType = Enum.Parse<PerformanceType>(current!.Type, true);
            if (
                billing.Type.HasValue
                && acceptedType != PerformanceType.Unknown
                && billing.Type != acceptedType
            )
            {
                diagnostic ??= "performance_billing_changed";
            }
        }

        if (current != null && current.DisplayDate != displayDate)
        {
            // A held peer still retains its last accepted date. Ignoring it on the next
            // item would let contradictory observations move the shared show back and forth.
            var conflictingPeer = await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<bool>(
                    """
                    SELECT EXISTS(SELECT 1 FROM source_show_assignments a JOIN sources s ON s.id=a.source_id
                        WHERE a.show_id=@showId AND a.source_id<>@sourceId
                            AND s.display_date<>@displayDate)
                    """,
                    new
                    {
                        showId = current.ShowId,
                        sourceId = source!.id,
                        displayDate,
                    }
                )
            );
            if (conflictingPeer)
            {
                diagnostic = "performance_date_conflicts_with_recordings";
            }
        }

        // An unlabeled title supplies no new performance claim. Keep reviewed evidence
        // from another metadata field, rather than replacing it with absent title billing.
        if (billing.Type == null && diagnostic == null && current is { Kind: "accepted" })
        {
            return new(
                current.Key,
                Enum.Parse<PerformanceType>(current.Type, true),
                current.Label,
                current.Evidence,
                null,
                false
            );
        }

        // Preserve previously accepted identities even if corrected metadata changes the
        // date. A new partial date cannot group recordings from its unknown components.
        string? key = null;
        if (billing.Type.HasValue)
        {
            key = hasAcceptedIdentity
                ? current!.Key
                : PerformanceKey(metadata, displayDate, billing.Type.Value);
            if (
                !hasAcceptedIdentity
                && !displayDate.Contains('X', StringComparison.OrdinalIgnoreCase)
            )
            {
                var matches = await MatchingAcceptedKeysAsync(
                    artist,
                    metadata,
                    displayDate,
                    billing.Type.Value
                );
                if (matches.Length == 1)
                {
                    key = matches[0];
                }
                else if (matches.Length > 1)
                {
                    diagnostic ??= "multiple_matching_performances";
                }
            }
        }

        var evidence =
            current != null && priorEvidence?["reviewed_fields"] is JObject
                ? RetainReviewedEvidence(current.Evidence, metadata, displayDate, diagnostic)
                : JsonConvert.SerializeObject(
                    new
                    {
                        provider = "archive.org",
                        item_identifier = metadata.identifier,
                        field = combinedDescription ? "description" : "title",
                        value = combinedDescription ? metadata.description : metadata.title,
                        metadata_date = metadata.date,
                        display_date = displayDate,
                        venue = metadata.venue,
                        coverage = metadata.coverage,
                        diagnostic = diagnostic,
                    }
                );
        var label =
            current != null && priorEvidence?["reviewed_fields"] is JObject
                ? current.Label
                : billing.Label;
        return new(key, billing.Type, label, evidence, diagnostic, billing.Combined);
    }

    private static string RetainReviewedEvidence(
        string acceptedEvidence,
        Metadata metadata,
        string displayDate,
        string? diagnostic
    )
    {
        var retained = JObject.Parse(acceptedEvidence);
        if (diagnostic == null)
        {
            retained.Remove("current_evidence_conflict");
            return retained.ToString(Formatting.None);
        }

        retained["current_evidence_conflict"] = JObject.FromObject(
            new
            {
                diagnostic,
                display_date = displayDate,
                title = metadata.title,
                observed_fields = new Dictionary<string, string?>
                {
                    ["metadata.description"] =
                        metadata.description == null
                            ? null
                            : Convert.ToHexString(
                                SHA256.HashData(Encoding.UTF8.GetBytes(metadata.description))
                            ),
                    ["metadata.title"] =
                        metadata.title == null
                            ? null
                            : Convert.ToHexString(
                                SHA256.HashData(Encoding.UTF8.GetBytes(metadata.title))
                            ),
                },
            }
        );
        return retained.ToString(Formatting.None);
    }

    // Reviewed operators may bind their decision to these complete provider strings.
    // Hash the typed value as UTF-8, without trimming, case folding or HTML normalization.
    // Missing/empty maps preserve older permanent decisions and automatic provider evidence.
    private static bool ReviewedEvidenceChanged(JObject evidence, Metadata metadata)
    {
        var fields = evidence["reviewed_fields"] as JObject;
        if (fields == null)
        {
            return false;
        }

        foreach (var field in fields.Properties())
        {
            var value = field.Name switch
            {
                "metadata.description" => metadata.description,
                "metadata.title" => metadata.title,
                _ => null,
            };
            if (value == null)
            {
                return true;
            }

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
            if (!string.Equals(hash, (string?)field.Value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // Look up current evidence, rather than reconstructing an old key from today's
    // date. This lets another taper join a performance after a supported date correction.
    private async Task<string[]> MatchingAcceptedKeysAsync(
        Artist artist,
        Metadata metadata,
        string displayDate,
        PerformanceType type
    )
    {
        var keys = await db.WithWriteConnection(connection =>
            connection.QueryAsync<string>(
                """
                SELECT DISTINCT sh.performance_key FROM shows sh
                JOIN source_show_assignments a ON a.show_id=sh.id
                WHERE sh.artist_id=@artistId AND sh.display_date=@displayDate
                    AND sh.performance_type=@type AND a.assignment_kind='accepted'
                    AND a.evidence->>'provider'='archive.org'
                    AND lower(trim(coalesce(a.evidence->>'venue','')))=@venue
                    AND lower(trim(coalesce(a.evidence->>'coverage','')))=@coverage
                LIMIT 2
                """,
                new
                {
                    artistId = artist.id,
                    displayDate,
                    type = type.ToString().ToLowerInvariant(),
                    venue = metadata.venue?.Trim().ToLowerInvariant() ?? "",
                    coverage = metadata.coverage?.Trim().ToLowerInvariant() ?? "",
                }
            )
        );
        return keys.ToArray();
    }

    internal Task ApplyAsync(Artist artist, Source source, ArchivePerformanceAssignment assignment)
    {
        if (assignment.Diagnostic != null)
        {
            // Uncertain performance evidence affects enrichment, not audio eligibility.
            // The core retains an existing primary or picks the date's default once.
            return shows.AssignDefaultSourceAsync(
                artist,
                source.id,
                source.display_date,
                assignment.Evidence,
                assignment.Combined ? "combined" : "unresolved"
            );
        }

        return assignment.Key == null
            ? Task.CompletedTask
            : shows.AssignSourceAsync(
                artist,
                source.id,
                assignment.Key,
                source.display_date,
                assignment.Type ?? PerformanceType.Unknown,
                assignment.Label,
                assignment.Evidence
            );
    }

    internal static ArchiveTitleBilling ParseTitle(string? title)
    {
        title ??= "";
        var matches = Billing.Matches(title).Cast<Match>().ToArray();
        var types = matches
            .Select(match =>
                match.Groups["type"].Value.ToLowerInvariant() switch
                {
                    "early" => PerformanceType.Early,
                    "late" => PerformanceType.Late,
                    _ => PerformanceType.Afternoon,
                }
            )
            .Distinct()
            .ToArray();
        var combined = types.Length > 1 || Combined.IsMatch(title);
        if (combined)
        {
            return new(null, null, "multiple_performances_in_recording", true);
        }
        if (types.Length == 0)
        {
            return new(
                null,
                null,
                UnclassifiedBilling.IsMatch(title) ? "unclassified_performance_billing" : null,
                false
            );
        }
        if (Uncertain.IsMatch(title))
        {
            return new(null, null, "uncertain_performance_billing", false);
        }
        return new(types[0], matches[0].Value, null, false);
    }

    private static string PerformanceKey(
        Metadata metadata,
        string displayDate,
        PerformanceType type
    )
    {
        if (displayDate.Contains('X', StringComparison.OrdinalIgnoreCase))
        {
            return "archive:item:" + metadata.identifier;
        }

        // The billed date, category and retained venue evidence identify the ordinary
        // early/late case. Reviewed keys can distinguish two performances with the same
        // category and venue; the enum is never a database uniqueness constraint.
        var venue = string.Join(
            "|",
            metadata.venue?.Trim().ToLowerInvariant(),
            metadata.coverage?.Trim().ToLowerInvariant()
        );
        var venueHash = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(venue)))
            .ToLowerInvariant();
        return $"archive:{displayDate}:{type.ToString().ToLowerInvariant()}:{venueHash}";
    }

    private sealed record CurrentPerformance(
        long ShowId,
        string DisplayDate,
        string Key,
        string Type,
        string Kind,
        string? Label,
        string Evidence
    );
}

internal sealed record ArchiveTitleBilling(
    PerformanceType? Type,
    string? Label,
    string? Diagnostic,
    bool Combined
);

internal sealed record ArchivePerformanceAssignment(
    string? Key,
    PerformanceType? Type,
    string? Label,
    string Evidence,
    string? Diagnostic,
    bool Combined
)
{
    internal bool HoldsMetadata => Diagnostic == "performance_date_conflicts_with_recordings";
}

/// <summary>A conflicting performance date retains the last accepted recording metadata.</summary>
public sealed class ArchivePerformanceHeldException(string diagnostic) : Exception(diagnostic);
