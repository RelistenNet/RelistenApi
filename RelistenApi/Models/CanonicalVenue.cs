using System;
using System.ComponentModel.DataAnnotations;

namespace Relisten.Api.Models
{
    /// <summary>
    /// A global canonical venue representing a physical location, independent of any artist.
    /// Artist-scoped venues link to canonical venues via venues.canonical_venue_id.
    /// This allows cross-artist venue deduplication (e.g. all "Fox Theatre, Atlanta, GA"
    /// entries across different artists point to the same canonical venue).
    /// </summary>
    public class CanonicalVenue
    {
        [Required] public int id { get; set; }
        [Required] public string name { get; set; } = null!;
        [Required] public string location { get; set; } = null!;
        public double? latitude { get; set; }
        public double? longitude { get; set; }
        [Required] public string slug { get; set; } = null!;
        public string? past_names { get; set; }
        [Required] public DateTime created_at { get; set; }
        [Required] public DateTime updated_at { get; set; }
        [Required] public Guid uuid { get; set; }
    }
}
