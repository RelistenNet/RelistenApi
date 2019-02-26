
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Relisten.Vendor.Phishin
{
    public class PhishinRootObject<T>
    {
        public bool success { get; set; }
        public int total_entries { get; set; }
        public int total_pages { get; set; }
        public int page { get; set; }
        public T data { get; set; }
    }

    public class PhishinEraResponse : PhishinRootObject<IDictionary<string, IList<string>>>
    {

    }

    public class PhishinSmallTour
    {
        public int id { get; set; }
        public string name { get; set; }
        public int shows_count { get; set; }
        public string starts_on { get; set; }
        public string ends_on { get; set; }
        public string slug { get; set; }
        public DateTime updated_at { get; set; }
    }

    public class PhishinTour : PhishinSmallTour
    {
        public List<PhishinSmallShow> shows { get; set; }
    }

    public class PhishinSmallShow
    {
        public int id { get; set; }
        public string date { get; set; }
        public int duration { get; set; }
        public bool incomplete { get; set; }
        public bool missing { get; set; }
        public bool sbd { get; set; }
        public bool remastered { get; set; }
        public int? tour_id { get; set; }
        public int venue_id { get; set; }
        public int likes_count { get; set; }
        public string taper_notes { get; set; }
        public DateTime updated_at { get; set; }
        public string venue_name { get; set; }
        public string location { get; set; }
    }

    public class PhishinSmallVenue
    {
        public int id { get; set; }
        public string name { get; set; }
        public string past_names { get; set; }
        public double? latitude { get; set; }
        public double? longitude { get; set; }
        public int shows_count { get; set; }
        public string location { get; set; }
        public string slug { get; set; }
        public DateTime updated_at { get; set; }
    }

    public class PhishinVenue
    {
        public int id { get; set; }
        public string name { get; set; }
        public string past_names { get; set; }
        public double latitude { get; set; }
        public double longitude { get; set; }
        public int shows_count { get; set; }
        public string location { get; set; }
        public string slug { get; set; }
        public IList<string> show_dates { get; set; }
        public IList<int> show_ids { get; set; }
        public DateTime updated_at { get; set; }
    }

    public class PhishinSmallSong
    {
        public int id { get; set; }
        public string title { get; set; }
        public int? alias_for { get; set; }
        public int tracks_count { get; set; }
        public string slug { get; set; }
        public DateTime updated_at { get; set; }
    }

    public class PhishinSong
    {
        public int id { get; set; }
        public string title { get; set; }
        public int? alias_for { get; set; }
        public int tracks_count { get; set; }
        public string slug { get; set; }
        public DateTime updated_at { get; set; }
        public List<PhishinTrack> tracks { get; set; }
    }

    public class PhishinTrack
    {
        public int id { get; set; }
        public string title { get; set; }
        public int duration { get; set; }
        public int show_id { get; set; }
        public string show_date { get; set; }
        public string set { get; set; }
        public string set_name { get; set; }
        public int position { get; set; }
        public int likes_count { get; set; }
        public string slug { get; set; }
        public string mp3 { get; set; }
    }

    public class PhishinShowTrack : PhishinTrack
    {
        public IList<int> song_ids { get; set; }
    }

    public class PhishinShow
    {
        public int id { get; set; }
        public string date { get; set; }
        public int duration { get; set; }
        public bool incomplete { get; set; }
        public bool missing { get; set; }
        public bool sbd { get; set; }
        public bool remastered { get; set; }
        public List<PhishinTag> tags { get; set; }
        public int tour_id { get; set; }
        public PhishinSmallVenue venue { get; set; }
        public string taper_notes { get; set; }
        public int likes_count { get; set; }
        public List<PhishinShowTrack> tracks { get; set; }
        public DateTime updated_at { get; set; }
    }

    public class PhishinTag
    {
        public long id { get; set; }
        public string name { get; set; }
        public long priority { get; set; }
        public string group { get; set; }
        public string color { get; set; }
        public object notes { get; set; }
    }
}