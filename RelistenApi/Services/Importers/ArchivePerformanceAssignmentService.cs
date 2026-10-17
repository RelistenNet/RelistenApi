using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Vendor.ArchiveOrg.Metadata;

namespace Relisten.Import;

/// <summary>
/// Uses explicit Archive title billing to distinguish performances. Ordinary set numbers
/// describe a recording's contents and never establish a separate concert. Accepted keys
/// survive corrected dates; contradictory billing holds the update for review.
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
        var current =
            source == null
                ? null
                : await db.WithWriteConnection(connection =>
                    connection.QuerySingleOrDefaultAsync<CurrentPerformance>(
                        """
                        SELECT sh.id AS ShowId,sh.display_date AS DisplayDate,
                            sh.performance_key AS Key,sh.performance_type AS Type,
                            a.assignment_kind AS Kind
                        FROM source_show_assignments a JOIN shows sh ON sh.id=a.show_id
                        WHERE a.source_id=@id
                        """,
                        new { source.id }
                    )
                );

        var hold = billing.HoldReason;
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
                hold ??= "performance_required_on_split_date";
            }
        }

        if (current != null && current.Kind != "legacy")
        {
            var acceptedType = Enum.Parse<PerformanceType>(current.Type, true);
            if (acceptedType != PerformanceType.Unknown && billing.Type != acceptedType)
            {
                hold ??= "performance_billing_changed";
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
                hold ??= "performance_date_conflicts_with_recordings";
            }
        }

        // Preserve previously accepted identities even if corrected metadata changes the
        // date. A new partial date cannot group recordings from its unknown components.
        string? key = null;
        if (billing.Type.HasValue)
        {
            key = current is { Kind: not "legacy", Key: not "legacy" }
                ? current.Key
                : PerformanceKey(metadata, displayDate, billing.Type.Value);
            if (
                current is not { Kind: not "legacy", Key: not "legacy" }
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
                    hold ??= "multiple_matching_performances";
                }
            }
        }

        var evidence = JsonConvert.SerializeObject(
            new
            {
                provider = "archive.org",
                item_identifier = metadata.identifier,
                field = "title",
                value = metadata.title,
                metadata_date = metadata.date,
                display_date = displayDate,
                venue = metadata.venue,
                coverage = metadata.coverage,
                diagnostic = hold,
            }
        );
        return new(key, billing.Type, billing.Label, evidence, hold, billing.Combined);
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
        if (assignment.HoldReason != null)
        {
            // Existing playable coverage stays on its last accepted show. Recording the
            // ambiguity removes permission to use it for performance-specific enrichment.
            return source.show_id.HasValue
                ? shows.AssignSourceAsync(
                    artist,
                    source.id,
                    assignment.Key ?? "archive:held:" + source.uuid,
                    source.display_date,
                    evidence: assignment.Evidence,
                    assignmentKind: assignment.Combined ? "combined" : "unresolved"
                )
                : Task.CompletedTask;
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
        string Kind
    );
}

internal sealed record ArchiveTitleBilling(
    PerformanceType? Type,
    string? Label,
    string? HoldReason,
    bool Combined
);

internal sealed record ArchivePerformanceAssignment(
    string? Key,
    PerformanceType? Type,
    string? Label,
    string Evidence,
    string? HoldReason,
    bool Combined
);

/// <summary>A new item cannot publish until its performance coverage is resolved.</summary>
public sealed class ArchivePerformanceHeldException(string diagnostic) : Exception(diagnostic);
