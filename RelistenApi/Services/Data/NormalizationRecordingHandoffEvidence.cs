using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// The initiating lookup's immutable evidence is a subset of a complete processing preparation.
/// Added peer inputs may extend that preparation; they cannot refresh the initiating facts,
/// legacy capture receipt or its original CAS revisions. Exact binding equality also retains
/// every supporting input, including a Local directory's original root revision.
/// </summary>
internal sealed record NormalizationRecordingHandoffEvidence(
    Guid HandoffUuid,
    Guid ProcessingAttemptUuid,
    NormalizationRecordingLookupMode Mode,
    NormalizationRecordingLookupPreparation Preparation
)
{
    internal static async Task<NormalizationRecordingHandoffEvidence?> ReadAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid handoffUuid,
        CancellationToken ct
    )
    {
        var row = await c.QuerySingleOrDefaultAsync<HandoffEvidenceRow>(
            new CommandDefinition(
                """
                SELECT h.processing_attempt_uuid AS "ProcessingAttemptUuid",l.mode AS "Mode",
                    a.preparation::text AS "PreparationJson"
                FROM normalization.recording_lookup_handoffs h
                JOIN normalization.recording_lookup_attempts a ON a.attempt_uuid=h.lookup_attempt_uuid
                JOIN normalization.recording_lookups l ON l.uuid=a.lookup_uuid WHERE h.uuid=@handoffUuid
                """,
                new { handoffUuid },
                t,
                cancellationToken: ct
            )
        );
        return row == null
            ? null
            : new(
                handoffUuid,
                row.ProcessingAttemptUuid,
                Enum.Parse<NormalizationRecordingLookupMode>(row.Mode),
                JsonSerializer.Deserialize<NormalizationRecordingLookupPreparation>(
                    row.PreparationJson
                )!
            );
    }

    internal bool MatchesRequest(Guid attemptUuid, Guid sourceUuid, string processingVersion) =>
        ProcessingAttemptUuid == attemptUuid
        && Preparation.Binding?.Recording.Value?.SourceUuid == sourceUuid
        && (
            Mode == NormalizationRecordingLookupMode.RetainedProvider
            || Preparation.ProcessingVersion == processingVersion
        );

    internal bool MatchesPreparation(NormalizationRecordingPreparation candidate)
    {
        if (!Same(candidate.Binding, Preparation.Binding))
        {
            return false;
        }
        if (Mode == NormalizationRecordingLookupMode.RetainedProvider)
        {
            return candidate.FetchUuid == null && candidate.ProducerCatalogInputs.Count == 0;
        }
        var original = Preparation.LegacyPreparation!;
        return candidate.FetchUuid == original.FetchUuid
            && Same(candidate.ProducerCatalogInputs, original.ProducerCatalogInputs)
            && candidate.ExpectedConfigurationGeneration == original.ExpectedConfigurationGeneration
            && candidate.ExpectedContributionRevision == original.ExpectedContributionRevision
            && (
                candidate.Contributor == original.Contributor
                || original.Contributor == null
                    && candidate.Contributor
                        == new NormalizationResourceKey(
                            "relisten",
                            "recording",
                            original.Binding.Resource.ProviderKey
                        )
            );
    }

    private static bool Same<T>(T left, T right) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(left),
            JsonSerializer.SerializeToElement(right)
        );

    private sealed record HandoffEvidenceRow(
        Guid ProcessingAttemptUuid,
        string Mode,
        string PreparationJson
    );
}
