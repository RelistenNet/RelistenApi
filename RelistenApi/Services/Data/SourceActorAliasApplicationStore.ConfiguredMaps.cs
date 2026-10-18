using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

/// <summary>
/// Planner consumption of an authenticated original accepted map. It carries allocation keys,
/// not new observations or reconstructed historical Work request authority.
/// </summary>
internal sealed record SourceActorAliasConfiguredMap(
    SourceActorAliasMapStage? Configured,
    SourceActorAliasMapStage? Committed
);

internal sealed record SourceActorAliasMapStage(
    Guid StageUuid,
    Guid WorkUuid,
    Guid ApplicationUuid,
    Guid OriginalPersonUuid,
    Guid OwnerSubjectUuid,
    Guid? PredecessorStageUuid,
    Guid? ConfiguredPredecessorStageUuid,
    long? ExpectedConfigurationGeneration,
    IReadOnlyList<NormalizationInputContributionRevision> ExpectedContributions,
    NormalizationResourceKey? UnmanagedBaseline,
    IReadOnlyList<SourceActorAliasContributionAllocation> Contributions,
    IReadOnlyList<SourceActorAliasScopeMapEntry> ResultingMap
);

/// <summary>Every allocation key family owned by one saved prepared contributor.</summary>
internal sealed record SourceActorAliasContributionAllocation(
    NormalizationInputContribution Contribution,
    IReadOnlyList<Guid> CatalogArtistUuids,
    IReadOnlyList<string> SourceIdentifiers,
    bool UsesMappingRegistry,
    IReadOnlyList<Guid> ImportedVenueUuids,
    IReadOnlyList<NormalizationCandidateUniverseKey> CandidateUniverses,
    IReadOnlyList<Guid> SourceUuids
)
{
    internal static SourceActorAliasContributionAllocation FromPrepared(
        NormalizationPreparedInputContribution value
    ) =>
        new(
            value.Contribution,
            (value.CatalogInputs ?? []).Select(item => item.ArtistUuid).ToArray(),
            (value.SourceIdentities ?? []).Select(item => item.UpstreamIdentifier).ToArray(),
            value.MappingRegistry != null,
            (value.ImportedVenues ?? []).Select(item => item.VenueUuid).ToArray(),
            (value.CandidateUniverses ?? []).Select(item => item.Universe).ToArray(),
            (value.SourceUuids ?? []).Select(item => item.SourceUuid).ToArray()
        );

    internal bool Matches(NormalizationInputContributionState current) =>
        current.Contributor == Contribution.Contributor
        && current.Resources.ToHashSet().SetEquals(Contribution.Resources)
        && current.DependencySubjectUuids.ToHashSet().SetEquals(Contribution.DependencySubjectUuids)
        && (current.OwnerObservationSubjectUuids ?? [])
            .ToHashSet()
            .SetEquals(Contribution.OwnerObservationSubjectUuids ?? [])
        && current.CatalogArtistUuids.ToHashSet().SetEquals(CatalogArtistUuids)
        && current.SourceIdentifiers.ToHashSet().SetEquals(SourceIdentifiers)
        && current.UsesMappingRegistry == UsesMappingRegistry
        && (current.ImportedVenueUuids ?? []).ToHashSet().SetEquals(ImportedVenueUuids)
        && (current.CandidateUniverses ?? []).ToHashSet().SetEquals(CandidateUniverses)
        && (current.SourceUuids ?? []).ToHashSet().SetEquals(SourceUuids);
}

