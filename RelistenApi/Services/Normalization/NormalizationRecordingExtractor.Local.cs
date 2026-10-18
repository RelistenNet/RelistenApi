using System.Threading;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

public sealed partial class NormalizationRecordingExtractor
{
    /// <summary>
    /// Combines directory prose and files with its selected root account's recording-wide
    /// taper notes. Root citations keep their actual revision and locator. Titles and file
    /// scopes come only from the directory; unrelated root accounts never enter the result.
    /// </summary>
    public NormalizationRecordingExtraction ExtractLocal(
        NormalizationInputRevision directoryInput,
        ProviderDecodeResult directoryDecoded,
        NormalizationRecordingBindingSnapshot binding,
        NormalizationInputRevision rootInput,
        ProviderDecodeResult rootDecoded,
        CancellationToken cancellationToken = default
    )
    {
        var selected = NormalizationLocalRecordingSelection.Select(
            directoryInput,
            directoryDecoded,
            binding,
            rootInput,
            rootDecoded,
            cancellationToken
        );
        var directory = Extract(directoryInput, directoryDecoded, binding, cancellationToken);
        using var rootDocument = NormalizationRetainedDocument.Open(rootInput, cancellationToken);
        var rootText = ExtractTextProposals(
            rootInput,
            selected.RootAccount,
            binding.Recording.Value!.SourceUuid,
            rootDocument,
            cancellationToken
        );
        return new(
            [.. directory.Credits, .. rootText.Credits],
            [.. directory.UnresolvedCredits, .. rootText.Unresolved],
            directory.Titles,
            [.. directory.DecodeIssues, .. selected.RootIssues],
            [.. directory.SourceActorAliases!, .. rootText.SourceActors]
        );
    }
}
