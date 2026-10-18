using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    public const string ShowVenueContextVersion = "venue-resolution-context-v1";

    /// <summary>
    /// Rechecks the published account and original binding in the same primary snapshot as
    /// direct assignments, imported fallbacks, all assignment targets and global venue facts.
    /// Historical accepted citations are retained facts, not current input-head requirements.
    /// </summary>
    public async Task<VenueResolutionContext> FindShowVenueResolutionContextAsync(
        VenueResolutionSource source,
        NormalizationCandidateQuery query,
        CandidateFactsLimits? limits = null,
        int maximumAssignments = 1000,
        CancellationToken cancellationToken = default
    )
    {
        if (
            source.Claim is not { IsShow: true, CatalogUuid: not null, ArtistUuid: not null } claim
            || source.OriginalTicket == null
            || source.ProposalOwner == null
            || source.ShowReference == null
            || query.Domain != IdentityMatchingDomain.Venues
        )
        {
            throw new ArgumentException(
                "Show venue candidates require an authenticated, bound published observation."
            );
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAssignments, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumAssignments, 10_000);
        var frozen = FreezeFactsQuery(query);
        var names = ValidateQuery(frozen);
        var bounds = ValidateFactsLimits(limits ?? new());

        await using var c = db.CreateConnection(false, false);
        await c.OpenAsync(cancellationToken);
        await using var t = await c.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken
        );
        await c.ExecuteAsync(
            new CommandDefinition(
                "SET TRANSACTION READ ONLY",
                transaction: t,
                cancellationToken: cancellationToken
            )
        );
        var context = new CandidateFactsContext();
        var showUuid = claim.CatalogUuid.Value;
        var owner = await ReadShowAssignmentOwner(c, t, showUuid, cancellationToken);
        context.AddOwner(source.ProposalOwner.SubjectUuid);
        context.AddArtist(claim.ArtistUuid.Value);
        var current = await ShowSourceIsCurrent(c, t, source, cancellationToken);
        if (!current)
        {
            context.AddIssue(
                CandidateFactsIssueKind.InvalidStoredFact,
                factKind: "show_source_changed"
            );
        }
        var assignments = await ReadShowAssignmentFacts(
            c,
            t,
            showUuid,
            maximumAssignments,
            bounds.MaximumEvidencePerAssertion,
            context,
            cancellationToken
        );
        if (owner != null || assignments.Length > 0)
        {
            context.AddOwner(owner?.SubjectUuid);
        }
        var fallbackIds = await ReadShowImportedFallbacks(c, t, source, cancellationToken);
        var fallbacks = new List<ImportedVenueAssignmentFact>();
        foreach (var importedUuid in fallbackIds)
        {
            var importedOwner = await ReadImportedAssignmentOwner(
                c,
                t,
                importedUuid,
                cancellationToken
            );
            // An unnormalized imported label has no accepted assignment owner to consume.
            // Existing assignments, however, must never lose their owner observation.
            var imported = await ReadImportedAssignmentFacts(
                c,
                t,
                importedUuid,
                maximumAssignments,
                bounds.MaximumEvidencePerAssertion,
                context,
                cancellationToken
            );
            if (imported.Length > 0)
            {
                context.AddOwner(importedOwner?.SubjectUuid);
            }
            fallbacks.AddRange(imported);
            if (fallbacks.Count + assignments.Length > maximumAssignments)
            {
                throw new InvalidOperationException(
                    "Show venue resolution exceeds its combined assignment limit."
                );
            }
        }
        var selection = await FindInSnapshotAsync(c, t, frozen, names, cancellationToken);
        var selected = selection;
        if (selection.Items.Count > bounds.MaximumCandidates)
        {
            context.AddIssue(CandidateFactsIssueKind.CandidateLimit);
            selected = selection with
            {
                Items = selection.Items.Take(bounds.MaximumCandidates).ToArray(),
            };
        }
        var facts = await ReadVenueFactsAsync(c, t, selected, bounds, context, cancellationToken);
        var targets = await ReadImportedAssignmentTargets(
            c,
            t,
            assignments.Concat(fallbacks).ToArray(),
            facts,
            bounds,
            context,
            cancellationToken
        );
        var universes = await ReadFactsUniversesAsync(c, t, frozen, cancellationToken);
        var membership = await NormalizationWorkService.GetInputContributionsInSnapshotAsync(
            c,
            t,
            new("venues", "show", showUuid),
            new(),
            cancellationToken
        );
        var ownerBindings =
            owner == null
                ? EmptyVenueOwnerBindings()
                : await ReadVenueOwnerBindings(c, t, owner.SubjectUuid, context, cancellationToken);
        var observationSubscription = await ReadVenueObservationSubscription(
            c,
            t,
            source,
            context,
            cancellationToken
        );
        var readSet = await context.ReadReadSetAsync(c, t, universes, cancellationToken);
        var candidates = new NormalizationCandidateFactsSnapshot<VenueCandidateFacts>(
            FactsVersion,
            FactsQueryHash(frozen, bounds),
            frozen,
            bounds,
            selection,
            facts,
            readSet,
            context.Issues
        );
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        version = ShowVenueContextVersion,
                        source.ShowReference,
                        showUuid,
                        queryHash = candidates.QueryHash,
                        maximumAssignments,
                    }
                )
            )
        );
        await t.CommitAsync(cancellationToken);
        return new(
            ShowVenueContextVersion,
            hash,
            owner,
            assignments,
            targets,
            candidates,
            fallbacks.ToArray(),
            readSet.CatalogInputs.SingleOrDefault(input => input.ArtistUuid == claim.ArtistUuid),
            current,
            membership,
            ownerBindings.ReadSet,
            ownerBindings.InputReferences,
            observationSubscription
        );
    }

    private static async Task<bool> ShowSourceIsCurrent(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        VenueResolutionSource source,
        CancellationToken ct
    )
    {
        var claim = source.Claim!;
        var binding = source.RecordingBinding?.Recording.Value;
        var sourceUuids =
            binding != null
                ? new[] { binding.SourceUuid }
                : source.SetlistBinding?.Performance.Value?.SupportingSourceUuids.ToArray() ?? [];
        if (
            (binding == null) == (source.SetlistBinding == null)
            || binding != null
                && (binding.ShowUuid != claim.CatalogUuid || binding.ArtistUuid != claim.ArtistUuid)
            || source.SetlistBinding?.Performance.Value is { } performance
                && (
                    performance.ShowUuid != claim.CatalogUuid
                    || performance.ArtistUuid != claim.ArtistUuid
                )
        )
        {
            return false;
        }
        if (!await OriginalVenueProposalIsCurrent(c, t, source, ct))
        {
            return false;
        }

        // Original clocks fence provider lookup replacement. These explicit checks also refuse
        // corrupt or cross-artist catalog homes without inferring an association by date or title.
        return await c.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT EXISTS(SELECT 1 FROM shows sh JOIN artists a ON a.id=sh.artist_id
                    WHERE sh.uuid=@showUuid AND a.uuid=@artistUuid)
                AND NOT EXISTS(
                    SELECT 1 FROM unnest(@sourceUuids::uuid[]) wanted(uuid)
                    WHERE NOT EXISTS(SELECT 1 FROM sources src JOIN artists a ON a.id=src.artist_id
                        JOIN shows sh ON sh.id=src.show_id AND sh.artist_id=src.artist_id
                        JOIN source_show_assignments assignment ON assignment.source_id=src.id
                            AND assignment.show_id=sh.id AND assignment.assignment_kind IN ('accepted','legacy')
                        LEFT JOIN venues v ON v.id=src.venue_id AND v.artist_id=src.artist_id
                        WHERE src.uuid=wanted.uuid AND sh.uuid=@showUuid AND a.uuid=@artistUuid
                            AND (NOT @isRecording OR v.uuid IS NOT DISTINCT FROM @sourceVenueUuid)))
                AND (@setlistShowUuid::uuid IS NULL OR EXISTS(
                    SELECT 1 FROM setlist_shows sl JOIN artists a ON a.id=sl.artist_id
                        JOIN setlist_show_assignments assignment ON assignment.setlist_show_id=sl.id
                        JOIN shows sh ON sh.id=assignment.show_id AND sh.artist_id=sl.artist_id
                        LEFT JOIN venues v ON v.id=sl.venue_id
                    WHERE sl.uuid=@setlistShowUuid AND a.uuid=@artistUuid AND sh.uuid=@showUuid
                        AND v.uuid IS NOT DISTINCT FROM @setlistVenueUuid))
                """,
                new
                {
                    showUuid = claim.CatalogUuid,
                    artistUuid = claim.ArtistUuid,
                    sourceUuids,
                    isRecording = binding != null,
                    sourceVenueUuid = binding?.SourceVenueUuid,
                    setlistShowUuid = source.SetlistBinding?.ImportedSetlist.Value?.SetlistShowUuid,
                    setlistVenueUuid = source.SetlistBinding?.ImportedSetlist.Value?.VenueUuid,
                },
                t,
                cancellationToken: ct
            )
        );
    }

    /// <summary>
    /// Published extraction authority and all original freshness families are checked even when
    /// no performance was bound. An unknown association must not replace its negative clocks
    /// with fresh observations when the proposal is acknowledged as an abstention.
    /// </summary>
    private static async Task<bool> OriginalVenueProposalIsCurrent(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        VenueResolutionSource source,
        CancellationToken ct
    )
    {
        if (source.OriginalTicket is not { } ticket || source.ProposalOwner is not { } proposal)
        {
            return false;
        }
        return await c.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT EXISTS(
                    SELECT 1 FROM normalization.work_subjects s JOIN normalization.work_runs r ON r.subject_uuid=s.uuid
                    WHERE s.uuid=@SubjectUuid AND s.domain='extractions' AND r.uuid=@WorkUuid AND r.state='published'
                        AND r.ticket=@ticketJson::jsonb AND s.published_work_uuid=@WorkUuid
                        AND s.desired_generation=@DesiredGeneration AND s.editorial_revision=@EditorialRevision
                        AND s.policy_version=@PolicyVersion
                        AND s.desired_generation=@proposalDesired AND s.editorial_revision=@proposalEditorial
                        AND s.published_work_uuid IS NOT DISTINCT FROM @proposalPublished)
                AND NOT EXISTS(
                    SELECT 1 FROM jsonb_to_recordset(@inputs::jsonb) AS i("ResourceUuid" uuid,"RevisionUuid" uuid,"Generation" bigint)
                    LEFT JOIN normalization.input_resources p ON p.uuid=i."ResourceUuid"
                    WHERE p.uuid IS NULL OR p.current_revision_uuid IS DISTINCT FROM i."RevisionUuid" OR p.generation<>i."Generation")
                AND NOT EXISTS(
                    SELECT 1 FROM jsonb_to_recordset(@catalog::jsonb) AS i("ArtistUuid" uuid,"Generation" bigint)
                    LEFT JOIN normalization.catalog_artist_revisions p ON p.artist_uuid=i."ArtistUuid"
                    WHERE p.artist_uuid IS NULL OR p.generation<>i."Generation")
                AND NOT EXISTS(
                    SELECT 1 FROM jsonb_to_recordset(@identities::jsonb) AS i("UpstreamIdentifier" text,"Generation" bigint)
                    LEFT JOIN normalization.catalog_source_identity_revisions p ON p.upstream_identifier=i."UpstreamIdentifier"
                    WHERE p.upstream_identifier IS NULL OR p.generation<>i."Generation")
                AND NOT EXISTS(
                    SELECT 1 FROM jsonb_to_recordset(@sourceClocks::jsonb) AS i("SourceUuid" uuid,"Generation" bigint)
                    LEFT JOIN normalization.catalog_source_uuid_revisions p ON p.source_uuid=i."SourceUuid"
                    WHERE p.source_uuid IS NULL OR p.generation<>i."Generation")
                AND NOT EXISTS(
                    SELECT 1 FROM jsonb_to_recordset(@venues::jsonb) AS i("VenueUuid" uuid,"Generation" bigint)
                    LEFT JOIN normalization.catalog_venue_revisions p ON p.venue_uuid=i."VenueUuid"
                    WHERE p.venue_uuid IS NULL OR p.generation<>i."Generation")
                AND (@mappingGeneration::bigint IS NULL OR EXISTS(SELECT 1 FROM normalization.catalog_mapping_registry_revision
                    WHERE singleton AND generation=@mappingGeneration))
                AND NOT EXISTS(
                    SELECT 1 FROM jsonb_to_recordset(@dependencies::jsonb) AS i("SubjectUuid" uuid,"DesiredGeneration" bigint,"EditorialRevision" bigint,"PublishedWorkUuid" uuid)
                    LEFT JOIN normalization.work_subjects s ON s.uuid=i."SubjectUuid"
                    WHERE s.uuid IS NULL OR s.desired_generation<>i."DesiredGeneration" OR s.editorial_revision<>i."EditorialRevision"
                        OR s.published_work_uuid IS DISTINCT FROM i."PublishedWorkUuid")
                AND NOT EXISTS(
                    SELECT 1 FROM jsonb_to_recordset(@observations::jsonb) AS i("SubjectUuid" uuid,"DesiredGeneration" bigint,"EditorialRevision" bigint,"PublishedWorkUuid" uuid)
                    LEFT JOIN normalization.work_subjects s ON s.uuid=i."SubjectUuid"
                    WHERE s.uuid IS NULL OR s.desired_generation<>i."DesiredGeneration" OR s.editorial_revision<>i."EditorialRevision"
                        OR s.published_work_uuid IS DISTINCT FROM i."PublishedWorkUuid")
                AND NOT EXISTS(
                    SELECT 1 FROM jsonb_array_elements(@universes::jsonb) i
                    LEFT JOIN normalization.candidate_universe_revisions p ON p.domain=i->'Universe'->>'Domain'
                        AND p.scope_uuid=(i->'Universe'->>'ScopeUuid')::uuid
                    WHERE COALESCE(p.generation,0)<>(i->>'Generation')::bigint)
                """,
                new
                {
                    ticket.SubjectUuid,
                    ticket.WorkUuid,
                    ticket.DesiredGeneration,
                    ticket.EditorialRevision,
                    ticket.PolicyVersion,
                    ticketJson = JsonSerializer.Serialize(ticket),
                    proposalDesired = proposal.DesiredGeneration,
                    proposalEditorial = proposal.EditorialRevision,
                    proposalPublished = proposal.PublishedWorkUuid,
                    inputs = JsonSerializer.Serialize(ticket.Inputs),
                    catalog = JsonSerializer.Serialize(ticket.CatalogInputs ?? []),
                    identities = JsonSerializer.Serialize(ticket.SourceIdentities ?? []),
                    sourceClocks = JsonSerializer.Serialize(ticket.SourceUuids ?? []),
                    venues = JsonSerializer.Serialize(ticket.ImportedVenues ?? []),
                    mappingGeneration = ticket.MappingRegistry?.Generation,
                    dependencies = JsonSerializer.Serialize(ticket.Dependencies),
                    observations = JsonSerializer.Serialize(ticket.OwnerObservations ?? []),
                    universes = JsonSerializer.Serialize(ticket.CandidateUniverses ?? []),
                },
                t,
                cancellationToken: ct
            )
        );
    }

    private static async Task<Guid[]> ReadShowImportedFallbacks(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        VenueResolutionSource source,
        CancellationToken ct
    )
    {
        var observedImportedVenue =
            source.RecordingBinding?.Recording.Value?.SourceVenueUuid
            ?? source.SetlistBinding?.ImportedSetlist.Value?.VenueUuid;
        return (
            await c.QueryAsync<Guid>(
                new CommandDefinition(
                    """
                    SELECT v.uuid FROM shows sh JOIN artists a ON a.id=sh.artist_id
                        JOIN venues v ON v.id=sh.venue_id AND v.artist_id=sh.artist_id
                    WHERE sh.uuid=@showUuid AND a.uuid=@artistUuid
                    UNION SELECT v.uuid FROM venues v JOIN artists a ON a.id=v.artist_id
                    WHERE v.uuid=@observedImportedVenue AND a.uuid=@artistUuid
                    ORDER BY uuid
                    """,
                    new
                    {
                        showUuid = source.Claim!.CatalogUuid,
                        artistUuid = source.Claim.ArtistUuid,
                        observedImportedVenue,
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
    }

    private static async Task<NormalizationSubjectState?> ReadShowAssignmentOwner(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid showUuid,
        CancellationToken ct
    )
    {
        var row = await c.QuerySingleOrDefaultAsync<ImportedAssignmentOwner>(
            new CommandDefinition(
                """
                SELECT uuid AS "SubjectUuid",desired_generation AS "DesiredGeneration",editorial_revision AS "EditorialRevision",
                    policy_version AS "PolicyVersion",published_generation AS "PublishedGeneration",published_work_uuid AS "PublishedWorkUuid"
                FROM normalization.work_subjects WHERE domain='venues' AND subject_kind='show' AND subject_uuid=@showUuid
                """,
                new { showUuid },
                t,
                cancellationToken: ct
            )
        );
        return row == null
            ? null
            : new(
                row.SubjectUuid,
                new("venues", "show", showUuid),
                row.DesiredGeneration,
                row.EditorialRevision,
                row.PolicyVersion,
                row.PublishedGeneration,
                row.PublishedWorkUuid
            );
    }

    private static async Task<ImportedVenueAssignmentFact[]> ReadShowAssignmentFacts(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid showUuid,
        int maximumAssignments,
        int maximumEvidence,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        var rows = (
            await c.QueryAsync<ImportedAssignmentRow>(
                new CommandDefinition(
                    """
                    WITH assignments AS (
                        SELECT uuid,revision,canonical_venue_uuid,state,basis,reason_code,decision_uuid,
                            false AS historical,NULL::uuid AS superseding_decision_uuid
                        FROM normalization.show_venue_assignments WHERE show_uuid=@showUuid
                        UNION ALL
                        SELECT uuid,revision,canonical_venue_uuid,state,basis,reason_code,decision_uuid,
                            true AS historical,superseding_decision_uuid
                        FROM normalization.show_venue_assignments_history
                        WHERE show_uuid=@showUuid AND state='rejected' AND basis='reviewed'
                    )
                    SELECT a.uuid AS "AssignmentUuid",a.revision AS "Revision",a.canonical_venue_uuid AS "StoredVenueUuid",
                        r.resolved_uuid AS "ResolvedVenueUuid",a.state AS "State",a.basis AS "Basis",a.reason_code AS "ReasonCode",
                        a.decision_uuid AS "DecisionUuid",a.historical AS "IsHistorical",a.superseding_decision_uuid AS "SupersedingDecisionUuid"
                    FROM assignments a LEFT JOIN normalization.venue_identity_resolution r ON r.original_uuid=a.canonical_venue_uuid
                    ORDER BY a.uuid,a.revision,a.historical LIMIT @take
                    """,
                    new { showUuid, take = maximumAssignments + 1 },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (rows.Length > maximumAssignments)
        {
            throw new InvalidOperationException(
                "Show venue resolution exceeds its assignment and reviewed-rejection history limit."
            );
        }
        var evidence = await ReadShowAssignmentEvidence(c, t, rows, maximumEvidence, ct);
        return rows.Select(row =>
            {
                if (row.ResolvedVenueUuid == null)
                {
                    context.AddIssue(
                        CandidateFactsIssueKind.MissingRelatedIdentity,
                        row.StoredVenueUuid,
                        "assignment_target"
                    );
                }
                if (evidence[(row.AssignmentUuid, row.Revision)].Length == 0)
                {
                    context.AddIssue(
                        CandidateFactsIssueKind.InvalidStoredFact,
                        row.StoredVenueUuid,
                        "assignment_evidence"
                    );
                }
                return new ImportedVenueAssignmentFact(
                    row.AssignmentUuid,
                    row.Revision,
                    row.StoredVenueUuid,
                    row.ResolvedVenueUuid,
                    row.State,
                    row.Basis,
                    row.ReasonCode,
                    row.DecisionUuid,
                    row.IsHistorical,
                    row.SupersedingDecisionUuid,
                    evidence[(row.AssignmentUuid, row.Revision)]
                );
            })
            .ToArray();
    }

    private static async Task<
        Dictionary<(Guid Uuid, long Revision), NormalizedEvidenceReference[]>
    > ReadShowAssignmentEvidence(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        ImportedAssignmentRow[] rows,
        int maximumEvidence,
        CancellationToken ct
    )
    {
        var revisions = rows.Select(row => (row.AssignmentUuid, row.Revision)).Distinct().ToArray();
        var evidence = (
            await c.QueryAsync<ImportedAssignmentEvidence>(
                new CommandDefinition(
                    """
                    SELECT wanted.uuid AS "AssignmentUuid",wanted.revision AS "Revision",e.input_revision_uuid AS "InputRevisionUuid",
                        e.json_pointer AS "JsonPointer",e.start_code_point AS "StartCodePoint",e.end_code_point AS "EndCodePoint",e.quote AS "Quote"
                    FROM unnest(@ids::uuid[],@revisions::bigint[]) wanted(uuid,revision)
                    JOIN LATERAL (
                        SELECT input_revision_uuid,json_pointer,start_code_point,end_code_point,quote
                        FROM normalization.venue_assignment_evidence
                        WHERE show_assignment_uuid=wanted.uuid AND assignment_revision=wanted.revision
                        ORDER BY input_revision_uuid,json_pointer,start_code_point NULLS FIRST,end_code_point NULLS FIRST,quote NULLS FIRST
                        LIMIT @take
                    ) e ON true
                    """,
                    new
                    {
                        ids = revisions.Select(row => row.AssignmentUuid).ToArray(),
                        revisions = revisions.Select(row => row.Revision).ToArray(),
                        take = maximumEvidence + 1,
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToLookup(row => (row.AssignmentUuid, row.Revision));
        var result = new Dictionary<(Guid, long), NormalizedEvidenceReference[]>();
        foreach (var revision in revisions)
        {
            var citations = evidence[revision].ToArray();
            if (citations.Length > maximumEvidence)
            {
                throw new InvalidOperationException(
                    "Show venue resolution exceeds its assignment evidence limit."
                );
            }
            result.Add(
                revision,
                citations
                    .Select(row => new NormalizedEvidenceReference(
                        row.InputRevisionUuid,
                        row.JsonPointer,
                        row.StartCodePoint,
                        row.EndCodePoint,
                        row.Quote
                    ))
                    .ToArray()
            );
        }
        return result;
    }
}