public sealed partial class SourceActorAliasApplicationStore
{
    private static readonly JsonSerializerOptions ConfiguredMapReadOptions = new()
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// Plans from complete original maps in the caller's primary repeatable-read snapshot.
    /// Published certificates authenticate immutable acceptance; current membership and active
    /// authority are still checked by this reader and the planner. No observations are refreshed.
    /// Publication and audit retain the full original codec/admission reader.
    /// </summary>
    internal static async Task<SourceActorAliasConfiguredMap> ReadConfiguredMapInSnapshotAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid originalPersonUuid,
        NormalizationInputContributionsSnapshot membership,
        CancellationToken ct
    )
    {
        if (membership.Owner == null)
        {
            return new(null, null);
        }
        if (
            membership.Owner.Subject
            != new NormalizationSubjectKey("people", "person", originalPersonUuid)
        )
        {
            throw new ArgumentException(
                "Configured-map membership belongs to another original person."
            );
        }
        var sourceHeader = membership.Contributions.SingleOrDefault(value =>
            value.Contributor == StableContributor
        );
        if (sourceHeader == null)
        {
            return new(null, null);
        }
        var current = membership
            .Contributions.Select(value => new NormalizationInputContributionRevision(
                value.Contributor,
                value.Revision
            ))
            .Concat(
                membership.UnmanagedBaseline is { } baseline ? [new(baseline.Contributor, 0)] : []
            )
            .ToArray();
        RequireUniqueHeaders(current);
        var rows = (
            await c.QueryAsync<ConfiguredMapIdentityRow>(
                new CommandDefinition(
                    """
                    SELECT stage.stage_uuid AS "StageUuid",stage.application_uuid AS "ApplicationUuid",
                        coalesce(stage.receipt_header ? 'CertificateVersion',false) AS "HasVersion",
                        coalesce(stage.receipt_header ? 'AuthenticatedRequestHash',false) AS "HasRequest"
                    FROM normalization.source_actor_alias_person_stages stage
                    JOIN normalization.work_runs run ON run.uuid=stage.work_uuid
                    JOIN normalization.work_subjects owner ON owner.uuid=run.subject_uuid
                    WHERE stage.person_uuid=@originalPersonUuid AND stage.owner_subject_uuid=@ownerUuid
                        AND stage.contribution_revision=@revision
                        AND owner.domain='people' AND owner.subject_kind='person' AND owner.subject_uuid=@originalPersonUuid
                    """,
                    new
                    {
                        originalPersonUuid,
                        ownerUuid = membership.Owner.SubjectUuid,
                        revision = sourceHeader.Revision,
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();

        // Authenticate every present certificate before choosing compatibility.
        // An unrelated legacy candidate must not hide a contradictory certified candidate.
        var fullProofRequired = false;
        var certified = new List<ConfiguredMapIdentityRow>();
        foreach (var row in rows)
        {
            if (!row.HasVersion && !row.HasRequest)
            {
                fullProofRequired = true;
                continue;
            }
            await RequirePublishedStageHeader(c, t, row.ApplicationUuid, row.StageUuid, ct);
            certified.Add(row);
        }
        if (fullProofRequired)
        {
            return await ReadFullConfiguredMap(c, t, originalPersonUuid, membership, ct);
        }

        SourceActorAliasMapStage? selected = null;
        foreach (var row in certified)
        {
            var projected = await ReadPublishedMapProjection(
                c,
                t,
                row.ApplicationUuid,
                row.StageUuid,
                ct
            );
            if (
                !SameHeaders(
                    AdmittedHeaders(
                        projected.ExpectedContributions,
                        projected.Contributions.Select(value => value.Contribution).ToArray(),
                        projected.UnmanagedBaseline
                    ),
                    current
                )
            )
            {
                continue;
            }
            RequireConfiguredAllocations(projected.Contributions, membership.Contributions);
            if (selected != null)
            {
                throw new NormalizationConflictException(
                    "More than one source alias stage claims the exact configured header and revision vector."
                );
            }
            selected = projected;
        }
        return new(selected, selected);
    }

    private static async Task<SourceActorAliasConfiguredMap> ReadFullConfiguredMap(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid person,
        NormalizationInputContributionsSnapshot membership,
        CancellationToken ct
    )
    {
        var configured = await GetConfiguredStageInSnapshotAsync(c, t, person, membership, ct);
        if (configured != null && configured.Receipt == null && configured.TerminalReason == null)
        {
            throw new NormalizationConflictException(
                $"Resume saved source alias stage {configured.StageUuid:D} before preparing another person publication."
            );
        }
        var committed = configured;
        if (committed != null && committed.Receipt == null)
        {
            committed = committed.PredecessorStageUuid is Guid predecessor
                ? await ReadStage(c, t, predecessor, ct)
                : null;
        }
        if (committed != null && committed.Receipt == null)
        {
            throw new NormalizationConflictException(
                "A committed predecessor map is missing its accepted receipt."
            );
        }
        var ids = new[] { configured?.StageUuid, committed?.StageUuid }
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .Distinct()
            .ToArray();
        var applications = (
            await c.QueryAsync<MapApplicationRow>(
                new CommandDefinition(
                    "SELECT stage_uuid AS \"StageUuid\",application_uuid AS \"ApplicationUuid\" FROM normalization.source_actor_alias_person_stages WHERE stage_uuid=ANY(@ids)",
                    new { ids },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToDictionary(value => value.StageUuid, value => value.ApplicationUuid);
        return new(
            configured == null
                ? null
                : FromFullMapStage(configured, applications[configured.StageUuid]),
            committed == null
                ? null
                : FromFullMapStage(committed, applications[committed.StageUuid])
        );
    }

    private static SourceActorAliasMapStage FromFullMapStage(
        SourceActorAliasPersonStage value,
        Guid applicationUuid
    ) =>
        new(
            value.StageUuid,
            value.WorkUuid,
            applicationUuid,
            value.OriginalPersonUuid,
            value.OwnerSubjectUuid,
            value.PredecessorStageUuid,
            value.ConfiguredPredecessorStageUuid,
            value.ExpectedConfigurationGeneration,
            value.ExpectedContributions,
            value.Facts.Membership.UnmanagedBaseline?.Contributor,
            value
                .Contributions.Select(SourceActorAliasContributionAllocation.FromPrepared)
                .ToArray(),
            value.Receipt?.ResultingMap ?? []
        );

    private static void RequireConfiguredAllocations(
        IReadOnlyList<SourceActorAliasContributionAllocation> prepared,
        IReadOnlyList<NormalizationInputContributionState> current
    )
    {
        foreach (var actual in current)
        {
            var saved = prepared.Single(value =>
                value.Contribution.Contributor == actual.Contributor
            );
            if (!saved.Matches(actual))
            {
                throw new NormalizationConflictException(
                    "The configured person allocation differs from its exact saved complete source stage."
                );
            }
        }
    }

    private static async Task<SourceActorAliasMapStage> ReadPublishedMapProjection(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid applicationUuid,
        Guid stageUuid,
        CancellationToken ct
    )
    {
        // These are named projections of the v1 codec's immutable stage.Value.Value and
        // receipt.Value, not a second decoder or a partial reconstruction of Work authority.
        // Owner keys already belong to Contribution; pooled owner stamps are not consumed.
        var row = await c.QuerySingleAsync<ConfiguredMapProjectionRow>(
            new CommandDefinition(
                """
                WITH original AS MATERIALIZED (
                    SELECT stage.*,stage.stage#>'{Value,Value}' AS body,stage.receipt->'Value' AS accepted
                    FROM normalization.source_actor_alias_person_stages stage
                    WHERE stage.application_uuid=@applicationUuid AND stage.stage_uuid=@stageUuid
                ), projection AS MATERIALIZED (
                    SELECT jsonb_build_object(
                        'StageUuid',stage_uuid,'WorkUuid',work_uuid,'ApplicationUuid',application_uuid,
                        'OriginalPersonUuid',person_uuid,'OwnerSubjectUuid',owner_subject_uuid,
                        'PredecessorStageUuid',predecessor_stage_uuid,'ConfiguredPredecessorStageUuid',configured_predecessor_stage_uuid,
                        'ExpectedConfigurationGeneration',body->'ExpectedConfigurationGeneration',
                        'ExpectedContributions',body->'ExpectedContributions',
                        'UnmanagedBaseline',body#>'{Facts,Membership,UnmanagedBaseline,Contributor}',
                        'Contributions',(SELECT jsonb_agg(jsonb_build_object(
                            'Contribution',value->'Contribution',
                            'CatalogArtistUuids',(SELECT coalesce(jsonb_agg(item->'ArtistUuid'),'[]'::jsonb)
                                FROM jsonb_array_elements(coalesce(nullif(value->'CatalogInputs','null'::jsonb),'[]'::jsonb)) item),
                            'SourceIdentifiers',(SELECT coalesce(jsonb_agg(item->'UpstreamIdentifier'),'[]'::jsonb)
                                FROM jsonb_array_elements(coalesce(nullif(value->'SourceIdentities','null'::jsonb),'[]'::jsonb)) item),
                            'UsesMappingRegistry',coalesce(value->'MappingRegistry','null'::jsonb)<>'null'::jsonb,
                            'ImportedVenueUuids',(SELECT coalesce(jsonb_agg(item->'VenueUuid'),'[]'::jsonb)
                                FROM jsonb_array_elements(coalesce(nullif(value->'ImportedVenues','null'::jsonb),'[]'::jsonb)) item),
                            'CandidateUniverses',(SELECT coalesce(jsonb_agg(item->'Universe'),'[]'::jsonb)
                                FROM jsonb_array_elements(coalesce(nullif(value->'CandidateUniverses','null'::jsonb),'[]'::jsonb)) item),
                            'SourceUuids',(SELECT coalesce(jsonb_agg(item->'SourceUuid'),'[]'::jsonb)
                                FROM jsonb_array_elements(coalesce(nullif(value->'SourceUuids','null'::jsonb),'[]'::jsonb)) item)
                        )) FROM jsonb_array_elements(body->'Contributions') value),
                        'ResultingMap',accepted->'ResultingMap') AS payload,
                        (stage->>'Format'=@format AND stage->>'Kind'='stage'
                            AND receipt->>'Format'=@format AND receipt->>'Kind'='receipt'
                            AND body->>'StageUuid'=stage_uuid::text AND body->>'WorkUuid'=work_uuid::text
                            AND body->>'OriginalPersonUuid'=person_uuid::text AND body->>'OwnerSubjectUuid'=owner_subject_uuid::text
                            AND accepted->>'StageUuid'=stage_uuid::text AND accepted->>'WorkUuid'=work_uuid::text
                            AND accepted->>'OriginalPersonUuid'=person_uuid::text
                            AND jsonb_typeof(body->'ExpectedContributions')='array'
                            AND jsonb_typeof(body->'Contributions')='array'
                            AND jsonb_typeof(accepted->'ResultingMap')='array') AS matches
                    FROM original
                )
                SELECT octet_length(payload::text)::bigint AS "Bytes",matches AS "ShapeMatches",
                    CASE WHEN octet_length(payload::text)<=@maximumBytes THEN payload::text END AS "Payload"
                FROM projection
                """,
                new
                {
                    applicationUuid,
                    stageUuid,
                    format = SourceActorAliasApplicationCheckpointCodec.FormatVersion,
                    maximumBytes = SourceActorAliasApplicationCheckpointCodec.MaximumCheckpointBytes,
                },
                t,
                cancellationToken: ct
            )
        );
        if (row.Bytes > SourceActorAliasApplicationCheckpointCodec.MaximumCheckpointBytes)
        {
            throw new SourceActorReviewCapacityException(
                "source_alias_configured_map_capacity_exceeded"
            );
        }
        if (row.ShapeMatches != true || row.Payload == null)
        {
            throw new NormalizationConflictException(
                "The certified original map has an unsupported or incomplete retained shape."
            );
        }
        var value =
            JsonSerializer.Deserialize<SourceActorAliasMapStage>(
                row.Payload,
                ConfiguredMapReadOptions
            ) ?? throw new NormalizationConflictException("The certified original map is absent.");
        RequireProjectedMapShape(value);
        return value;
    }

    private static void RequireProjectedMapShape(SourceActorAliasMapStage value)
    {
        if (
            value.ExpectedContributions == null
            || value.Contributions == null
            || value.ResultingMap == null
            || value.Contributions.Any(item =>
                item == null
                || item.Contribution == null
                || item.Contribution.Resources == null
                || item.Contribution.DependencySubjectUuids == null
                || item.CatalogArtistUuids == null
                || item.SourceIdentifiers == null
                || item.ImportedVenueUuids == null
                || item.CandidateUniverses == null
                || item.SourceUuids == null
            )
            || value.ResultingMap.Any(entry =>
                entry == null
                || entry.Scope == null
                || entry.Bindings == null
                || entry.IncompleteObservations == null
                || entry.Bindings.Any(binding => binding == null || binding.Acceptance == null)
            )
        )
        {
            throw new NormalizationConflictException(
                "The certified original map must retain every required scope, binding and allocation collection."
            );
        }
    }

    private sealed record ConfiguredMapIdentityRow(
        Guid StageUuid,
        Guid ApplicationUuid,
        bool HasVersion,
        bool HasRequest
    );

    private sealed record MapApplicationRow(Guid StageUuid, Guid ApplicationUuid);

    private sealed record ConfiguredMapProjectionRow(
        long Bytes,
        bool? ShapeMatches,
        string? Payload
    );
}
