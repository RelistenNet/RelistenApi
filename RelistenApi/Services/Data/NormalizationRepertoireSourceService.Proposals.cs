using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRepertoireSourceService
{
    private async Task<RepertoireResolutionSourceSnapshot> ReadRecording(
        RecordingRepertoireResolutionSource source,
        CancellationToken ct
    )
    {
        var batch =
            await recordingProposals.GetAttemptBatchAsync(source.PublishedProcessingAttemptUuid, ct)
            ?? throw new ArgumentException("Unknown recording extraction batch.");
        if (
            batch.ProcessingVersion != NormalizationRecordingProcessor.Version
            || !NormalizationRecordingReadSet.IsComplete(batch.Ticket, batch.Preparation)
        )
        {
            throw new NormalizationConflictException(
                "Recording repertoire claims require a compatible complete extraction receipt."
            );
        }
        await RequirePublishedSource(source.PublishedProcessingAttemptUuid, batch.Ticket, true, ct);
        var retained = await ReadPublishedInputs(batch.Ticket, ct);
        var prepared = NormalizationRecordingReadSet.Inputs(batch.Preparation);
        var groups = new[]
        {
            new NormalizationRecordingInputExtraction(
                batch.Preparation.Binding.InputRevisionUuid,
                batch.Extraction
            ),
        }
            .Concat(batch.AdditionalExtractions ?? [])
            .ToArray();
        var claims = new List<RepertoireResolutionClaim>();
        var issues = new List<string>();
        foreach (var group in groups)
        {
            var original = prepared
                .Where(input => input.Binding.InputRevisionUuid == group.InputRevisionUuid)
                .ToArray();
            if (
                original.Length == 0
                || original.Any(input =>
                    input.Binding.Recording.Value?.SourceUuid != batch.SourceUuid
                    || input.Binding.Recording.Value?.ArtistUuid != batch.ArtistUuid
                )
            )
            {
                throw new NormalizationConflictException(
                    "A title group is outside its exact published source and artist binding."
                );
            }
            var binding = original[0].Binding;
            using var document = NormalizationRetainedDocument.Open(
                retained[group.InputRevisionUuid],
                ct
            );
            foreach (var proposal in group.Extraction.Titles)
            {
                var files = binding
                    .Files.Where(file =>
                        file.Binding.Status == NormalizationCatalogBindingStatus.Bound
                        && file.Binding.Value?.TrackUuid == proposal.TrackUuid
                    )
                    .ToArray();
                if (
                    files.Length != 1
                    || !SameSourceJson(files[0].Observation.Title, proposal.Title)
                )
                {
                    throw new NormalizationConflictException(
                        "A title proposal has no unique exact checked file binding."
                    );
                }
                var file = files[0].Observation;
                var key = "recording:" + proposal.ProposalUuid.ToString("D");
                if (
                    proposal.Title == null
                    || proposal.Extraction == null
                    || proposal.Extraction.Segments.Count == 0
                )
                {
                    claims.Add(
                        new(
                            ClaimUuid(key, proposal),
                            key,
                            proposal.Title,
                            "unknown",
                            file.Origin,
                            [],
                            [],
                            TrackUuid: proposal.TrackUuid,
                            ProposalUuid: proposal.ProposalUuid
                        )
                    );
                    continue;
                }
                document.ValidateEvidence(proposal.Title.Evidence);
                if (
                    proposal.Extraction.OriginalTitle != proposal.Title.Value
                    || proposal.Extraction.ArtistUuid != batch.ArtistUuid
                )
                {
                    throw new NormalizationConflictException(
                        "Title interpretation does not belong to the retained source wording and artist."
                    );
                }
                var segments = proposal.Extraction.Segments;
                foreach (var segment in segments)
                {
                    var citation = new NormalizedEvidenceReference(
                        proposal.Title.Evidence.InputRevisionUuid,
                        proposal.Title.Evidence.JsonPointer,
                        (proposal.Title.Evidence.StartCodePoint ?? 0)
                            + segment.Evidence.StartCodePoint,
                        (proposal.Title.Evidence.StartCodePoint ?? 0)
                            + segment.Evidence.EndCodePoint,
                        segment.Evidence.Quote
                    );
                    document.ValidateEvidence(citation);
                    var correspondence = key + ":segment:" + segment.Position;
                    // File membership is not a positional map. Only a single song mention and
                    // a single supplied song identity have an unambiguous file-level endpoint.
                    var identities =
                        segments.Count == 1
                        && segment.Kind == TrackContentKind.Song
                        && file.SongIdentities.Count == 1
                            ? file.SongIdentities
                            : [];
                    claims.Add(
                        new(
                            ClaimUuid(correspondence, new { proposal.ProposalUuid, segment }),
                            correspondence,
                            new(segment.Title ?? segment.Evidence.Quote, citation),
                            segment.Kind.ToString().ToLowerInvariant(),
                            file.Origin with
                            {
                                Evidence = citation,
                            },
                            identities,
                            [],
                            TrackUuid: proposal.TrackUuid,
                            ProposalUuid: proposal.ProposalUuid
                        )
                    );
                }
            }
        }
        return new(
            new("relisten", "recording", batch.SourceUuid.ToString("D")),
            batch.ArtistUuid,
            claims,
            PublishedReadSet(batch.Ticket),
            RepertoireResolutionPolicyEvaluator.SourceVersion,
            issues
        );
    }

    private async Task<RepertoireResolutionSourceSnapshot> ReadSetlist(
        SetlistRepertoireResolutionSource source,
        CancellationToken ct
    )
    {
        var batch =
            await setlistProposals.GetAttemptBatchAsync(source.PublishedProcessingAttemptUuid, ct)
            ?? throw new ArgumentException("Unknown setlist extraction batch.");
        if (
            batch.ProcessingVersion != NormalizationSetlistProcessor.Version
            || !NormalizationSetlistReadSet.IsComplete(batch.Ticket, batch.Preparation)
        )
        {
            throw new NormalizationConflictException(
                "Setlist repertoire claims require a compatible complete extraction receipt."
            );
        }
        await RequirePublishedSource(
            source.PublishedProcessingAttemptUuid,
            batch.Ticket,
            false,
            ct
        );
        var retained = await ReadPublishedInputs(batch.Ticket, ct);
        var claims = new List<RepertoireResolutionClaim>();
        var artists = new HashSet<Guid>();
        var unbound = false;
        foreach (var group in batch.Extractions)
        {
            var prepared = batch.Preparation.Inputs.Single(input =>
                input.Input.InputRevisionUuid == group.InputRevisionUuid
            );
            using var document = NormalizationRetainedDocument.Open(
                retained[group.InputRevisionUuid],
                ct
            );
            if (prepared.Accounts.Count != group.Accounts.Count)
            {
                throw new NormalizationConflictException(
                    "Published setlist accounts do not reproduce their complete original preparation."
                );
            }
            for (var index = 0; index < group.Accounts.Count; index++)
            {
                var account = group.Accounts[index];
                var binding = prepared.Accounts[index].Binding;
                if (
                    !SameSourceJson(account.Account, prepared.Accounts[index].Account)
                    || binding.Account != account.Account.Origin
                )
                {
                    throw new NormalizationConflictException(
                        "A passage account is outside its exact published binding."
                    );
                }
                if (
                    binding.Artist.Status == NormalizationCatalogBindingStatus.Bound
                    && binding.Artist.Value != null
                )
                {
                    artists.Add(binding.Artist.Value.ArtistUuid);
                }
                else
                {
                    unbound = true;
                }
                foreach (var proposal in account.Passages)
                {
                    var passage = proposal.Passage;
                    document.ValidateEvidence(passage.Title.Evidence);
                    var key = "setlist:" + proposal.ProposalUuid.ToString("D");
                    var kind = SetlistClaimKind(passage);
                    claims.Add(
                        new(
                            ClaimUuid(key, proposal),
                            key,
                            passage.Title,
                            kind,
                            account.Account.Origin with
                            {
                                Evidence = passage.Title.Evidence,
                            },
                            passage.SongIdentities,
                            [],
                            ProposalUuid: proposal.ProposalUuid
                        )
                    );
                }
            }
        }
        var issues = new List<string>();
        if (unbound || artists.Count == 0)
        {
            issues.Add("source_artist_unbound");
        }
        if (artists.Count > 1)
        {
            issues.Add("source_artist_ambiguous");
        }
        return new(
            new("relisten", "setlist", batch.RegistrationUuid.ToString("D")),
            !unbound && artists.Count == 1 ? artists.Single() : null,
            claims,
            PublishedReadSet(batch.Ticket),
            RepertoireResolutionPolicyEvaluator.SourceVersion,
            issues
        );
    }

    private static string SetlistClaimKind(ProviderPassageObservation passage)
    {
        if (passage.SongIdentities.Count > 0)
        {
            return "song";
        }
        var parsed = new TrackTitleExtractor().Extract(Guid.Empty, passage.Title.Value);
        return parsed.Segments.Count == 1
            ? parsed.Segments[0].Kind.ToString().ToLowerInvariant()
            : "unknown";
    }

    private async Task RequirePublishedSource(
        Guid attempt,
        NormalizationWorkTicket ticket,
        bool recording,
        CancellationToken ct
    )
    {
        var table = recording ? "recording_processing_attempts" : "setlist_processing_attempts";
        var kind = recording ? "recording" : "setlist";
        var key = recording ? "source_uuid" : "registration_uuid";
        var valid = await db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    $"""
                    SELECT EXISTS(SELECT 1 FROM normalization.{table} a
                        JOIN normalization.work_runs r ON r.uuid=a.attempt_uuid AND r.state='published'
                        JOIN normalization.work_subjects s ON s.uuid=r.subject_uuid AND s.domain='extractions'
                            AND s.subject_kind=@kind AND s.subject_uuid=a.{key} AND s.published_work_uuid=a.attempt_uuid
                        WHERE a.attempt_uuid=@attempt AND a.disposition='published' AND a.batch IS NOT NULL
                            AND r.ticket=@ticketJson::jsonb AND r.subject_uuid=@SubjectUuid)
                    """,
                    new
                    {
                        attempt,
                        kind,
                        ticketJson = JsonSerializer.Serialize(ticket),
                        ticket.SubjectUuid,
                    },
                    cancellationToken: ct
                )
            )
        );
        if (!valid)
        {
            throw new NormalizationConflictException(
                "Only the exact current published extraction can supply repertoire claims."
            );
        }
    }

    private async Task<IReadOnlyDictionary<Guid, NormalizationInputRevision>> ReadPublishedInputs(
        NormalizationWorkTicket ticket,
        CancellationToken ct
    )
    {
        if (ticket.Inputs.Count > 100)
        {
            throw new InvalidOperationException(
                "Repertoire source extraction exceeds its complete 100-input limit."
            );
        }
        var metadata = await ReadInputMetadata(
            ticket.Inputs.Select(input => input.RevisionUuid).ToArray(),
            ct
        );
        if (
            metadata.Length != ticket.Inputs.Count
            || ticket.Inputs.Any(input =>
                !metadata.Any(row =>
                    row.RevisionUuid == input.RevisionUuid && row.ResourceUuid == input.ResourceUuid
                )
            )
        )
        {
            throw new NormalizationConflictException(
                "An exact original extraction input is missing."
            );
        }
        return (
            await evidence.GetInputsAsync(
                metadata
                    .Select(row => new NormalizationBindingInputReference(
                        row.RevisionUuid,
                        row.Resource
                    ))
                    .ToArray(),
                new(100, 32 * 1024 * 1024, 128 * 1024 * 1024),
                ct
            )
        ).ToDictionary(input => input.Uuid);
    }

    private static RepertoireResolutionReadSet PublishedReadSet(NormalizationWorkTicket ticket) =>
        new(
            ticket.Inputs,
            ticket
                .Dependencies.Append(
                    new(
                        ticket.SubjectUuid,
                        ticket.DesiredGeneration,
                        ticket.EditorialRevision,
                        ticket.WorkUuid
                    )
                )
                .Distinct()
                .ToArray(),
            ticket.CatalogInputs ?? [],
            ticket.SourceIdentities ?? [],
            ticket.MappingRegistry,
            ticket.ImportedVenues ?? [],
            ticket.CandidateUniverses ?? [],
            ticket.OwnerObservations ?? [],
            ticket.SourceUuids ?? []
        );

    private static bool SameSourceJson<T>(T a, T b) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(a),
            JsonSerializer.SerializeToElement(b)
        );
}
