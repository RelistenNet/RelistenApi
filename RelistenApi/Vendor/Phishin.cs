using System;
using System.Collections.Generic;

namespace Relisten.Vendor.Phishin
{
    public class PhishinYear
    {
        public string period { get; set; } = null!;
        public string era { get; set; } = null!;
        public int shows_count { get; set; }
    }

    public class PhishinSmallTour
    {
        public string slug { get; set; } = null!;
        public string name { get; set; } = null!;
        public int shows_count { get; set; }
        public string starts_on { get; set; } = null!;
        public string ends_on { get; set; } = null!;
        public DateTime updated_at { get; set; }
    }

    public class PhishinSmallVenue
    {
        public string slug { get; set; } = null!;
        public string name { get; set; } = null!;
        public List<string> other_names { get; set; } = null!;
        public double? latitude { get; set; }
        public double? longitude { get; set; }
        public int shows_count { get; set; }
        public string location { get; set; } = null!;
        public DateTime updated_at { get; set; }
    }

    public class PhishinSmallSong
    {
        public string slug { get; set; } = null!;
        public string title { get; set; } = null!;
        public string? alias { get; set; }
        public int tracks_count { get; set; }
        public DateTime updated_at { get; set; }
    }

    public class PhishinTrackSong
    {
        public string slug { get; set; } = null!;
        public string title { get; set; } = null!;
    }

    public class PhishinTrack
    {
        public int id { get; set; }
        public string title { get; set; } = null!;
        public int duration { get; set; }
        public string show_date { get; set; } = null!;
        public string set_name { get; set; } = null!;
        public int position { get; set; }
        public int likes_count { get; set; }
        public string slug { get; set; } = null!;
        public DateTime? updated_at { get; set; }
        public DateTime? created_at { get; set; }
        public string mp3_url { get; set; } = null!;
    }

    public class PhishinShowTrack : PhishinTrack
    {
        public IList<PhishinTrackSong> songs { get; set; } = null!;
    }

    public class PhishinShow
    {
        public int id { get; set; }
        public string date { get; set; } = null!;
        public int duration { get; set; }
        public string? tour_name { get; set; }
        public List<PhishinTag> tags { get; set; } = null!;
        public PhishinSmallVenue venue { get; set; } = null!;
        public string taper_notes { get; set; } = null!;
        public int likes_count { get; set; }
        public List<PhishinShowTrack>? tracks { get; set; }
        public DateTime updated_at { get; set; }
    }

    public class PhishinTag
    {
        public string name { get; set; } = null!;
        public string? description { get; set; }
        public long priority { get; set; }
        public string? color { get; set; }
        public object? notes { get; set; }
    }
}
