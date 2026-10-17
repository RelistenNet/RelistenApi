using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    private const string VenueFactIdentityColumns = """
        v.uuid AS "Uuid",v.display_name AS "DisplayText",v.revision AS "Revision",v.kind AS "Kind",
        v.parent_venue_uuid AS "ParentVenueUuid",v.metadata_reviewed AS "MetadataReviewed",
        owner.uuid AS "OwnerSubjectUuid",redirect.survivor_uuid AS "SurvivorUuid",redirect.decision_uuid AS "DecisionUuid"
        """;

    private const string VenueFactAssertionColumns = """
        a.uuid AS "AssertionUuid",a.venue_uuid AS "StoredIdentityUuid",a.revision AS "Revision",a.basis AS "Basis",
        a.input_revision_uuid AS "InputRevisionUuid",a.json_pointer AS "JsonPointer",
        a.start_code_point AS "StartCodePoint",a.end_code_point AS "EndCodePoint",a.quote AS "Quote"
        """;

    /// <summary>
    /// Reads complete accepted histories through the selected survivor's reverse redirect closure.
    /// Limits are explicit blocking issues, never permission to compare only convenient assertions.
    /// The caller owns the primary repeatable-read transaction and its candidate generation.
    /// </summary>
    private static async Task<VenueCandidateFacts[]> ReadVenueFactsAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationCandidateSet selection,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        var result = new List<VenueCandidateFacts>();
        foreach (var hit in selection.Items)
        {
            var closure = await ReadVenueFactIdentityClosure(
                c,
                t,
                hit.Uuid,
                limits.MaximumIdentityClosure,
                false,
                ct
            );
            if (closure.Length > limits.MaximumIdentityClosure)
            {
                context.AddIssue(CandidateFactsIssueKind.IdentityClosureLimit, hit.Uuid, "venue");
                closure = closure.Take(limits.MaximumIdentityClosure).ToArray();
            }
            var current = closure.SingleOrDefault(row => row.Uuid == hit.Uuid);
            if (current == null)
            {
                context.AddIssue(CandidateFactsIssueKind.MissingRelatedIdentity, hit.Uuid, "venue");
                continue;
            }
            foreach (var identity in closure)
            {
                context.AddOwner(identity.OwnerSubjectUuid, hit.Uuid);
            }
            var owners = closure.ToDictionary(row => row.Uuid, row => row.OwnerSubjectUuid);
            var identityUuids = closure.Select(row => row.Uuid).ToArray();
            var names = await ReadVenueFactNames(
                c,
                t,
                identityUuids,
                owners,
                hit.Uuid,
                limits,
                context,
                ct
            );
            var locations = await ReadVenueFactLocations(
                c,
                t,
                identityUuids,
                owners,
                hit.Uuid,
                limits,
                context,
                ct
            );
            var externalIdentities = await ReadVenueFactExternalIdentities(
                c,
                t,
                identityUuids,
                owners,
                hit.Uuid,
                limits,
                context,
                ct
            );
            var parents = await ReadVenueFactParents(c, t, closure, hit.Uuid, limits, context, ct);
            result.Add(
                new VenueCandidateFacts
                {
                    Hit = hit,
                    Header = current.ToHeader(),
                    IdentityClosure = closure.Select(row => row.ToHeader()).ToArray(),
                    Redirects = VenueFactRedirects(closure),
                    Names = names,
                    Locations = locations,
                    ExternalIdentities = externalIdentities,
                    Parents = parents,
                }
            );
        }
        return result.ToArray();
    }

    private static async Task<VenueFactIdentityRow[]> ReadVenueFactIdentityClosure(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid uuid,
        int maximum,
        bool forward,
        CancellationToken ct
    )
    {
        // UNION terminates malformed cycles. The bounded CTE input limits materialization before
        // the final ordering; a limit breach remains visible in the snapshot's blocking issues.
        // Include the requested root explicitly because recursive stream order is not a contract.
        var next = forward
            ? "SELECT r.survivor_uuid FROM identities i JOIN normalization.venue_redirects r ON r.retired_uuid=i.uuid"
            : "SELECT r.retired_uuid FROM identities i JOIN normalization.venue_redirects r ON r.survivor_uuid=i.uuid";
        return (
            await c.QueryAsync<VenueFactIdentityRow>(
                new CommandDefinition(
                    $"""
                    WITH RECURSIVE identities(uuid) AS (SELECT @uuid::uuid UNION {next}),
                        bounded AS MATERIALIZED (SELECT uuid FROM identities LIMIT @take),
                        retained AS (SELECT uuid FROM bounded UNION SELECT @uuid::uuid)
                    SELECT {VenueFactIdentityColumns}
                    FROM retained JOIN normalization.canonical_venues v ON v.uuid=retained.uuid
                    LEFT JOIN normalization.venue_redirects redirect ON redirect.retired_uuid=v.uuid
                    LEFT JOIN normalization.work_subjects owner ON owner.domain='venues' AND owner.subject_kind='venue' AND owner.subject_uuid=v.uuid
                    ORDER BY (v.uuid=@uuid) DESC,v.uuid
                    """,
                    new { uuid, take = maximum + 1 },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
    }

    private static async Task<CandidateNameFact[]> ReadVenueFactNames(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid[] identities,
        IReadOnlyDictionary<Guid, Guid?> owners,
        Guid candidateUuid,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        var rows = (
            await c.QueryAsync<VenueFactNameRow>(
                new CommandDefinition(
                    $"""
                    SELECT {VenueFactAssertionColumns},a.wording AS "Wording",a.language AS "Language",
                        a.valid_from AS "ValidFrom",a.valid_to AS "ValidTo"
                    FROM normalization.venue_names a WHERE a.venue_uuid=ANY(@identities) AND a.state='accepted'
                    ORDER BY a.venue_uuid,a.uuid LIMIT @take
                    """,
                    new { identities, take = limits.MaximumAssertionsPerKind + 1 },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (rows.Length > limits.MaximumAssertionsPerKind)
        {
            context.AddIssue(CandidateFactsIssueKind.AssertionLimit, candidateUuid, "venue_name");
        }
        return rows.Take(limits.MaximumAssertionsPerKind)
            .Select(row => new CandidateNameFact(
                row.ToProvenance(owners[row.StoredIdentityUuid]),
                row.Wording,
                row.Language,
                row.ValidFrom,
                row.ValidTo
            ))
            .ToArray();
    }

    private static async Task<CandidateVenueLocationFact[]> ReadVenueFactLocations(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid[] identities,
        IReadOnlyDictionary<Guid, Guid?> owners,
        Guid candidateUuid,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        var rows = (
            await c.QueryAsync<VenueFactLocationRow>(
                new CommandDefinition(
                    $"""
                    SELECT {VenueFactAssertionColumns},a.original_locality AS "OriginalLocality",a.country AS "Country",
                        a.region AS "Region",a.city AS "City",a.address AS "Address",a.latitude AS "Latitude",
                        a.longitude AS "Longitude",a.coordinate_precision AS "CoordinatePrecision",
                        a.valid_from AS "ValidFrom",a.valid_to AS "ValidTo"
                    FROM normalization.venue_locations a WHERE a.venue_uuid=ANY(@identities) AND a.state='accepted'
                    ORDER BY a.venue_uuid,a.uuid LIMIT @take
                    """,
                    new { identities, take = limits.MaximumAssertionsPerKind + 1 },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (rows.Length > limits.MaximumAssertionsPerKind)
        {
            context.AddIssue(
                CandidateFactsIssueKind.AssertionLimit,
                candidateUuid,
                "venue_location"
            );
        }
        if (rows.Length != 0)
        {
            // These fields retain provider wording and precision. They establish no ISO country,
            // geographic locality identity, street-address point or inferred validity interval.
            context.AddIssue(
                CandidateFactsIssueKind.UnknownGeography,
                candidateUuid,
                "venue_location"
            );
        }
        return rows.Take(limits.MaximumAssertionsPerKind)
            .Select(row => new CandidateVenueLocationFact(
                row.ToProvenance(owners[row.StoredIdentityUuid]),
                row.OriginalLocality,
                row.Country,
                row.Region,
                row.City,
                row.Address,
                row.Latitude,
                row.Longitude,
                row.CoordinatePrecision,
                row.ValidFrom,
                row.ValidTo
            ))
            .ToArray();
    }

    private static async Task<CandidateExternalIdentityFact[]> ReadVenueFactExternalIdentities(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid[] identities,
        IReadOnlyDictionary<Guid, Guid?> owners,
        Guid candidateUuid,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        var rows = (
            await c.QueryAsync<VenueFactExternalIdentityRow>(
                new CommandDefinition(
                    $"""
                    SELECT {VenueFactAssertionColumns},a.provider AS "Provider",a.resource_type AS "ResourceType",a.provider_key AS "ProviderKey"
                    FROM normalization.venue_external_identities a WHERE a.venue_uuid=ANY(@identities) AND a.state='accepted'
                    ORDER BY a.venue_uuid,a.uuid LIMIT @take
                    """,
                    new { identities, take = limits.MaximumAssertionsPerKind + 1 },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (rows.Length > limits.MaximumAssertionsPerKind)
        {
            context.AddIssue(
                CandidateFactsIssueKind.AssertionLimit,
                candidateUuid,
                "venue_external_identity"
            );
        }
        if (rows.Length != 0)
        {
            context.AddIssue(
                CandidateFactsIssueKind.UnknownAuthority,
                candidateUuid,
                "venue_external_identity"
            );
        }
        return rows.Take(limits.MaximumAssertionsPerKind)
            .Select(row => new CandidateExternalIdentityFact(
                row.ToProvenance(owners[row.StoredIdentityUuid]),
                row.Provider,
                row.ResourceType,
                row.ProviderKey
            ))
            .ToArray();
    }

    private static async Task<CandidateVenueParentFact[]> ReadVenueFactParents(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        IReadOnlyList<VenueFactIdentityRow> identities,
        Guid candidateUuid,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        var parents = new List<CandidateVenueParentFact>();
        var consumedParentUuids = new HashSet<Guid>();
        var chains = new Dictionary<Guid, (VenueFactIdentityRow[] Rows, bool Truncated)>();
        foreach (var identity in identities)
        {
            if (identity.ParentVenueUuid is not { } parentUuid)
            {
                parents.Add(new(identity.Uuid, null, null, [], []));
                continue;
            }
            if (!chains.TryGetValue(parentUuid, out var chain))
            {
                var observed = await ReadVenueFactIdentityClosure(
                    c,
                    t,
                    parentUuid,
                    limits.MaximumParentClosure,
                    true,
                    ct
                );
                var retained = new List<VenueFactIdentityRow>();
                var omitted = observed.Length > limits.MaximumParentClosure;
                foreach (var parent in observed)
                {
                    if (
                        consumedParentUuids.Contains(parent.Uuid)
                        || consumedParentUuids.Count < limits.MaximumParentClosure
                    )
                    {
                        consumedParentUuids.Add(parent.Uuid);
                        retained.Add(parent);
                    }
                    else
                    {
                        omitted = true;
                    }
                }
                chain = (retained.ToArray(), omitted);
                chains.Add(parentUuid, chain);
            }
            var (closure, truncated) = chain;
            if (truncated)
            {
                context.AddIssue(
                    CandidateFactsIssueKind.ParentClosureLimit,
                    candidateUuid,
                    "venue_parent"
                );
            }
            foreach (var parent in closure)
            {
                context.AddOwner(parent.OwnerSubjectUuid, candidateUuid);
            }
            var terminal = closure.Where(row => row.SurvivorUuid == null).ToArray();
            var resolved = !truncated && terminal.Length == 1 ? terminal[0] : null;
            if (!truncated && resolved == null)
            {
                context.AddIssue(
                    closure.Length == 0
                        ? CandidateFactsIssueKind.MissingRelatedIdentity
                        : CandidateFactsIssueKind.InvalidStoredFact,
                    candidateUuid,
                    "venue_parent"
                );
            }
            if (resolved != null && resolved.Kind != "site")
            {
                context.AddIssue(
                    CandidateFactsIssueKind.InvalidStoredFact,
                    candidateUuid,
                    "venue_parent_kind"
                );
            }
            parents.Add(
                new(
                    identity.Uuid,
                    parentUuid,
                    resolved?.Uuid,
                    closure.Select(row => row.ToHeader()).ToArray(),
                    VenueFactRedirects(closure)
                )
            );
        }
        return parents.ToArray();
    }

    private static CandidateRedirectFact[] VenueFactRedirects(
        IEnumerable<VenueFactIdentityRow> identities
    ) =>
        identities
            .Where(row => row.SurvivorUuid.HasValue)
            .Select(row => new CandidateRedirectFact(
                row.Uuid,
                row.SurvivorUuid!.Value,
                row.DecisionUuid
            ))
            .ToArray();

    private sealed class VenueFactIdentityRow
    {
        public Guid Uuid { get; init; }
        public string DisplayText { get; init; } = null!;
        public long Revision { get; init; }
        public string Kind { get; init; } = null!;
        public bool MetadataReviewed { get; init; }
        public Guid? OwnerSubjectUuid { get; init; }
        public Guid? ParentVenueUuid { get; init; }
        public Guid? SurvivorUuid { get; init; }
        public Guid? DecisionUuid { get; init; }

        public CandidateIdentityHeader ToHeader() =>
            new(Uuid, DisplayText, Revision, OwnerSubjectUuid, Kind, MetadataReviewed);
    }

    private abstract class VenueFactAssertionRow
    {
        public Guid AssertionUuid { get; init; }
        public Guid StoredIdentityUuid { get; init; }
        public long Revision { get; init; }
        public string Basis { get; init; } = null!;
        public Guid InputRevisionUuid { get; init; }
        public string JsonPointer { get; init; } = null!;
        public int? StartCodePoint { get; init; }
        public int? EndCodePoint { get; init; }
        public string? Quote { get; init; }

        public CandidateAssertionProvenance ToProvenance(Guid? ownerUuid) =>
            new(
                AssertionUuid,
                StoredIdentityUuid,
                ownerUuid,
                Revision,
                Basis,
                [new(InputRevisionUuid, JsonPointer, StartCodePoint, EndCodePoint, Quote)]
            );
    }

    private sealed class VenueFactNameRow : VenueFactAssertionRow
    {
        public string Wording { get; init; } = null!;
        public string? Language { get; init; }
        public DateOnly? ValidFrom { get; init; }
        public DateOnly? ValidTo { get; init; }
    }

    private sealed class VenueFactLocationRow : VenueFactAssertionRow
    {
        public string OriginalLocality { get; init; } = null!;
        public string? Country { get; init; }
        public string? Region { get; init; }
        public string? City { get; init; }
        public string? Address { get; init; }
        public double? Latitude { get; init; }
        public double? Longitude { get; init; }
        public string? CoordinatePrecision { get; init; }
        public DateOnly? ValidFrom { get; init; }
        public DateOnly? ValidTo { get; init; }
    }

    private sealed class VenueFactExternalIdentityRow : VenueFactAssertionRow
    {
        public string Provider { get; init; } = null!;
        public string ResourceType { get; init; } = null!;
        public string ProviderKey { get; init; } = null!;
    }
}
