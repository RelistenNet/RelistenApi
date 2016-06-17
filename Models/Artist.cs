
using System;

namespace Relisten.Api.Models
{
    public class Artist {
        public int id { get; set; }

        public DateTime createdAt { get; set; }
        public DateTime updatedAt { get; set; }

        public string name { get; set; }
        public string archive_collection { get; set; }
        public string slug { get; set; }
        public bool from_archive { get; set; }
        public string musicbrainz_id { get; set; }
        public int extended_features { get; set; }
    }
}
