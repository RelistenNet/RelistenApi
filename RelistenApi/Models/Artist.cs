
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace Relisten.Api.Models
{
    public abstract class BaseRelistenModel
    {
        [Required]
        public int id { get; set; }

        [Required]
        public DateTime created_at { get; set; }

        [Required]
        public DateTime updated_at { get; set; }

        private PropertyInfo[] _PropertyInfos = null;

        public override string ToString()
        {
            if (_PropertyInfos == null)
                _PropertyInfos = this.GetType().GetProperties();

            var sb = new StringBuilder();

            foreach (var info in _PropertyInfos)
            {
                var value = info.GetValue(this, null) ?? "(null)";
                sb.AppendLine(info.Name + ": " + value.ToString());
            }

            return sb.ToString();
        }
    }

    public class Artist : BaseRelistenModel
    {
        [Required]
        public string upstream_identifier { get; set; }

        [Required]
        public string data_source { get; set; }

        [Required]
        public string musicbrainz_id { get; set; }

        [Required]
        public string name { get; set; }

        [Required]
        public int featured { get; set; }

        [Required]
        public string slug { get; set; }


        [Required]
        public Features features { get; set; }
    }

    public class ArtistWithCounts : Artist
    {
        [Required]
        public int show_count { get; set; }

        [Required]
        public int source_count { get; set; }
    }

    public class Features
    {

        [Required]
        public int id { get; set; }


        [Required]
        public bool descriptions { get; set; }

        [Required]
        public bool eras { get; set; }

        [Required]
        public bool multiple_sources { get; set; }

        [Required]
        public bool reviews { get; set; }

        [Required]
        public bool ratings { get; set; }

        [Required]
        public bool tours { get; set; }

        [Required]
        public bool taper_notes { get; set; }

        [Required]
        public bool source_information { get; set; }

        [Required]
        public bool sets { get; set; }

        [Required]
        public bool per_show_venues { get; set; }

        [Required]
        public bool per_source_venues { get; set; }

        [Required]
        public bool venue_coords { get; set; }

        [Required]
        public bool songs { get; set; }

        [Required]
        public bool years { get; set; }

        [Required]
        public bool track_md5s { get; set; }

        [Required]
        public bool review_titles { get; set; }

        [Required]
        public bool jam_charts { get; set; }

        [Required]
        public bool setlist_data_incomplete { get; set; }

        [Required]
        public bool artist_id { get; set; }

        [Required]
        public bool track_names { get; set; }

        [Required]
        public bool venue_past_names { get; set; }

        [Required]
        public bool reviews_have_ratings { get; set; }

        [Required]
        public bool track_durations { get; set; }
    }
}