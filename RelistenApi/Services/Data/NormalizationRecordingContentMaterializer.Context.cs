using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRecordingContentMaterializer
{
    private Task<Guid> ReadSourceDescriptorAsync(
        Guid repertoireAttemptUuid,
        CancellationToken ct
    ) =>
        db.WithWriteConnection(async c =>
        {
            var source = await c.QuerySingleOrDefaultAsync<Guid?>(
                new CommandDefinition(
                    """
                    SELECT p.source_uuid FROM normalization.repertoire_resolution_attempts a
                    JOIN normalization.repertoire_resolution_registrations r ON r.registration_uuid=a.registration_uuid
                        AND r.current_attempt_uuid=a.attempt_uuid
                    JOIN normalization.recording_processing_attempts p
                        ON p.attempt_uuid=(a.request->'Source'->>'PublishedProcessingAttemptUuid')::uuid
                    WHERE a.attempt_uuid=@repertoireAttemptUuid AND (a.result->>'Disposition')::integer=1
                        AND a.request->'Source'->>'kind'='recording_titles' AND p.disposition='published'
                        AND r.provider='relisten' AND r.resource_type='recording'
                        AND r.provider_key=p.source_uuid::text
                    """,
                    new { repertoireAttemptUuid },
                    cancellationToken: ct
                )
            );
            return source
                ?? throw new ArgumentException(
                    "Select a current completed recording repertoire account."
                );
        });

    private Task<bool> HasSupportedSourceShapeAsync(
        Guid repertoireAttemptUuid,
        CancellationToken ct
    ) =>
        db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    """
                    SELECT COALESCE((SELECT a.processing_version=@resolverVersion AND p.processing_version=@producerVersion
                        AND p.batch->>'ProcessingVersion'=@producerVersion
                        AND p.batch->'Ticket' IS NOT NULL
                        AND COALESCE(jsonb_array_length(p.batch->'Ticket'->'Dependencies'),0)=0
                        AND COALESCE(jsonb_array_length(p.batch->'Ticket'->'ImportedVenues'),0)=0
                        AND COALESCE(jsonb_array_length(p.batch->'Ticket'->'CandidateUniverses'),0)=0
                        AND COALESCE(jsonb_array_length(p.batch->'Ticket'->'OwnerObservations'),0)=0
                        FROM normalization.repertoire_resolution_attempts a JOIN normalization.recording_processing_attempts p
                            ON p.attempt_uuid=(a.request->'Source'->>'PublishedProcessingAttemptUuid')::uuid
                        WHERE a.attempt_uuid=@repertoireAttemptUuid),false)
                    """,
                    new
                    {
                        repertoireAttemptUuid,
                        resolverVersion = NormalizationRepertoireResolutionProcessor.Version,
                        producerVersion = NormalizationRecordingProcessor.Version,
                    },
                    cancellationToken: ct
                )
            )
        );

    private Task<CompletedRepertoireConsumption> ReadSelectionAsync(
        Guid repertoireAttemptUuid,
        CancellationToken ct
    ) =>
        db.WithWriteConnection(async connection =>
        {
            var c = (NpgsqlConnection)connection;
            await using var t = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await c.ExecuteAsync(
                new CommandDefinition(
                    "SET TRANSACTION READ ONLY",
                    transaction: t,
                    cancellationToken: ct
                )
            );
            var consumption = await repertoire.ReadCompletedConsumptionAsync(
                c,
                t,
                repertoireAttemptUuid,
                ct
            );
            await t.CommitAsync(ct);
            return consumption;
        });

    private async Task<RecordingContentMaterializationContext> ReadContextAsync(
        Guid repertoireAttemptUuid,
        CancellationToken ct
    )
    {
        await policies.ReadStateAsync(ct);
        return await db.WithWriteConnection(async connection =>
        {
            var c = (NpgsqlConnection)connection;
            await using var t = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await c.ExecuteAsync(
                new CommandDefinition(
                    "SET TRANSACTION READ ONLY",
                    transaction: t,
                    cancellationToken: ct
                )
            );
            var consumption = await repertoire.ReadCompletedConsumptionAsync(
                c,
                t,
                repertoireAttemptUuid,
                ct
            );
            var context = await ReadContextInSnapshotAsync(c, t, consumption, ct);
            await t.CommitAsync(ct);
            return context;
        });
    }

    internal static async Task<RecordingContentMaterializationContext> ReadContextInSnapshotAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        CompletedRepertoireConsumption consumption,
        CancellationToken ct
    )
    {
        var batch =
            consumption.Recording
            ?? throw new ArgumentException(
                "Recording contents require a completed recording repertoire account."
            );
        var owner = await NormalizationMaterializationOwners.ReadAsync(
            c,
            t,
            Owner(batch.SourceUuid),
            ct
        );
        var policy = await RepertoireConsumerPolicyService.ReadStateAsync(c, t, ct);
        var authorizations =
            await NormalizedMusicService.ReadRecordingContentAuthorizationHeadsAsync(
                c,
                t,
                batch.SourceUuid,
                ct,
                OwnerObservation(owner.Membership.Owner)
            );
        var authorizedTargets = authorizations
            .SelectMany(authorization => authorization.Targets)
            .Select(target => target.Uuid)
            .Distinct()
            .ToArray();
        var bound = await c.QuerySingleAsync<ContentBound>(
            new CommandDefinition(
                """
                SELECT count(*) AS "Count",COALESCE(sum(octet_length(r.payload::text)),0)::bigint AS "Bytes"
                FROM normalization.track_contents x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                WHERE (x.source_uuid=@source OR EXISTS(SELECT 1 FROM source_tracks tr JOIN sources s ON s.id=tr.source_id
                    WHERE tr.uuid=x.source_track_uuid AND s.uuid=@source))
                    AND (NOT x.retracted OR r.basis='reviewed' OR x.uuid=ANY(@authorizedTargets))
                """,
                new { source = batch.SourceUuid, authorizedTargets },
                t,
                cancellationToken: ct
            )
        );
        if (bound.Count > MaximumContents || bound.Bytes > MaximumImageBytes)
        {
            throw new MusicMaterializationCapacityException(
                "The complete accepted recording image exceeds its capacity profile.",
                OwnerObservation(owner.Membership.Owner)
            );
        }
        var rows = (
            await c.QueryAsync<ContentRow>(
                new CommandDefinition(
                    """
                    SELECT r.payload::text AS "Payload",r.revision AS "Revision",r.created_at AS "UpdatedAt",r.decision_uuid AS "DecisionUuid",
                        x.artist_uuid AS "ArtistUuid",x.source_uuid AS "SourceUuid",
                        EXISTS(SELECT 1 FROM normalization.recording_content_correspondences m WHERE m.content_uuid=x.uuid) AS "Managed"
                    FROM normalization.track_contents x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                    WHERE (x.source_uuid=@source OR EXISTS(SELECT 1 FROM source_tracks tr JOIN sources s ON s.id=tr.source_id
                        WHERE tr.uuid=x.source_track_uuid AND s.uuid=@source))
                        AND (NOT x.retracted OR r.basis='reviewed' OR x.uuid=ANY(@authorizedTargets)) ORDER BY x.uuid
                    """,
                    new { source = batch.SourceUuid, authorizedTargets },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var contents = rows.Select(row => new RecordingContentAcceptedRow(
                new(
                    JsonSerializer.Deserialize<TrackContentChange>(row.Payload)! with
                    {
                        ExpectedRevision = row.Revision,
                    },
                    row.Revision,
                    row.UpdatedAt,
                    row.DecisionUuid
                ),
                row.ArtistUuid,
                row.SourceUuid,
                row.Managed
            ))
            .ToArray();
        var tracks = consumption
            .Source.Claims.Where(claim => claim.TrackUuid != null)
            .Select(claim => claim.TrackUuid!.Value)
            .Concat(contents.Select(row => row.Record.Value.SourceTrackUuid))
            .Distinct()
            .Select(track => track.ToString("D"))
            .ToArray();
        var exclusionCount = await c.ExecuteScalarAsync<long>(
            new CommandDefinition(
                "SELECT count(DISTINCT claim_hash) FROM normalization.music_exclusions WHERE payload ? 'SourceTrackUuid' AND payload->>'SourceTrackUuid'=ANY(@tracks)",
                new { tracks },
                t,
                cancellationToken: ct
            )
        );
        if (exclusionCount > MaximumCorrespondences)
        {
            throw new MusicMaterializationCapacityException(
                "The complete recording exclusion scope exceeds its capacity profile.",
                OwnerObservation(owner.Membership.Owner)
            );
        }
        var exclusions = (
            await c.QueryAsync<string>(
                new CommandDefinition(
                    "SELECT DISTINCT claim_hash FROM normalization.music_exclusions WHERE payload ? 'SourceTrackUuid' AND payload->>'SourceTrackUuid'=ANY(@tracks) ORDER BY claim_hash",
                    new { tracks },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var history = await ReadCorrespondenceHistoryAsync(
            c,
            t,
            batch.SourceUuid,
            OwnerObservation(owner.Membership.Owner),
            ct
        );
        var sourceRead = SourceReadSet(consumption, policy);
        var contributor = Contribution(batch.SourceUuid);
        var peerReads = owner
            .Membership.Contributions.Where(item => item.Contributor != contributor)
            .Concat(owner.Membership.UnmanagedBaseline is { } baseline ? [baseline] : [])
            .Select(item =>
                RepertoireResolutionReadSets.ForContribution(
                    owner.Bindings,
                    item,
                    owner.InputReferences
                )
            );
        var read = RepertoireResolutionReadSets.Merge([sourceRead, .. peerReads]);
        return new()
        {
            Consumption = consumption,
            SourceUuid = batch.SourceUuid,
            ArtistUuid = batch.ArtistUuid,
            OwnerContext = owner,
            ReadSet = read,
            InputReferences = consumption
                .InputReferences.Concat(owner.InputReferences)
                .Distinct()
                .ToArray(),
            Policy = policy,
            Contents = contents,
            PriorCorrespondence = history.Maps,
            PriorSourceImages = history.Images,
            KnownCorrespondences = history.Known,
            Authorizations = authorizations,
            ReviewedContentExclusions = exclusions,
        };
    }

    internal static RepertoireResolutionReadSet SourceReadSet(
        CompletedRepertoireConsumption consumption,
        MusicMaterializationPolicyState policy
    ) =>
        RepertoireResolutionReadSets.Merge(
            consumption.ProducerReadSet,
            new(
                [],
                consumption
                    .AcceptedReadSet.Dependencies.Append(consumption.AcknowledgmentPublication)
                    .Append(
                        new(
                            policy.OwnerObservation.SubjectUuid,
                            policy.OwnerObservation.DesiredGeneration,
                            policy.OwnerObservation.EditorialRevision,
                            policy.OwnerObservation.PublishedWorkUuid
                        )
                    )
                    .ToArray(),
                [],
                [],
                null,
                [],
                [],
                [],
                []
            )
        );

    private static async Task<ContentHistory> ReadCorrespondenceHistoryAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid source,
        NormalizationSubjectObservation? owner,
        CancellationToken ct
    )
    {
        var bound = await c.QuerySingleAsync<ContentBound>(
            new CommandDefinition(
                """
                WITH ranked AS (
                    SELECT m.source_reference,row_number() OVER(PARTITION BY m.content_uuid,r.resource_uuid ORDER BY m.sequence DESC) AS rank
                    FROM normalization.recording_content_correspondence_heads h
                    JOIN normalization.recording_content_correspondences m ON m.uuid=h.correspondence_uuid
                    JOIN normalization.input_revisions r ON r.uuid=(m.source_reference->>'InputRevisionUuid')::uuid
                    WHERE m.source_uuid=@source)
                SELECT count(*) AS "Count",COALESCE(sum(octet_length(source_reference::text)),0)::bigint AS "Bytes"
                FROM ranked WHERE rank=1
                """,
                new { source },
                t,
                cancellationToken: ct
            )
        );
        if (bound.Count > MaximumCorrespondences || bound.Bytes > MaximumImageBytes)
        {
            throw new MusicMaterializationCapacityException(
                "The complete recording correspondence image exceeds its capacity profile.",
                owner
            );
        }
        var rows = (
            await c.QueryAsync<CorrespondenceRow>(
                new CommandDefinition(
                    """
                    WITH ranked AS (
                        SELECT m.*,row_number() OVER(PARTITION BY m.content_uuid,input.resource_uuid ORDER BY m.sequence DESC) AS rank
                        FROM normalization.recording_content_correspondence_heads h
                        JOIN normalization.recording_content_correspondences m ON m.uuid=h.correspondence_uuid
                        JOIN normalization.input_revisions input ON input.uuid=(m.source_reference->>'InputRevisionUuid')::uuid
                        WHERE m.source_uuid=@source)
                    SELECT m.attempt_uuid AS "AttemptUuid",m.decision_uuid AS "DecisionUuid",m.content_uuid AS "ContentUuid",
                        m.source_reference::text AS "Source",m.content_revision AS "Revision",m.withdrawn AS "Withdrawn",
                        m.authorization_uuid AS "AuthorizationUuid"
                    FROM ranked m JOIN normalization.recording_content_materialization_attempts a ON a.attempt_uuid=m.attempt_uuid
                    JOIN normalization.work_runs r ON r.uuid=(a.ticket->>'WorkUuid')::uuid
                    WHERE m.rank=1 AND a.receipt->>'Status'='0' AND r.state='published' AND r.ticket=a.ticket
                    ORDER BY m.sequence
                    """,
                    new { source },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        // Only published, immutable preparation supplies a complete previous file image. Ledger
        // heads alone prove individual mappings, not completeness of an older title interpretation.
        var priorIds = rows.Select(row => row.AttemptUuid).Distinct().ToArray();
        var bytes = await c.ExecuteScalarAsync<long>(
            new CommandDefinition(
                "SELECT COALESCE(sum(octet_length((preparation->'Context'->'Consumption')::text)),0)::bigint FROM normalization.recording_content_materialization_attempts WHERE attempt_uuid=ANY(@priorIds)",
                new { priorIds },
                t,
                cancellationToken: ct
            )
        );
        if (bytes > MaximumPreparationBytes)
        {
            throw new MusicMaterializationCapacityException(
                "The complete prior file source images exceed their snapshot budget.",
                owner
            );
        }
        var sources = await c.QueryAsync<string>(
            new CommandDefinition(
                "SELECT (preparation->'Context'->'Consumption')::text FROM normalization.recording_content_materialization_attempts WHERE attempt_uuid=ANY(@priorIds) ORDER BY sequence",
                new { priorIds },
                t,
                cancellationToken: ct
            )
        );
        var images = sources
            .SelectMany(json =>
                ProjectSourceImages(
                    JsonSerializer.Deserialize<CompletedRepertoireConsumption>(json)!
                )
            )
            .GroupBy(image =>
                (
                    image.Container.SourceLineage,
                    image.Container.AccountIdentity,
                    image.Container.TrackUuid
                )
            )
            .Select(group => group.Last())
            .ToArray();
        var maps = rows.Where(row => !row.Withdrawn)
            .Select(row => new RecordingContentCorrespondence(
                row.ContentUuid,
                row.Revision,
                JsonSerializer.Deserialize<RecordingContentSourceReference>(row.Source)!,
                row.AuthorizationUuid
            ))
            .ToArray();
        var known = rows.Where(row => !row.Withdrawn)
            .Select(row => new MusicUsageKnownCorrespondence(
                ClaimReference(
                    JsonSerializer.Deserialize<RecordingContentSourceReference>(row.Source)!
                ),
                row.ContentUuid,
                row.Revision,
                new(row.AttemptUuid, row.DecisionUuid)
            ))
            .ToArray();
        return new(maps, images, known);
    }

    internal static IReadOnlyList<MusicUsageSourceImage> ProjectSourceImages(
        CompletedRepertoireConsumption consumption
    )
    {
        var batch =
            consumption.Recording
            ?? throw new ArgumentException(
                "A recording source image requires its original extraction."
            );
        var groups = new[]
        {
            new NormalizationRecordingInputExtraction(
                batch.Preparation.Binding.InputRevisionUuid,
                batch.Extraction
            ),
        }.Concat(batch.AdditionalExtractions ?? []);
        var prepared = NormalizationRecordingReadSet.Inputs(batch.Preparation);
        var targets = consumption.Targets.ToDictionary(
            target => target.RequestedUuid,
            target => target.CanonicalUuid
        );
        var decisions = consumption.Decisions.ToDictionary(decision => decision.ClaimUuid);
        var claimsByProposal = consumption.Source.Claims.ToLookup(claim =>
            (claim.Origin.Evidence.InputRevisionUuid, claim.ProposalUuid)
        );
        var images = new List<MusicUsageSourceImage>();
        foreach (var group in groups)
        {
            var binding = prepared
                .First(item => item.Binding.InputRevisionUuid == group.InputRevisionUuid)
                .Binding;
            var files = binding
                .Files.Where(file => file.Binding.Value != null)
                .ToDictionary(file => file.Binding.Value!.TrackUuid);
            foreach (var proposal in group.Extraction.Titles)
            {
                var file = files[proposal.TrackUuid].Observation;
                var claims = claimsByProposal[(group.InputRevisionUuid, proposal.ProposalUuid)]
                    .ToArray();
                var positions =
                    proposal.Extraction?.Segments.ToDictionary(
                        segment =>
                            "recording:"
                            + proposal.ProposalUuid.ToString("D")
                            + ":segment:"
                            + segment.Position,
                        segment => segment.Position
                    )
                    ?? new Dictionary<string, int>();
                var facts = claims
                    .Select(claim =>
                    {
                        var position = positions.TryGetValue(
                            claim.CorrespondenceKey,
                            out var segmentPosition
                        )
                            ? segmentPosition
                            : (int?)null;
                        var decision = decisions[claim.ClaimUuid];
                        var song = decision.RepertoireSongUuid is { } selected
                            ? targets[selected]
                            : (Guid?)null;
                        return new MusicUsageClaimFact(
                            new(
                                batch.AttemptUuid,
                                group.InputRevisionUuid,
                                ProposalUuid: proposal.ProposalUuid,
                                TrackUuid: proposal.TrackUuid,
                                SegmentPosition: position
                            ),
                            position,
                            claim.Kind,
                            song,
                            null,
                            [claim.Origin.Evidence]
                        );
                    })
                    .ToArray();
                images.Add(
                    new(
                        MusicUsageKind.TrackContent,
                        new(
                            consumption.Source.Lineage,
                            binding.Resource,
                            proposal.TrackUuid,
                            file.Identities.Count == 1 ? file.Identities[0] : null
                        ),
                        Version,
                        RepertoireConsumerCorrespondenceEvaluator.SourceFactsVersion,
                        facts,
                        []
                    )
                );
            }
        }
        return images;
    }

    internal static MusicUsageClaimReference ClaimReference(
        RecordingContentSourceReference source
    ) =>
        new(
            source.ExtractionAttemptUuid,
            source.InputRevisionUuid,
            ProposalUuid: source.ProposalUuid,
            TrackUuid: source.TrackUuid,
            SegmentPosition: source.SegmentPosition
        );

    private sealed record ContentBound(long Count, long Bytes);

    private sealed record ContentRow(
        string Payload,
        long Revision,
        DateTime UpdatedAt,
        Guid DecisionUuid,
        Guid ArtistUuid,
        Guid SourceUuid,
        bool Managed
    );

    private sealed record CorrespondenceRow(
        Guid AttemptUuid,
        Guid DecisionUuid,
        Guid ContentUuid,
        string Source,
        long Revision,
        bool Withdrawn,
        Guid? AuthorizationUuid
    );

    private sealed record ContentHistory(
        IReadOnlyList<RecordingContentCorrespondence> Maps,
        IReadOnlyList<MusicUsageSourceImage> Images,
        IReadOnlyList<MusicUsageKnownCorrespondence> Known
    );

    private static NormalizationSubjectObservation? OwnerObservation(
        NormalizationSubjectState? owner
    ) =>
        owner == null
            ? null
            : new(
                owner.SubjectUuid,
                owner.DesiredGeneration,
                owner.EditorialRevision,
                owner.PublishedWorkUuid
            );
}
