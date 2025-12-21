using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Relisten.Api.Models
{
    public class SearchResults
    {
        [Required] public IEnumerable<SlimArtist> Artists { get; set; } = null!;

        [Required] public IEnumerable<ShowWithSlimArtist> Shows { get; set; } = null!;

        [Required] public IEnumerable<SetlistSongWithSlimArtist> Songs { get; set; } = null!;

        [Required] public IEnumerable<SourceWithSlimArtist> Source { get; set; } = null!;

        [Required] public IEnumerable<TourWithSlimArtist> Tours { get; set; } = null!;

        [Required] public IEnumerable<VenueWithSlimArtist> Venues { get; set; } = null!;
    }

    public class ShowWithSlimArtist : Show
    {
        [Required] public SlimArtist slim_artist { get; set; } = null!;
    }

    public class SourceWithSlimArtist : Source
    {
        [Required] public SlimArtist slim_artist { get; set; } = null!;
    }

    public class TourWithSlimArtist : Tour
    {
        [Required] public SlimArtist slim_artist { get; set; } = null!;
    }

    public class VenueWithSlimArtist : Venue
    {
        [Required] public SlimArtist slim_artist { get; set; } = null!;
    }

    public class SetlistSongWithSlimArtist : SetlistSongWithPlayCount
    {
        [Required] public SlimArtist slim_artist { get; set; } = null!;
    }
}
