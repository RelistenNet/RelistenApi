using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>
/// Builds the exact role-free alias account for one original recording. Production extraction
/// and checkpoint validation share this path; neither replays recording credits or titles.
/// The caller owns the open retained document, including a selected Local root's document.
/// </summary>
internal sealed class NormalizationRecordingSourceActorAliasBuilder
{
    private readonly RetainedSourceActorAliasExtractor extractor;

    internal NormalizationRecordingSourceActorAliasBuilder(
        SourceActorAliasGrammar grammar = SourceActorAliasGrammar.Current
    ) => extractor = new(grammar);

    internal NormalizationRecordingSourceActorAliasScope[] Build(
        NormalizationInputRevision input,
        NormalizationRetainedDocument document,
        ProviderRecordingObservation recording,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        document.ValidateEvidence(recording.Origin.Evidence);
        var scopes = new List<NormalizationRecordingSourceActorAliasScope>();
        Add(recording.Text, null);
        foreach (var file in recording.Files)
        {
            ct.ThrowIfCancellationRequested();
            // The binder cannot replace original decoded text or its file membership.
            // These guards also apply when the file has no text and produces no alias scope.
            document.ValidateEvidence(file.Origin.Evidence);
            if (
                file.Origin.Item != input.Resource
                && !file.Identities.Any(identity => identity.Identity == file.Origin.Item)
            )
            {
                throw new ArgumentException(
                    "A file credit origin must belong to this retained recording."
                );
            }
            foreach (var field in file.Text)
            {
                NormalizationRecordingExtractor.ValidateFileCreditEvidence(
                    input,
                    document,
                    file.Origin,
                    field.Text.Evidence
                );
            }
            Add(file.Text, file.Origin);
        }
        return scopes.ToArray();

        void Add(
            IReadOnlyList<ProviderTextObservation> fields,
            ProviderObservationOrigin? fileOrigin
        )
        {
            // Missing text is not an original empty string or an authoritative clear.
            if (fields.Count == 0)
            {
                return;
            }
            scopes.Add(
                new(
                    extractor.ProducerVersion,
                    input.Uuid,
                    input.Resource,
                    recording.Origin,
                    extractor.Extract(document, fields, ct),
                    fileOrigin
                )
            );
        }
    }
}
