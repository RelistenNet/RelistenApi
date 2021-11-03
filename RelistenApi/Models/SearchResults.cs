using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Relisten.Api.Models
{
    public class SearchResults
    {
        [Required] public IEnumerable<SlimArtist> Artists { get; set; }

        [Required] public IEnumerable<ShowWithSlimArtist> Shows { get; set; }

        [Required] public IEnumerable<SetlistSongWithSlimArtist> Songs { get; set; }

        [Required] public IEnumerable<SourceWithSlimArtist> Source { get; set; }

        [Required] public IEnumerable<TourWithSlimArtist> Tours { get; set; }

        [Required] public IEnumerable<VenueWithSlimArtist> Venues { get; set; }
    }

    public class ShowWithSlimArtist : Show
    {
        [Required] public SlimArtist slim_artist { get; set; }
    }

    public class SourceWithSlimArtist : Source
    {
        [Required] public SlimArtist slim_artist { get; set; }
    }

    public class TourWithSlimArtist : Tour
    {
        [Required] public SlimArtist slim_artist { get; set; }
    }

    public class VenueWithSlimArtist : Venue
    {
        [Required] public SlimArtist slim_artist { get; set; }
    }

    public class SetlistSongWithSlimArtist : SetlistSongWithPlayCount
    {
        [Required] public SlimArtist slim_artist { get; set; }
    }
}
