using System;
using System.Threading;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>
/// Decodes retained representations into cited provider assertions. No network,
/// catalog writes, canonical identity allocation or acceptance occurs here.
/// </summary>
public sealed partial class NormalizationProviderDecoder
{
    public const string Version = "provider-observations-v1";

    /// <summary>
    /// Requires a known provider/resource/capture-version contract. Unsupported versions
    /// fail explicitly so a new representation cannot silently reuse an old interpretation.
    /// </summary>
    public ProviderDecodeResult Decode(
        NormalizationInputRevision input,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        Func<ProviderObservationReader, ProviderDecodeResult> decode = (
            input.Resource.Provider,
            input.Resource.ResourceType,
            input.CaptureVersion
        ) switch
        {
            ("archive.org", "item_metadata", "archive-item-response-v1") => DecodeArchive,
            ("phish.in", "catalog", "raw-http:1") => DecodePhishCatalog,
            ("phish.in", "show", "phishin-show-object-v1") => DecodePhishShow,
            ("jerrygarcia.com", "mirror_show_page", "jerry-mirror-html:1") => DecodeJerry,
            ("local", "metadata_root", "local-http-json-v1") => DecodeLocalRoot,
            ("local", "directory_metadata", "local-http-json-v1") => DecodeLocalDirectory,
            ("setlist.fm", "setlist-page", "setlist-fm-http-v1") => DecodeSetlistFm,
            ("phantasytour.com", "show-list", "phantasy-tour-http-v1") => DecodePhantasyTourList,
            ("phantasytour.com", "show", "phantasy-tour-http-v1") => DecodePhantasyTourShow,
            ("phish.net", "shows", "phish-net-http-v1") => DecodePhishNetShows,
            ("phish.net", "reviews", "phish-net-http-v1") => DecodePhishNetReviews,
            ("phish.net", "ratings", "phish-net-http-v1") => DecodePhishNetHtml,
            ("legacy_catalog", "recording", "legacy-recording-snapshot-v1") =>
                DecodeLegacyRecording,
            _ => throw new NotSupportedException(
                "The retained provider representation is not supported by this decoder."
            ),
        };
        using var reader = new ProviderObservationReader(input, cancellationToken);
        var result = decode(reader);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
