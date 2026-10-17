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
using Relisten.Normalization;

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
                await ValidateExtractionEvidence(connection, transaction, frozen, ct);
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
        ValidateCurrentPreparation(batch.ProcessingVersion, batch.Preparation);
        ValidatePreparation(batch.SourceUuid, batch.Preparation);
        if (!NormalizationRecordingReadSet.IsComplete(batch.Ticket, batch.Preparation))
        {
            throw new ArgumentException(
                "Published proposals must consume the complete saved binding read set."
            );
        }
        var inputs = NormalizationRecordingReadSet.Inputs(batch.Preparation);
        var groups = ExtractionGroups(batch);
        if (
            groups.Select(group => group.InputRevisionUuid).Distinct().Count() != groups.Length
            || !groups
                .Select(group => group.InputRevisionUuid)
                .ToHashSet()
                .SetEquals(inputs.Select(input => input.Binding.InputRevisionUuid))
        )
        {
            throw new ArgumentException(
                "Recording proposals require one extraction for every distinct prepared input."
            );
        }
        var proposalUuids = groups
            .SelectMany(group =>
                group
                    .Extraction.Credits.Select(credit => credit.ProposalUuid)
                    .Concat(group.Extraction.Titles.Select(title => title.ProposalUuid))
            )
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
        foreach (var group in groups)
        {
            foreach (
                var input in inputs.Where(input =>
                    input.Binding.InputRevisionUuid == group.InputRevisionUuid
                )
            )
            {
                ValidateInputExtraction(
                    batch.SourceUuid,
                    batch.ArtistUuid,
                    input.Binding,
                    group.Extraction
                );
            }
        }
    }

    private static NormalizationRecordingInputExtraction[] ExtractionGroups(
        NormalizationRecordingProposalBatch batch
    ) =>
        [
            new(batch.Preparation.Binding.InputRevisionUuid, batch.Extraction),
            .. batch.AdditionalExtractions ?? [],
        ];

    private static void ValidateInputExtraction(
        Guid sourceUuid,
        Guid artistUuid,
        NormalizationRecordingBindingSnapshot binding,
        NormalizationRecordingExtraction extraction
    )
    {
        var sourceUuids = binding.SourceUuids ?? [];
        // Legacy capture reads an exact source UUID. Provider accounts ordinarily resolve
        // by qualified identifier; only actual retained-UUID lookups add UUID fences.
        if (
            binding.Resource.Provider == "legacy_catalog"
            && (
                sourceUuids.Count != 1
                || sourceUuids[0].SourceUuid != sourceUuid
                || sourceUuids[0].Generation < 0
            )
        )
        {
            throw new ArgumentException(
                "Recording proposals require the checked recording's source UUID fence."
            );
        }
        var recording = binding.Recording;
        if (
            recording.Status != NormalizationCatalogBindingStatus.Bound
            || recording.Value == null
            || recording.Value.SourceUuid != sourceUuid
            || recording.Value.ArtistUuid != artistUuid
        )
        {
            throw new ArgumentException(
                "Published recording proposals require their positively bound artist and source."
            );
        }
        var files = binding
            .Files.Where(file => file.Binding.Status == NormalizationCatalogBindingStatus.Bound)
            .ToDictionary(file => file.Binding.Value!.TrackUuid);
        var titleTracks = extraction.Titles.Select(title => title.TrackUuid).ToArray();
        if (
            titleTracks.Distinct().Count() != titleTracks.Length
            || !files.Keys.ToHashSet().SetEquals(titleTracks)
            || files.Values.Any(file =>
                file.Binding.Value!.SourceUuid != sourceUuid
                || file.Binding.Value.ArtistUuid != artistUuid
            )
            || extraction.Titles.Any(title =>
                title.Title != files[title.TrackUuid].Observation.Title
                || ((title.Title == null) != (title.Extraction == null))
                || title.Extraction != null
                    && (
                        title.Extraction.ArtistUuid != artistUuid
                        || title.Extraction.OriginalTitle != title.Title?.Value
                    )
            )
        )
        {
            throw new ArgumentException(
                "Each input's title proposals must cover its bound files once and retain their exact titles and recording scope."
            );
        }
    }

    private static async Task ValidateExtractionEvidence(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationRecordingProposalBatch batch,
        CancellationToken ct
    )
    {
        // Check size and resource identities for the complete immutable read set before
        // allocating payloads. Supporting roots may be shared by several directory groups.
        var prepared = NormalizationRecordingReadSet.Inputs(batch.Preparation);
        var retained = await NormalizationEvidenceService.GetInputsAsync(
            c,
            t,
            prepared.SelectMany(input => input.Binding.Inputs).Distinct().ToArray(),
            cancellationToken: ct
        );
        var inputs = retained.ToDictionary(input => input.Uuid);
        var decoder = new NormalizationProviderDecoder();
        var roots = new Dictionary<Guid, ProviderDecodeResult>();
        foreach (var group in ExtractionGroups(batch))
        {
            var binding = prepared
                .First(input => input.Binding.InputRevisionUuid == group.InputRevisionUuid)
                .Binding;
            var input = inputs[group.InputRevisionUuid];
            var decoded = decoder.Decode(input, ct);
            NormalizationInputRevision? root = null;
            NormalizationLocalRecordingContext? selection = null;
            if (input.Resource.Provider == "local")
            {
                root = inputs[binding.SupportingInputs!.Single().InputRevisionUuid];
                if (!roots.TryGetValue(root.Uuid, out var rootDecoded))
                {
                    rootDecoded = NormalizationLocalRecordingSelection.DecodeRoot(
                        root,
                        decoder,
                        cancellationToken: ct
                    );
                    roots.Add(root.Uuid, rootDecoded);
                }
                selection = NormalizationLocalRecordingSelection.Select(
                    input,
                    decoded,
                    binding,
                    root,
                    rootDecoded,
                    ct
                );
            }
            using var document = NormalizationRetainedDocument.Open(input, ct);
            using var rootDocument =
                root == null ? null : NormalizationRetainedDocument.Open(root, ct);
            foreach (var reference in ExtractionEvidence(group.Extraction))
            {
                if (reference.InputRevisionUuid == input.Uuid)
                {
                    document.ValidateEvidence(reference);
                }
                else if (reference.InputRevisionUuid == root?.Uuid)
                {
                    rootDocument!.ValidateEvidence(reference);
                }
                else
                {
                    throw new ArgumentException(
                        "Recording proposals must cite their own input or its explicit selected Local root account."
                    );
                }
            }
            if (
                selection != null
                && group.Extraction.DecodeIssues.Any(issue =>
                    issue.Evidence.InputRevisionUuid == root!.Uuid
                    && !selection.RootIssues.Contains(issue)
                )
            )
            {
                throw new ArgumentException(
                    "Local root issues must belong to the selected directory account."
                );
            }
            ValidateCreditScopes(
                input,
                document,
                decoded,
                selection?.RootAccount,
                group.Extraction
            );
        }
    }

    private static void ValidateCreditScopes(
        NormalizationInputRevision input,
        NormalizationRetainedDocument document,
        ProviderDecodeResult decoded,
        ProviderRecordingObservation? selectedRoot,
        NormalizationRecordingExtraction extraction
    )
    {
        var claims = extraction
            .Credits.Select(credit => (credit.FileOrigin, credit.Mention.Evidence))
            .Concat(
                extraction.UnresolvedCredits.Select(issue => (issue.FileOrigin, issue.Evidence))
            )
            .ToArray();
        var files = decoded
            .Recordings.SelectMany(recording => recording.Files)
            .ToDictionary(file => file.Origin);
        var recordingFields = decoded
            .Recordings.SelectMany(recording => recording.Text)
            .Concat(selectedRoot?.Text ?? [])
            .ToArray();
        foreach (var claim in claims)
        {
            if (claim.FileOrigin is { } origin)
            {
                if (
                    !files.TryGetValue(origin, out var file)
                    || !file.Text.Any(field => EvidenceWithin(field.Text.Evidence, claim.Evidence))
                )
                {
                    throw new ArgumentException(
                        "File credit proposals require an original decoded file origin."
                    );
                }
                NormalizationRecordingExtractor.ValidateFileCreditEvidence(
                    input,
                    document,
                    origin,
                    claim.Evidence
                );
            }
            else if (
                !recordingFields.Any(field => EvidenceWithin(field.Text.Evidence, claim.Evidence))
            )
            {
                throw new ArgumentException(
                    "Recording credits must cite original selected recording prose without discarding file scope."
                );
            }
        }
    }

    private static bool EvidenceWithin(
        NormalizedEvidenceReference parent,
        NormalizedEvidenceReference child
    )
    {
        var sameLocator = parent.JsonPointer == child.JsonPointer;
        return parent.InputRevisionUuid == child.InputRevisionUuid
            && (
                sameLocator
                || child.JsonPointer.StartsWith(parent.JsonPointer + "/", StringComparison.Ordinal)
            )
            && (
                parent.StartCodePoint == null
                || sameLocator
                    && child.StartCodePoint >= parent.StartCodePoint
                    && child.EndCodePoint <= parent.EndCodePoint
            );
    }

    private static IEnumerable<NormalizedEvidenceReference> ExtractionEvidence(
        NormalizationRecordingExtraction extraction
    ) =>
        extraction
            .Credits.Select(credit => credit.Mention.Evidence)
            .Concat(extraction.UnresolvedCredits.Select(issue => issue.Evidence))
            .Concat(extraction.DecodeIssues.Select(issue => issue.Evidence))
            .Concat(
                extraction
                    .Titles.Where(title => title.Title != null)
                    .Select(title => title.Title!.Evidence)
            )
            .Concat(TitleSegmentEvidence(extraction.Titles))
            .Concat(
                extraction
                    .Credits.Where(credit => credit.FileOrigin != null)
                    .Select(credit => credit.FileOrigin!.Evidence)
            )
            .Concat(
                extraction
                    .UnresolvedCredits.Where(issue => issue.FileOrigin != null)
                    .Select(issue => issue.FileOrigin!.Evidence)
            );

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
