
using System;

namespace Relisten.Api.Models
{
    public class Venue {
        public int id { get; set; }

        public DateTime createdAt { get; set; }
        public DateTime updatedAt { get; set; }

        public string name { get; set; }
        public string city { get; set; }
        public string slug { get; set; }
    }
}
