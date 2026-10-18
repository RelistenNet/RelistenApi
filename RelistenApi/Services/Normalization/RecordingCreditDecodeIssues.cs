using System;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>
/// Separates credit coverage failures from retained playback and venue diagnostics. A source
/// can have usable taper evidence without a show or playable file. Actual file association
/// remains the checked binder's responsibility; an unbound credit cannot widen its scope.
/// Unknown issue kinds remain blocking until their credit effect is understood.
/// </summary>
internal static class RecordingCreditDecodeIssues
{
    internal const string Version = "recording-credit-decode-issues-v1";

    internal static bool AffectsCredits(ProviderDecodeIssue issue) =>
        issue.Code switch
        {
            "missing_recording_show_context"
            or "recording_show_context_mismatch"
            or "source_show_date_wording_differs"
            or "missing_recording_artist_context"
            or "recording_artist_context_mismatch"
            or "expected_venue_object"
            or "multiple_recording_dates"
            or "multiple_venue_localities"
            or "conflicting_directory_date"
            or "conflicting_file_title"
            or "expected_track_numbering_object"
            or "missing_file_locator"
            or "orphaned_catalog_track"
            or "unstructured_track_number"
            or "track_show_identity_mismatch"
            or "missing_track_source_context"
            or "missing_track_set_context"
            or "missing_track_artist_context"
            or "track_source_context_mismatch"
            or "track_set_context_mismatch"
            or "track_artist_context_mismatch"
            or "track_source_recording_mismatch"
            or "track_set_recording_mismatch"
            or "track_artist_recording_mismatch" => false,
            "invalid_catalog_identity" when IsVenuePointer(issue.Evidence.JsonPointer) => false,
            _ => true,
        };

    private static bool IsVenuePointer(string pointer) =>
        pointer.StartsWith("/source_venue/", StringComparison.Ordinal)
        || pointer.StartsWith("/show_venue/", StringComparison.Ordinal);
}
