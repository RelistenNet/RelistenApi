
using System;
using System.Collections.Generic;

namespace Relisten.Api.Models
{
    public class Year {
        public int id { get; set; }

        public DateTime createdAt { get; set; }
        public DateTime updatedAt { get; set; }

        public int year { get; set; }
        public int show_count { get; set; }
        public int recording_count { get; set; }
        public int duration { get; set; }
        public int avg_duration { get; set; }
        public double avg_rating { get; set; }
        public int artistId { get; set; }

        public Artist artist { get; set; }

        public IEnumerable<SimpleShow> shows { get; set; }
    }
}
