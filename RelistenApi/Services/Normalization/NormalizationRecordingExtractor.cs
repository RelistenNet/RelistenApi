using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>
/// Builds local recording proposals from retained assertions and their checked catalog associations.
/// It does not search candidates, allocate canonical identities or accept inferred matches.
/// </summary>
public sealed partial class NormalizationRecordingExtractor(
    RetainedRecordingCreditExtractor credits,
    TrackTitleExtractor titles
)
{
    // Adding a parallel extraction must not change the existing credit/title proposal IDs.
    // Their identity version still advances if any of their actual producers changes.
    private const string CreditAndTitleProposalVersion =
        "recording-extraction-v4/"
        + RetainedRecordingCreditExtractor.Version
        + "/"
        + TrackTitleExtractor.Version
        + "/"
        + NormalizationLocalRecordingSelection.Version;
    public const string Version =
        "recording-source-actor-envelope-v2/"
        + CreditAndTitleProposalVersion
        + "/"
        + RetainedSourceActorAliasExtractor.Version;
    private readonly NormalizationRecordingSourceActorAliasBuilder sourceActors = new();

    /// <summary>
    /// Extracts recording and file credits even when playback context is incomplete. File
    /// credits keep their retained file origin, including for unbound files. Only positively
    /// bound files receive title proposals. Retain the binding, unresolved credits and decoder
    /// issues with this result; an unresolved file must not disappear from the batch. Role-free
    /// aliases retain separate original scopes, including unresolved statements and coverage.
    /// </summary>
    /// <remarks>
    /// Credit/title proposal UUIDs use their unchanged producer identity version rather than
    /// this expanded envelope's version. Alias UUIDs retain their own producer/input/field
    /// scope. None identifies a canonical person or song. Title segment offsets remain relative
    /// to the original title; they describe neither audio times nor separate performances.
    /// </remarks>
    public NormalizationRecordingExtraction Extract(
        NormalizationInputRevision input,
        ProviderDecodeResult decoded,
        NormalizationRecordingBindingSnapshot binding,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var recording = RequireRecording(input, decoded, binding);
        var owner = binding.Recording.Value!;
        using var document = NormalizationRetainedDocument.Open(input, cancellationToken);
        document.ValidateEvidence(recording.Origin.Evidence);
        foreach (var issue in decoded.Issues)
        {
            document.ValidateEvidence(issue.Evidence);
        }

        var textProposals = ExtractTextProposals(
            input,
            recording,
            owner.SourceUuid,
            document,
            cancellationToken
        );
        var titleProposals = binding
            .Files.Where(file => file.Binding.Status == NormalizationCatalogBindingStatus.Bound)
            .Select(file => ExtractTitle(input, document, file, owner, cancellationToken))
            .ToArray();
        return new(
            textProposals.Credits,
            textProposals.Unresolved,
            titleProposals,
            decoded.Issues,
            textProposals.SourceActors
        );
    }

    private (
        NormalizationRecordingCreditProposal[] Credits,
        RetainedCreditUnresolved[] Unresolved,
        NormalizationRecordingSourceActorAliasScope[] SourceActors
    ) ExtractTextProposals(
        NormalizationInputRevision input,
        ProviderRecordingObservation recording,
        Guid sourceUuid,
        NormalizationRetainedDocument document,
        CancellationToken cancellationToken
    )
    {
        var proposals = new List<NormalizationRecordingCreditProposal>();
        var unresolved = new List<RetainedCreditUnresolved>();
        var actorScopes = sourceActors.Build(input, document, recording, cancellationToken);
        ExtractScope(recording.Text, null);
        foreach (var file in recording.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExtractScope(file.Text, file.Origin);
        }
        return (proposals.ToArray(), unresolved.ToArray(), actorScopes);

        void ExtractScope(
            IReadOnlyList<ProviderTextObservation> fields,
            ProviderObservationOrigin? fileOrigin
        )
        {
            var result = credits.Extract(input, fields, cancellationToken);
            proposals.AddRange(
                result.Credits.Select(
                    (mention, ordinal) =>
                        new NormalizationRecordingCreditProposal(
                            ProposalUuid(
                                "credit",
                                new
                                {
                                    input.Uuid,
                                    recording.Origin,
                                    sourceUuid,
                                    fileOrigin,
                                    ordinal,
                                    mention,
                                }
                            ),
                            mention,
                            fileOrigin
                        )
                )
            );
            unresolved.AddRange(
                result.Unresolved.Select(issue => issue with { FileOrigin = fileOrigin })
            );
        }
    }

    /// <summary>Checks retained file membership without assigning a catalog file or widening its scope.</summary>
    internal static void ValidateFileCreditEvidence(
        NormalizationInputRevision input,
        NormalizationRetainedDocument document,
        ProviderObservationOrigin fileOrigin,
        NormalizedEvidenceReference evidence
    )
    {
        document.ValidateEvidence(fileOrigin.Evidence);
        document.ValidateEvidence(evidence);
        var origin = fileOrigin.Evidence;
        var sameLocator = evidence.JsonPointer == origin.JsonPointer;
        var insideLocator =
            sameLocator
            || evidence.JsonPointer.StartsWith(origin.JsonPointer + "/", StringComparison.Ordinal);
        var insideSpan =
            origin.StartCodePoint is not { } originStart
            || (
                sameLocator
                && evidence.StartCodePoint is { } start
                && evidence.EndCodePoint is { } end
                && start >= originStart
                && end <= origin.EndCodePoint!.Value
            );
        if (fileOrigin.Evidence.InputRevisionUuid != input.Uuid || !insideLocator || !insideSpan)
        {
            throw new ArgumentException(
                "File credit evidence must remain within its retained file origin."
            );
        }
    }

    internal static ProviderRecordingObservation RequireRecording(
        NormalizationInputRevision input,
        ProviderDecodeResult decoded,
        NormalizationRecordingBindingSnapshot binding
    )
    {
        if (
            input.Uuid == Guid.Empty
            || binding.InputRevisionUuid != input.Uuid
            || binding.Resource != input.Resource
            || decoded.InputRevisionUuid != input.Uuid
            || decoded.Resource != input.Resource
            || decoded.Recordings.Count != 1
        )
        {
            throw new ArgumentException(
                "Recording extraction requires one decoded recording and its exact retained binding."
            );
        }
        var recording = decoded.Recordings[0];
        if (
            recording.Origin.Item != input.Resource
            || recording.Origin.Evidence.InputRevisionUuid != input.Uuid
            || binding.Recording.Status != NormalizationCatalogBindingStatus.Bound
            || binding.Recording.Value == null
        )
        {
            throw new ArgumentException(
                "Recording extraction requires a positively bound recording from this input."
            );
        }

        // File association belongs to the binder. Match its original assertions by cited origin,
        // never by a title or track position, before using the existing catalog UUIDs it returned.
        if (
            binding.Files.Count != recording.Files.Count
            || recording.Files.Select(file => file.Origin).Distinct().Count()
                != recording.Files.Count
            || binding.Files.Select(file => file.Observation.Origin).Distinct().Count()
                != binding.Files.Count
        )
        {
            throw new ArgumentException(
                "File bindings must retain the decoded recording's exact file assertions."
            );
        }
        var observedFiles = recording.Files.ToDictionary(file => file.Origin);
        if (
            binding.Files.Any(file =>
                !observedFiles.TryGetValue(file.Observation.Origin, out var observed)
                || observed.Title != file.Observation.Title
            )
        )
        {
            throw new ArgumentException(
                "File bindings must retain the decoded recording's exact file assertions."
            );
        }
        return recording;
    }

    private NormalizationRecordingTitleProposal ExtractTitle(
        NormalizationInputRevision input,
        NormalizationRetainedDocument document,
        NormalizationFileBinding file,
        NormalizationCatalogRecordingReference owner,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reference = file.Binding.Value;
        if (
            reference == null
            || reference.SourceUuid != owner.SourceUuid
            || reference.ArtistUuid != owner.ArtistUuid
        )
        {
            throw new ArgumentException(
                "A positively bound file must belong to the bound recording and artist."
            );
        }
        var observation = file.Observation;
        document.ValidateEvidence(observation.Origin.Evidence);
        var title = observation.Title;
        if (title != null)
        {
            document.ValidateEvidence(title.Evidence);
            var retained = document.Resolve(title.Evidence.JsonPointer);
            var exactText =
                title.Evidence.Quote
                ?? (retained.ValueKind == JsonValueKind.String ? retained.GetString() : null);
            if (title.Value != exactText)
            {
                throw new ArgumentException(
                    "A title proposal must retain the exact cited original title."
                );
            }
        }
        var proposalUuid = ProposalUuid(
            "title",
            new
            {
                input.Uuid,
                observation.Origin,
                reference.SourceUuid,
                reference.TrackUuid,
                title,
            }
        );
        return new(
            proposalUuid,
            reference.TrackUuid,
            title,
            title == null ? null : titles.Extract(owner.ArtistUuid, title.Value),
            title == null ? "missing_title" : null
        );
    }

    private static Guid ProposalUuid(string kind, object mention)
    {
        // Structured serialization keeps separators, Unicode and repeated mention ordinals
        // unambiguous. The digest is a proposal key only; no catalog identity is derived here.
        var digest = SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    Version = CreditAndTitleProposalVersion,
                    kind,
                    mention,
                }
            )
        );
        digest[6] = (byte)((digest[6] & 0x0f) | 0x80);
        digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        return new Guid(digest.AsSpan(0, 16), bigEndian: true);
    }
}
