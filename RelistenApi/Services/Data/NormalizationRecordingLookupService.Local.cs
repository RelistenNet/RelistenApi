using System;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRecordingLookupService
{
    /// <summary>
    /// Looks up one Local directory with its actual retained root revision. Both revisions
    /// belong to the immutable attempt. The root proves directory membership and mapping
    /// context; neither dates nor a constructed root account select the catalog recording.
    /// </summary>
    public Task<NormalizationRecordingLookupAttempt> LookupLocalAsync(
        Guid attemptUuid,
        NormalizationBindingInputReference rootReference,
        NormalizationBindingInputReference directoryReference,
        Guid? expectedArtistUuid = null,
        CancellationToken cancellationToken = default
    )
    {
        var rootResource = NormalizationProviderResources.LocalRootForDirectory(
            directoryReference.Resource
        );
        if (rootReference.Resource != rootResource)
        {
            throw new ArgumentException(
                "The retained root does not belong to the Local directory.",
                nameof(rootReference)
            );
        }
        return StartAsync(
            attemptUuid,
            new(
                NormalizationRecordingLookupMode.RetainedProvider,
                directoryReference.Resource,
                expectedArtistUuid,
                directoryReference,
                [rootReference]
            ),
            cancellationToken
        );
    }
}
