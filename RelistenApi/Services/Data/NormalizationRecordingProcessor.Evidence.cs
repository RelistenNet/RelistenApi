using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRecordingProcessor
{
    // One invocation owns these immutable bytes and root decodes. Recovery loads the saved
    // complete set again; a concurrent preparation winner replaces the candidate set as a whole.
    private sealed class RecordingInputSet
    {
        internal HashSet<NormalizationBindingInputReference> References { get; set; } = [];
        internal IReadOnlyDictionary<Guid, NormalizationInputRevision> Inputs { get; set; } =
            new Dictionary<Guid, NormalizationInputRevision>();
        internal Dictionary<Guid, ProviderDecodeResult> Roots { get; } = [];
    }

    private async Task<IReadOnlyDictionary<Guid, NormalizationInputRevision>> ReadInputsAsync(
        IReadOnlyList<NormalizationBindingInputReference> references,
        RecordingInputSet cache,
        CancellationToken ct
    )
    {
        if (!cache.References.SetEquals(references))
        {
            var retained = await evidence.GetInputsAsync(references, cancellationToken: ct);
            cache.Inputs = retained.ToDictionary(input => input.Uuid);
            cache.References = references.ToHashSet();
            cache.Roots.Clear();
        }
        return cache.Inputs;
    }
}
