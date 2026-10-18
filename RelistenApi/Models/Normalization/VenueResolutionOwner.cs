using System;
using System.Security.Cryptography;
using System.Text;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// Bound assignments retain their catalog owner. An unbound observation is enrolled under its
/// existing extraction account's stable owner, without asserting a performance association.
/// Multiple exact claims remain distinct attempt receipts; their shared source clocks describe
/// the same complete recording/account input set rather than invented accepted identities.
/// </summary>
public static class VenueResolutionOwner
{
    public static NormalizationSubjectKey For(
        VenueResolutionRequest request,
        VenueResolutionSource source
    )
    {
        if (request.Kind == VenueResolutionRequestKind.ImportedVenue)
        {
            return new("venues", "imported_venue", request.ImportedVenueUuid!.Value);
        }
        if (source.Claim?.CatalogUuid is { } showUuid)
        {
            return new("venues", "show", showUuid);
        }
        return ForObservation(source);
    }

    public static NormalizationSubjectKey ForObservation(VenueResolutionSource source)
    {
        var account =
            source.OriginalTicket?.SubjectUuid
            ?? throw new ArgumentException(
                "An unbound venue observation needs its original processing owner."
            );
        var uuid = new Guid(
            SHA256
                .HashData(
                    Encoding.UTF8.GetBytes("venue-observation-owner-v1\n" + account.ToString("D"))
                )
                .AsSpan(0, 16)
        );
        return new("venues", "observation", uuid);
    }
}
