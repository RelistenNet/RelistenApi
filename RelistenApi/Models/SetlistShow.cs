using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace Relisten.Api.Models
{
    public class SetlistShow : BaseRelistenModel
    {

        [Required]
        public int artist_id { get; set; }

        [Required]
        public int? tour_id { get; set; }

        [Required]
        public Tour tour { get; set; }


        [Required]
        public int? era_id { get; set; }

        [Required]
        public Era era { get; set; }


        [Required]
        public int venue_id { get; set; }

        [Required]
        public Venue venue { get; set; }

        /// <summary>ONLY DATE</summary>

        [Required]
        public DateTime date { get; set; }

        [Required]
        public string upstream_identifier { get; set; }
    }

    public class SimpleSetlistShow : BaseRelistenModel
    {
        /// <summary>ONLY DATE</summary>
        [Required]
        public DateTime date { get; set; }
    }
}