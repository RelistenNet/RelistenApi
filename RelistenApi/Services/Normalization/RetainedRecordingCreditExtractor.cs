using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>
/// Runs local credit grammar on decoded prose while retaining coordinates in the original
/// provider input. Repeated array fields and directory accounts are never flattened together.
/// </summary>
public sealed partial class RetainedRecordingCreditExtractor
{
    public const string Version =
        "retained-recording-credits-v2/"
        + RecordingCreditExtractor.Version
        + "/"
        + ActorMappingVersion;
    private readonly RecordingCreditExtractor extractor = new();

    public RetainedCreditExtraction Extract(
        NormalizationInputRevision input,
        IReadOnlyList<ProviderTextObservation> fields,
        CancellationToken cancellationToken = default
    )
    {
        using var document = NormalizationRetainedDocument.Open(input, cancellationToken);
        var credits = new List<RetainedCreditMention>();
        var unresolved = new List<RetainedCreditUnresolved>();
        foreach (var field in fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                field.Field
                is not (
                    "taper"
                    or "tapers"
                    or "transferrer"
                    or "transferrers"
                    or "description"
                    or "source"
                    or "lineage"
                    or "taper_notes"
                    or "tag_notes"
                    or "notes"
                )
            )
            {
                continue;
            }

            var reference = field.Text.Evidence;
            document.ValidateEvidence(reference);
            var value = document.Resolve(reference.JsonPointer);
            var raw =
                reference.Quote
                ?? (value.ValueKind == JsonValueKind.String ? value.GetString() : null);
            if (raw != field.Text.Value)
            {
                throw new ArgumentException(
                    "Credit text must retain the exact cited original value."
                );
            }

            var result = extractor.Extract(new Dictionary<string, string?> { [field.Field] = raw });
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var mention in result.Credits)
            {
                var evidence = Remap(reference, mention.Evidence);
                document.ValidateEvidence(evidence);
                credits.Add(
                    new(
                        mention.Name,
                        mention.Role,
                        evidence,
                        mention.OriginalRole,
                        mention.ActorFacts == null
                            ? null
                            : MapActorFacts(reference, mention.ActorFacts, document)
                    )
                );
            }
            foreach (var issue in result.Unresolved)
            {
                var evidence = Remap(reference, issue.Evidence);
                document.ValidateEvidence(evidence);
                unresolved.Add(new(issue.Reason, evidence));
            }
        }
        return new(credits, unresolved);
    }

    private static NormalizedEvidenceReference Remap(
        NormalizedEvidenceReference field,
        NormalizationTextEvidence mention
    ) =>
        new(
            field.InputRevisionUuid,
            field.JsonPointer,
            (field.StartCodePoint ?? 0) + mention.StartCodePoint,
            (field.StartCodePoint ?? 0) + mention.EndCodePoint,
            mention.Quote
        );
}
