using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRecordingProcessor
{
    private ProviderDecodeResult DecodeRoot(
        NormalizationInputRevision root,
        Dictionary<Guid, ProviderDecodeResult> decodedRoots,
        CancellationToken ct
    )
    {
        if (!decodedRoots.TryGetValue(root.Uuid, out var decoded))
        {
            decoded = NormalizationLocalRecordingSelection.DecodeRoot(
                root,
                decoder,
                cancellationToken: ct
            );
            decodedRoots.Add(root.Uuid, decoded);
        }
        return decoded;
    }

    private NormalizationRecordingExtraction ExtractLocal(
        NormalizationInputRevision directory,
        ProviderDecodeResult decoded,
        NormalizationRecordingBindingSnapshot binding,
        IReadOnlyDictionary<Guid, NormalizationInputRevision> retained,
        Dictionary<Guid, ProviderDecodeResult> decodedRoots,
        CancellationToken ct
    )
    {
        var root = retained[binding.SupportingInputs!.Single().InputRevisionUuid];
        return extractor.ExtractLocal(
            directory,
            decoded,
            binding,
            root,
            DecodeRoot(root, decodedRoots, ct),
            ct
        );
    }
}
