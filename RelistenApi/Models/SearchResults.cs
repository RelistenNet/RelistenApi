using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Relisten.Api.Models
{
    public class SearchResults
    {
        [Required] public IEnumerable<SlimArtist> Artists { get; set; } = null!;

        [Required] public IEnumerable<ShowWithSlimArtist> Shows { get; set; } = null!;

        [Required] public IEnumerable<SetlistSongWithSlimArtist> Songs { get; set; } = null!;

        [Required] public IEnumerable<SourceWithSlimArtist> Sources { get; set; } = null!;

        [Required] public IEnumerable<TourWithSlimArtist> Tours { get; set; } = null!;

        [Required] public IEnumerable<VenueWithSlimArtist> Venues { get; set; } = null!;

        public static SearchResults Empty()
        {
            return new SearchResults
            {
                Artists = new List<SlimArtist>(),
                Shows = new List<ShowWithSlimArtist>(),
                Songs = new List<SetlistSongWithSlimArtist>(),
                Sources = new List<SourceWithSlimArtist>(),
                Tours = new List<TourWithSlimArtist>(),
                Venues = new List<VenueWithSlimArtist>()
            };
        }
    }

    public class SearchOptions
    {
        public bool Artists { get; set; } = true;
        public bool Shows { get; set; } = true;
        public bool Songs { get; set; } = true;
        public bool Sources { get; set; } = true;
        public bool Tours { get; set; } = true;
        public bool Venues { get; set; } = true;

        public bool AnyEnabled => Artists || Shows || Songs || Sources || Tours || Venues;
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
