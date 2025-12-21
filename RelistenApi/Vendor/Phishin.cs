using System;
using System.Collections.Generic;

namespace Relisten.Vendor.Phishin
{
    public class PhishinRootObject<T>
    {
        public bool success { get; set; }
        public int total_entries { get; set; }
        public int total_pages { get; set; }
        public int page { get; set; }
        public T data { get; set; } = default!;
    }

    public class PhishinEraResponse : PhishinRootObject<IDictionary<string, IList<string>>>
    {
    }

    public class PhishinSmallTour
    {
        public int id { get; set; }
        public string name { get; set; } = null!;
        public int shows_count { get; set; }
        public string starts_on { get; set; } = null!;
        public string ends_on { get; set; } = null!;
        public string slug { get; set; } = null!;
        public DateTime updated_at { get; set; }
    }

    public class PhishinTour : PhishinSmallTour
    {
        public List<PhishinSmallShow> shows { get; set; } = null!;
    }

    public class PhishinSmallShow
    {
        public int id { get; set; }
        public string date { get; set; } = null!;
        public int duration { get; set; }
        public bool incomplete { get; set; }
        public bool missing { get; set; }
        public bool sbd { get; set; }
        public bool remastered { get; set; }
        public int? tour_id { get; set; }
        public int venue_id { get; set; }
        public int likes_count { get; set; }
        public string taper_notes { get; set; } = null!;
        public DateTime updated_at { get; set; }
        public string venue_name { get; set; } = null!;
        public string location { get; set; } = null!;
    }

    public class PhishinSmallVenue
    {
        public int id { get; set; }
        public string name { get; set; } = null!;
        public string past_names { get; set; } = null!;
        public double? latitude { get; set; }
        public double? longitude { get; set; }
        public int shows_count { get; set; }
        public string location { get; set; } = null!;
        public string slug { get; set; } = null!;
        public DateTime updated_at { get; set; }
    }

    public class PhishinVenue
    {
        public int id { get; set; }
        public string name { get; set; } = null!;
        public string past_names { get; set; } = null!;
        public double latitude { get; set; }
        public double longitude { get; set; }
        public int shows_count { get; set; }
        public string location { get; set; } = null!;
        public string slug { get; set; } = null!;
        public IList<string> show_dates { get; set; } = null!;
        public IList<int> show_ids { get; set; } = null!;
        public DateTime updated_at { get; set; }
    }

    public class PhishinSmallSong
    {
        public int id { get; set; }
        public string title { get; set; } = null!;
        public int? alias_for { get; set; }
        public int tracks_count { get; set; }
        public string slug { get; set; } = null!;
        public DateTime updated_at { get; set; }
    }

    public class PhishinSong
    {
        public int id { get; set; }
        public string title { get; set; } = null!;
        public int? alias_for { get; set; }
        public int tracks_count { get; set; }
        public string slug { get; set; } = null!;
        public DateTime updated_at { get; set; }
        public List<PhishinTrack> tracks { get; set; } = null!;
    }

    public class PhishinTrack
    {
        public int id { get; set; }
        public string title { get; set; } = null!;
        public int duration { get; set; }
        public int show_id { get; set; }
        public string show_date { get; set; } = null!;
        public string set { get; set; } = null!;
        public string set_name { get; set; } = null!;
        public int position { get; set; }
        public int likes_count { get; set; }
        public string slug { get; set; } = null!;
        public DateTime? updated_at { get; set; }
        public DateTime? created_at { get; set; }
        public string mp3 { get; set; } = null!;
    }

    public class PhishinShowTrack : PhishinTrack
    {
        public IList<int> song_ids { get; set; } = null!;
    }

    public class PhishinShow
    {
        public int id { get; set; }
        public string date { get; set; } = null!;
        public int duration { get; set; }
        public bool incomplete { get; set; }
        public bool missing { get; set; }
        public bool sbd { get; set; }
        public bool remastered { get; set; }
        public List<PhishinTag> tags { get; set; } = null!;
        public int tour_id { get; set; }
        public PhishinSmallVenue venue { get; set; } = null!;
        public string taper_notes { get; set; } = null!;
        public int likes_count { get; set; }
        public List<PhishinShowTrack> tracks { get; set; } = null!;
        public DateTime updated_at { get; set; }
    }

    public class PhishinTag
    {
        public long id { get; set; }
        public string name { get; set; } = null!;
        public long priority { get; set; }
        public string group { get; set; } = null!;
        public string color { get; set; } = null!;
        public object notes { get; set; } = null!;
    }
}
