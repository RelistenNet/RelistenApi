using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRecordingContentMaterializationStore
{
    // A receipt describes the whole attempted image. These retained heads describe the exact
    // accepted source-to-content associations, including explicit authorized withdrawal.
    internal async Task WriteCorrespondenceAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        RecordingContentMaterializationAttempt attempt,
        CancellationToken ct
    )
    {
        var preparation = attempt.Preparation!;
        foreach (var correspondence in preparation.Image!.Correspondence)
        {
            var decision = preparation.Decisions.Single(value =>
                value.ContentUuid == correspondence.ContentUuid
                && value.Source == correspondence.Source
                && !value.Withdrawal
                && value.Disposition
                    is MusicMaterializationDecisionDisposition.Accepted
                        or MusicMaterializationDecisionDisposition.Retained
            );
            await AppendCorrespondence(
                c,
                t,
                attempt,
                decision,
                correspondence.ContentUuid,
                correspondence.Source.CorrespondenceKey,
                JsonSerializer.Serialize(correspondence.Source),
                false,
                correspondence.AuthorizationUuid,
                ct
            );
        }
        foreach (var withdrawal in preparation.Image.Withdrawals)
        {
            var decision = preparation.Decisions.Single(value =>
                value.ContentUuid == withdrawal.ContentUuid
                && value.Withdrawal
                && value.Disposition == MusicMaterializationDecisionDisposition.Withdrawn
            );
            var rows = (
                await c.QueryAsync<SourceHead>(
                    new CommandDefinition(
                        """
                        SELECT m.source_claim_key AS "Key",m.source_reference::text AS "Source"
                        FROM normalization.recording_content_correspondence_heads h
                        JOIN normalization.recording_content_correspondences m ON m.uuid=h.correspondence_uuid
                        WHERE h.registration_uuid=@registration AND m.content_uuid=@content AND NOT m.withdrawn
                        ORDER BY m.source_claim_key
                        """,
                        new
                        {
                            registration = attempt.Registration.RegistrationUuid,
                            content = withdrawal.ContentUuid,
                        },
                        t,
                        cancellationToken: ct
                    )
                )
            ).ToArray();
            foreach (var row in rows)
            {
                await AppendCorrespondence(
                    c,
                    t,
                    attempt,
                    decision,
                    withdrawal.ContentUuid,
                    row.Key,
                    row.Source,
                    true,
                    withdrawal.AuthorizationUuid,
                    ct
                );
            }
        }
    }

    private static async Task AppendCorrespondence(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        RecordingContentMaterializationAttempt attempt,
        RecordingContentMaterializationDecision decision,
        Guid content,
        string key,
        string? source,
        bool withdrawn,
        Guid? authorization,
        CancellationToken ct
    )
    {
        var revision = await c.QuerySingleAsync<long>(
            new CommandDefinition(
                "SELECT r.revision FROM normalization.track_contents x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid WHERE x.uuid=@content AND x.source_uuid=@sourceUuid",
                new { content, sourceUuid = attempt.Registration.SourceUuid },
                t,
                cancellationToken: ct
            )
        );
        // Decision allocations are durable before Apply. The event UUID is deterministic from
        // those allocations and the source key so callback recovery cannot create a second event.
        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(decision.DecisionUuid.ToString("D") + "/" + key)
        );
        var uuid = new Guid(hash.AsSpan(0, 16));
        await c.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO normalization.recording_content_correspondences(uuid,registration_uuid,attempt_uuid,decision_uuid,
                    source_uuid,content_uuid,source_claim_key,source_reference,content_revision,withdrawn,authorization_uuid)
                VALUES(@uuid,@registration,@attemptUuid,@decisionUuid,@sourceUuid,@content,@key,@source::jsonb,@revision,@withdrawn,@authorization)
                """,
                new
                {
                    uuid,
                    registration = attempt.Registration.RegistrationUuid,
                    attemptUuid = attempt.AttemptUuid,
                    decisionUuid = decision.DecisionUuid,
                    sourceUuid = attempt.Registration.SourceUuid,
                    content,
                    key,
                    source,
                    revision,
                    withdrawn,
                    authorization,
                },
                t,
                cancellationToken: ct
            )
        );
        await c.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO normalization.recording_content_correspondence_heads(registration_uuid,source_claim_key,correspondence_uuid)
                VALUES(@registration,@key,@uuid) ON CONFLICT(registration_uuid,source_claim_key)
                DO UPDATE SET correspondence_uuid=EXCLUDED.correspondence_uuid
                """,
                new
                {
                    registration = attempt.Registration.RegistrationUuid,
                    key,
                    uuid,
                },
                t,
                cancellationToken: ct
            )
        );
    }

    private sealed record SourceHead(string Key, string? Source);
}
