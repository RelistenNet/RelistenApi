using System;

namespace Relisten.Api.Models.Api.V4;

/// <summary>
/// Fixed positions in the unsigned 64-bit artist features mask. Never renumber or reuse a bit.
/// Serialized as a decimal string so clients can decode all 64 bits without precision loss.
/// </summary>
[Flags]
public enum ArtistFeatures : ulong
{
    None = 0,
    Descriptions = 1UL << 0,
    Eras = 1UL << 1,
    MultipleSources = 1UL << 2,
    Reviews = 1UL << 3,
    Ratings = 1UL << 4,
    Tours = 1UL << 5,
    TaperNotes = 1UL << 6,
    SourceInformation = 1UL << 7,
    Sets = 1UL << 8,
    PerShowVenues = 1UL << 9,
    PerSourceVenues = 1UL << 10,
    VenueCoords = 1UL << 11,
    Songs = 1UL << 12,
    Years = 1UL << 13,
    TrackMd5s = 1UL << 14,
    ReviewTitles = 1UL << 15,
    JamCharts = 1UL << 16,
    SetlistDataIncomplete = 1UL << 17,
    TrackNames = 1UL << 18,
    VenuePastNames = 1UL << 19,
    ReviewsHaveRatings = 1UL << 20,
    TrackDurations = 1UL << 21,
    CanHaveFlac = 1UL << 22,
    Normalization = 1UL << 23,
}
