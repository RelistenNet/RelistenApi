using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRecordingProposalService
{
    /// <summary>
    /// Checkpoints the first completed extraction before publication. Its exact facts remain
    /// available after cancellation, stale work or callback rollback. A different batch conflicts;
    /// an already-terminal attempt returns its winner unchanged. Saving does not make a batch current.
    /// </summary>
    public Task<NormalizationRecordingAttempt> SaveExtractionAsync(
        NormalizationRecordingProposalBatch batch,
        CancellationToken ct = default
    )
    {
        var json = JsonSerializer.Serialize(batch);
        var frozen = JsonSerializer.Deserialize<NormalizationRecordingProposalBatch>(json)!;
        return InTransaction(
            async (connection, transaction) =>
            {
                var work = await ReadExtractionWork(
                    connection,
                    transaction,
                    frozen.AttemptUuid,
                    ct
                );
                var row = await RequireAttempt(connection, transaction, frozen.AttemptUuid, ct);
                if (
                    row.SourceUuid != frozen.SourceUuid
                    || row.ProcessingVersion != frozen.ProcessingVersion
                )
                {
                    throw new NormalizationConflictException(
                        "Completed extraction does not match its attempt request."
                    );
                }
                if (row.Disposition != null)
                {
                    return row.ToAttempt();
                }
                ValidateExtractionWork(frozen, work);
                if (!SameJson(row.PreparationJson, JsonSerializer.Serialize(frozen.Preparation)))
                {
                    throw new NormalizationConflictException(
                        "Completed extraction does not match its saved recording preparation."
                    );
                }

                var previous = await ReadBatch(connection, transaction, frozen.AttemptUuid, ct);
                if (previous != null)
                {
                    if (!SameJson(previous, json))
                    {
                        throw new NormalizationConflictException(
                            "A different extraction already belongs to this attempt."
                        );
                    }
                    return row.ToAttempt();
                }

                ValidateBatch(frozen);
                var evidence = frozen
                    .Extraction.Credits.Select(credit => credit.Mention.Evidence)
                    .Concat(frozen.Extraction.UnresolvedCredits.Select(issue => issue.Evidence))
                    .Concat(frozen.Extraction.DecodeIssues.Select(issue => issue.Evidence))
                    .Concat(
                        frozen
                            .Extraction.Titles.Where(title => title.Title != null)
                            .Select(title => title.Title!.Evidence)
                    )
                    .Concat(TitleSegmentEvidence(frozen.Extraction.Titles))
                    .ToArray();
                var consumed = frozen.Ticket.Inputs.Select(input => input.RevisionUuid).ToHashSet();
                if (evidence.Any(reference => !consumed.Contains(reference.InputRevisionUuid)))
                {
                    throw new ArgumentException(
                        "Recording proposals cite evidence outside their publication ticket."
                    );
                }
                await NormalizedPeopleService.ValidateEvidenceAsync(
                    connection,
                    transaction,
                    evidence,
                    ct
                );
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE normalization.recording_processing_attempts SET batch=@json::jsonb,
                            extraction_work_uuid=attempt_uuid WHERE attempt_uuid=@AttemptUuid
                        """,
                        new { json, frozen.AttemptUuid },
                        transaction,
                        cancellationToken: ct
                    )
                );
                return row.ToAttempt();
            },
            ct
        );
    }

    /// <summary>Reads a completed extraction for audit, including unfinished or stale attempts. It does not establish freshness.</summary>
    public Task<NormalizationRecordingProposalBatch?> GetAttemptBatchAsync(
        Guid attemptUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<NormalizationRecordingProposalBatch?>(async connection =>
        {
            var json = await ReadBatch((NpgsqlConnection)connection, null, attemptUuid, ct);
            return json == null
                ? null
                : JsonSerializer.Deserialize<NormalizationRecordingProposalBatch>(json);
        });

    private static Task<string?> ReadBatch(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid attemptUuid,
        CancellationToken ct
    ) =>
        connection.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(
                "SELECT batch::text FROM normalization.recording_processing_attempts WHERE attempt_uuid=@attemptUuid",
                new { attemptUuid },
                transaction,
                cancellationToken: ct
            )
        );

    private static Task<ExtractionWorkRow?> ReadExtractionWork(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid attemptUuid,
        CancellationToken ct
    ) =>
        connection.QuerySingleOrDefaultAsync<ExtractionWorkRow>(
            new CommandDefinition(
                """
                SELECT s.domain AS "Domain",s.subject_kind AS "Kind",s.subject_uuid AS "SourceUuid",
                    r.state AS "State",r.ticket::text AS "TicketJson"
                FROM normalization.work_runs r JOIN normalization.work_subjects s ON s.uuid=r.subject_uuid
                WHERE r.uuid=@attemptUuid
                """,
                new { attemptUuid },
                transaction,
                cancellationToken: ct
            )
        );

    private static void ValidateExtractionWork(
        NormalizationRecordingProposalBatch batch,
        ExtractionWorkRow? work
    )
    {
        if (
            work == null
            || work.Domain != "extractions"
            || work.Kind != "recording"
            || work.SourceUuid != batch.SourceUuid
            || batch.Ticket.WorkUuid != batch.AttemptUuid
            || batch.Ticket.PolicyVersion != batch.ProcessingVersion
            || !SameJson(work.TicketJson, JsonSerializer.Serialize(batch.Ticket))
        )
        {
            throw new NormalizationConflictException(
                "Recording proposals require their exact extraction-owner publication ticket."
            );
        }
    }

    private static void ValidateBatch(NormalizationRecordingProposalBatch batch)
    {
        ValidatePreparation(batch.SourceUuid, batch.Preparation);
        if ((batch.Ticket.ImportedVenues?.Count ?? 0) > 0)
        {
            // This batch format has no separately consumed imported-venue facts.
            // Saving it must not let publication acknowledge another contributor's clock.
            throw new ArgumentException(
                "Recording proposals do not yet consume imported venue inputs."
            );
        }

        if ((batch.Ticket.OwnerObservations?.Count ?? 0) > 0)
        {
            throw new ArgumentException(
                "Recording proposals do not yet consume candidate owner observations."
            );
        }

        if ((batch.Ticket.CandidateUniverses?.Count ?? 0) > 0)
        {
            throw new ArgumentException(
                "Recording proposals do not yet consume candidate pool facts."
            );
        }
        var recording = batch.Preparation.Binding.Recording;
        if (
            recording.Status != NormalizationCatalogBindingStatus.Bound
            || recording.Value == null
            || recording.Value.SourceUuid != batch.SourceUuid
            || recording.Value.ArtistUuid != batch.ArtistUuid
        )
        {
            throw new ArgumentException(
                "Published recording proposals require their positively bound artist and source."
            );
        }
        if (
            batch.Preparation.Binding.Inputs.Any(input =>
                !batch.Ticket.Inputs.Any(consumed =>
                    consumed.RevisionUuid == input.InputRevisionUuid
                )
            )
            || batch.Preparation.Binding.CatalogInputs.Any(input =>
                !(batch.Ticket.CatalogInputs ?? []).Contains(input)
            )
            || batch.Preparation.Binding.SourceIdentities.Any(input =>
                !(batch.Ticket.SourceIdentities ?? []).Contains(input)
            )
            || batch.Preparation.Binding.MappingRegistry != batch.Ticket.MappingRegistry
        )
        {
            throw new ArgumentException(
                "Published proposals must consume the complete saved binding read set."
            );
        }

        var proposalUuids = batch
            .Extraction.Credits.Select(credit => credit.ProposalUuid)
            .Concat(batch.Extraction.Titles.Select(title => title.ProposalUuid))
            .ToArray();
        if (
            proposalUuids.Any(uuid => uuid == Guid.Empty)
            || proposalUuids.Distinct().Count() != proposalUuids.Length
        )
        {
            throw new ArgumentException(
                "Recording proposals need distinct nonempty proposal UUIDs."
            );
        }
        var files = batch
            .Preparation.Binding.Files.Where(file =>
                file.Binding.Status == NormalizationCatalogBindingStatus.Bound
            )
            .ToDictionary(file => file.Binding.Value!.TrackUuid);
        var titleTracks = batch.Extraction.Titles.Select(title => title.TrackUuid).ToArray();
        if (
            titleTracks.Distinct().Count() != titleTracks.Length
            || !files.Keys.ToHashSet().SetEquals(titleTracks)
            || files.Values.Any(file =>
                file.Binding.Value!.SourceUuid != batch.SourceUuid
                || file.Binding.Value.ArtistUuid != batch.ArtistUuid
            )
            || batch.Extraction.Titles.Any(title =>
                title.Title != files[title.TrackUuid].Observation.Title
                || ((title.Title == null) != (title.Extraction == null))
                || (
                    title.Extraction != null
                    && (
                        title.Extraction.ArtistUuid != batch.ArtistUuid
                        || title.Extraction.OriginalTitle != title.Title?.Value
                    )
                )
            )
        )
        {
            throw new ArgumentException(
                "Title proposals must cover each positively bound file once and retain its exact original title and recording scope."
            );
        }
    }

    private static IEnumerable<NormalizedEvidenceReference> TitleSegmentEvidence(
        IReadOnlyList<NormalizationRecordingTitleProposal> titles
    )
    {
        foreach (var title in titles)
        {
            if (title.Extraction == null)
            {
                continue;
            }
            var original = title.Title!.Evidence;
            var length = title.Extraction.OriginalTitle.EnumerateRunes().Count();
            var spans = title
                .Extraction.Segments.Select(segment => segment.Evidence)
                .Concat(title.Extraction.Qualifiers.Select(qualifier => qualifier.Evidence));
            foreach (var span in spans)
            {
                if (
                    span.StartCodePoint < 0
                    || span.EndCodePoint < span.StartCodePoint
                    || span.EndCodePoint > length
                )
                {
                    throw new ArgumentException(
                        "Title evidence must remain within its original title."
                    );
                }
                yield return new(
                    original.InputRevisionUuid,
                    original.JsonPointer,
                    (original.StartCodePoint ?? 0) + span.StartCodePoint,
                    (original.StartCodePoint ?? 0) + span.EndCodePoint,
                    span.Quote
                );
            }
        }
    }

    private sealed record ExtractionWorkRow(
        string Domain,
        string Kind,
        Guid SourceUuid,
        string State,
        string TicketJson
    );
}
