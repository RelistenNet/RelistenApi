using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRecordingProposalService
{
    private static async Task RequireInitiatingHandoff(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        AttemptRow attempt,
        NormalizationRecordingPreparation? preparation,
        CancellationToken ct
    )
    {
        if (attempt.InitiatingHandoffUuid is not { } handoffUuid)
        {
            return;
        }
        var original = await NormalizationRecordingHandoffEvidence.ReadAsync(c, t, handoffUuid, ct);
        if (
            original == null
            || !original.MatchesRequest(
                attempt.AttemptUuid,
                attempt.SourceUuid,
                attempt.ProcessingVersion
            )
            || preparation != null && !original.MatchesPreparation(preparation)
        )
        {
            throw new NormalizationConflictException(
                "Recording processing must retain its initiating handoff and original evidence."
            );
        }
    }
}
