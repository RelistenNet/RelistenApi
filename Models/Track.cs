
using System;

namespace Relisten.Api.Models
{
    public class Track {
        public int id { get; set; }

        public DateTime createdAt { get; set; }
        public DateTime updatedAt { get; set; }

        public string title { get; set; }
        public string md5 { get; set; }
        public int trackNumber { get; set; }
        public int bitrate { get; set; }
        public int fileSize { get; set; }
        public int duration { get; set; }
        public string fileUrl { get; set; }
        public string slug { get; set; }

        public int showId { get; set; }
        public int artistId { get; set; }
    }
}
