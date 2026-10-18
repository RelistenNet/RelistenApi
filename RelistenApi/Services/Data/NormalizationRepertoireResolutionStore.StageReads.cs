using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRepertoireResolutionStore
{
    /// <summary>Loads one original audit stage; GetAsync deliberately projects completed history.</summary>
    public Task<RepertoireResolutionStage?> GetStageAsync(
        Guid attemptUuid,
        Guid stageUuid,
        CancellationToken ct = default
    ) =>
        InTransaction(
            async (c, t) =>
            {
                await c.ExecuteScalarAsync<Guid?>(
                    new CommandDefinition(
                        "SELECT attempt_uuid FROM normalization.repertoire_resolution_attempts WHERE attempt_uuid=@attemptUuid FOR UPDATE",
                        new { attemptUuid },
                        t,
                        cancellationToken: ct
                    )
                );
                return await ReadStage(c, t, attemptUuid, stageUuid, ct);
            },
            ct
        );

    internal Task<RepertoireResolutionStage?> GetStageInTransactionAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid attemptUuid,
        Guid stageUuid,
        CancellationToken ct
    ) => ReadStage(c, t, attemptUuid, stageUuid, ct);

    private static async Task<RepertoireResolutionStage?> ReadStage(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid attemptUuid,
        Guid stageUuid,
        CancellationToken ct
    )
    {
        var bytes = await c.ExecuteScalarAsync<long?>(
            new CommandDefinition(
                "SELECT octet_length(stage::text)+COALESCE(octet_length(ticket::text),0)+COALESCE(octet_length(receipt::text),0) FROM normalization.repertoire_resolution_stages WHERE attempt_uuid=@attemptUuid AND stage_uuid=@stageUuid",
                new { attemptUuid, stageUuid },
                t,
                cancellationToken: ct
            )
        );
        if (bytes == null)
        {
            return null;
        }
        // Stage and ticket each have an independent 32 MiB write bound. No preceding page
        // is materialized to inspect this exact original source/candidate preparation.
        if (bytes > 2L * MaximumCheckpointBytes)
        {
            throw new InvalidOperationException(
                "The saved repertoire stage exceeds its bounded stage and ticket representations."
            );
        }
        var row = await c.QuerySingleAsync<StageRow>(
            new CommandDefinition(
                "SELECT stage::text AS \"Stage\",ticket::text AS \"Ticket\",receipt::text AS \"Receipt\",superseded AS \"Superseded\",failure_reason AS \"FailureReason\",own_effects::text AS \"OwnEffects\" FROM normalization.repertoire_resolution_stages WHERE attempt_uuid=@attemptUuid AND stage_uuid=@stageUuid",
                new { attemptUuid, stageUuid },
                t,
                cancellationToken: ct
            )
        );
        return DecodeStage(row);
    }

    private static RepertoireResolutionStage DecodeStage(StageRow row) =>
        JsonSerializer.Deserialize<RepertoireResolutionStage>(row.Stage)! with
        {
            Ticket =
                row.Ticket == null
                    ? null
                    : JsonSerializer.Deserialize<NormalizationWorkTicket>(row.Ticket),
            Receipt =
                row.Receipt == null
                    ? null
                    : JsonSerializer.Deserialize<NormalizationPublicationResult>(row.Receipt),
            Superseded = row.Superseded,
            FailureReason = row.FailureReason,
            OwnEffects =
                row.OwnEffects == null
                    ? null
                    : JsonSerializer.Deserialize<RepertoireResolutionOwnEffects>(row.OwnEffects),
        };

    private static async Task RequireSummarySize(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid uuid,
        CancellationToken ct,
        bool includeSourceClaims = true
    )
    {
        var bytes = await c.ExecuteScalarAsync<long>(
            new CommandDefinition(
                includeSourceClaims ? SummarySizeSql : PublicSummarySizeSql,
                new { selected = new[] { uuid } },
                t,
                cancellationToken: ct
            )
        );
        if (bytes > MaximumSummaryBytes)
        {
            throw new InvalidOperationException(
                "Repertoire source and projected history exceed the 256 MiB summary bound."
            );
        }
    }

    // Processed page decisions remain available for source acknowledgment. Their full candidate
    // pools and binding unions stay immutable in storage and are fetched only by exact stage UUID.
    // The latest undecided page and unfinished Work stage retain their complete context.
    private const string CompactStageCondition = """
        ((s.stage->>'Kind')::integer<>3 AND (s.receipt IS NOT NULL OR s.superseded))
        OR ((s.stage->>'Kind')::integer=3 AND EXISTS(
            SELECT 1 FROM normalization.repertoire_resolution_stages later
            WHERE later.attempt_uuid=s.attempt_uuid AND (
                ((later.stage->>'Kind')::integer=0 AND later.receipt IS NOT NULL
                    AND later.stage->>'PageIndex'=s.stage->>'PageIndex')
                OR ((later.stage->>'Kind')::integer=3 AND later.ordinal>s.ordinal)
                OR (later.stage->>'Kind')::integer=2)))
        """;

    private const string EmptyReadJson = """
        '{"Inputs":[],"Dependencies":[],"CatalogInputs":[],"SourceIdentities":[],"MappingRegistry":null,"ImportedVenues":[],"CandidateUniverses":[],"OwnerObservations":[],"SourceUuids":[]}'::jsonb
        """;

    private const string StageProjection =
        "CASE WHEN ("
        + CompactStageCondition
        + ") THEN "
        + """
            (s.stage-'Context'-'Publication'-'ReadSet'-'Decisions') || jsonb_build_object(
                'ContextOmitted',true,
                'Context',jsonb_build_object('ArtistUuid',s.stage->'Context'->'ArtistUuid','Claims','[]'::jsonb,
                    'ReadSet',
            """
        + EmptyReadJson
        + ", 'ObservationOwner',jsonb_build_object('Membership',jsonb_build_object('Owner',null,'Contributions','[]'::jsonb,'UnmanagedBaseline',null),'Bindings',"
        + EmptyReadJson
        + ", 'InputReferences','[]'::jsonb),"
        + """
                    'ArtistOwner',null,'Policy',s.stage->'Context'->'Policy','Issues','[]'::jsonb),
                'Publication','{}'::jsonb,'ReadSet',
            """
        + EmptyReadJson
        + ", 'Decisions',CASE WHEN (s.stage->>'Kind')::integer=3 THEN COALESCE((SELECT jsonb_agg((d.value-'CandidateUuids') || jsonb_build_object('CandidateUuids','[]'::jsonb) ORDER BY d.ordinality) FROM jsonb_array_elements(s.stage->'Decisions') WITH ORDINALITY d(value,ordinality)), '[]'::jsonb) ELSE '[]'::jsonb END) ELSE s.stage END";

    private const string StageSummarySql =
        "SELECT ("
        + StageProjection
        + ")::text AS \"Stage\",(CASE WHEN ("
        + CompactStageCondition
        + ") THEN NULL ELSE ticket::text END) AS \"Ticket\",receipt::text AS \"Receipt\",superseded AS \"Superseded\",failure_reason AS \"FailureReason\",own_effects::text AS \"OwnEffects\" FROM normalization.repertoire_resolution_stages s WHERE s.attempt_uuid=@uuid ORDER BY s.ordinal";

    // Source coordinates and all original clocks remain visible; the 50,000-claim source
    // array is loaded only by the processor, never by a public status/recovery request.
    private const string PublicPreparationProjection = """
        (CASE WHEN a.preparation IS NULL THEN NULL ELSE jsonb_set(a.preparation,'{Source}',
            ((a.preparation->'Source')-'Claims') || jsonb_build_object('Claims','[]'::jsonb,
                'TotalClaims',jsonb_array_length(a.preparation->'Source'->'Claims'),'ClaimsOmitted',true)) END)
        """;

    private static readonly string PublicSummarySizeSql = SummarySizeSql.Replace(
        "octet_length(a.preparation::text)",
        "octet_length((" + PublicPreparationProjection + ")::text)",
        StringComparison.Ordinal
    );

    private const string SummarySizeSql =
        """
            SELECT COALESCE(sum(octet_length(a.registration::text)+octet_length(a.request::text)
                +COALESCE(octet_length(a.preparation::text),0)+COALESCE(octet_length(a.result::text),0)
                +COALESCE((SELECT sum(octet_length((
            """
        + StageProjection
        + ")::text)+CASE WHEN ("
        + CompactStageCondition
        + ") THEN 0 ELSE COALESCE(octet_length(s.ticket::text),0) END+COALESCE(octet_length(s.receipt::text),0)+COALESCE(octet_length(s.failure_reason),0)+COALESCE(octet_length(s.own_effects::text),0)) FROM normalization.repertoire_resolution_stages s WHERE s.attempt_uuid=a.attempt_uuid),0)),0)::bigint FROM normalization.repertoire_resolution_attempts a WHERE a.attempt_uuid=ANY(@selected)";
}
