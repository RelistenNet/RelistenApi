﻿using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Reflection;
using System.Text;
using Dapper;
using Newtonsoft.Json;
using Relisten.Api.Models.Api;
using Relisten.Import;

namespace Relisten.Api.Models
{
    public interface IHasPersistentIdentifier
    {
        [Required] Guid uuid { get; set; }
    }

    public class PersistentIdentifierHandler : SqlMapper.TypeHandler<Guid>
    {
        public override Guid Parse(object value)
        {
            return new Guid(value.ToString());
        }

        public override void SetValue(IDbDataParameter parameter, Guid value)
        {
            parameter.Value = value.ToString();
        }
    }

    public class DateTimeHandler : SqlMapper.TypeHandler<DateTime>
    {
        public override void SetValue(IDbDataParameter parameter, DateTime value)
        {
            parameter.Value = value;
        }

        public override DateTime Parse(object value)
        {
            return ((DateTime)value).ToUniversalTime();
        }
    }

    public abstract class BaseRelistenModel
    {
        private PropertyInfo[] _PropertyInfos;

        [V2JsonOnly] [Required] public int id { get; set; }

        [Required] public DateTime created_at { get; set; }

        [Required] public DateTime updated_at { get; set; }

        public override string ToString()
        {
            if (_PropertyInfos == null)
            {
                _PropertyInfos = GetType().GetProperties();
            }

            var sb = new StringBuilder();

            foreach (var info in _PropertyInfos)
            {
                var value = info.GetValue(this, null) ?? "(null)";
                sb.AppendLine(info.Name + ": " + value);
            }

            return sb.ToString();
        }
    }

    public class SlimArtist : BaseRelistenModel, IHasPersistentIdentifier
    {
        [Required] public string musicbrainz_id { get; set; }

        [Required] public string name { get; set; }

        [Required] public int featured { get; set; }

        [Required] public string slug { get; set; }

        [Required] public string sort_name { get; set; }

        [Required] public Guid uuid { get; set; }
    }

    public class SlimArtistWithFeatures : SlimArtist
    {
        [Required] public Features features { get; set; }
    }

    public class Artist : SlimArtistWithFeatures
    {
        [Required] public IEnumerable<ArtistUpstreamSource> upstream_sources { get; set; }
    }

    public class ArtistWithCounts : Artist
    {
        [Required] public int show_count { get; set; }

        [Required] public int source_count { get; set; }
    }

    public class FullArtist
    {
        [Required] public ArtistWithCounts artist { get; set; }
        [Required] public List<VenueWithShowCount> venues { get; set; }
        [Required] public List<SetlistSongWithPlayCount> songs { get; set; }
        [Required] public List<TourWithShowCount> tours { get; set; }
        [Required] public List<Year> years { get; set; }
        [Required] public List<Show> shows { get; set; }
    }

    public class Features
    {
        [V2JsonOnly] [Required] public int id { get; set; }


        [Required] public bool descriptions { get; set; }

        [Required] public bool eras { get; set; }

        [Required] public bool multiple_sources { get; set; }

        [Required] public bool reviews { get; set; }

        [Required] public bool ratings { get; set; }

        [Required] public bool tours { get; set; }

        [Required] public bool taper_notes { get; set; }

        [Required] public bool source_information { get; set; }

        [Required] public bool sets { get; set; }

        [Required] public bool per_show_venues { get; set; }

        [Required] public bool per_source_venues { get; set; }

        [Required] public bool venue_coords { get; set; }

        [Required] public bool songs { get; set; }

        [Required] public bool years { get; set; }

        [Required] public bool track_md5s { get; set; }

        [Required] public bool review_titles { get; set; }

        [Required] public bool jam_charts { get; set; }

        [Required] public bool setlist_data_incomplete { get; set; }

        [V2JsonOnly] [Required] public int artist_id { get; set; }

        [Required] public bool track_names { get; set; }

        [Required] public bool venue_past_names { get; set; }

        [Required] public bool reviews_have_ratings { get; set; }

        [Required] public bool track_durations { get; set; }

        [Required] public bool can_have_flac { get; set; }
    }

    public class UpstreamSource
    {
        [V2JsonOnly] [Required] public int id { get; set; }

        [Required] public string name { get; set; }

        [Required] public string url { get; set; }

        [Required] public string description { get; set; }

        [Required] public string credit_line { get; set; }

        [JsonIgnore] public ImporterBase importer { get; set; }
    }

    public class SlimArtistUpstreamSource
    {
        [Required] public int upstream_source_id { get; set; }

        public string upstream_identifier { get; set; }
    }

    public class ArtistUpstreamSource : SlimArtistUpstreamSource
    {
        [V2JsonOnly] [Required] public int artist_id { get; set; }
        [Required] public Guid artist_uuid { get; set; }

        public UpstreamSource upstream_source { get; set; }
    }
}
