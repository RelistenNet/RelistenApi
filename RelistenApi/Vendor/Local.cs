using System;
using System.Collections.Generic;

namespace Relisten.Vendor.Local
{
    public class LocalRootObject<T>
    {
        public bool success { get; set; }
        public int total_entries { get; set; }
        public int total_pages { get; set; }
        public int page { get; set; }
        public IDictionary<string, IList<T>> data { get; set; } = null!;
    }

    public class LocalShowObject
    {
        public List<LocalTrack> tracks { get; set; } = null!;
        public List<string> txts { get; set; } = null!;
    }

    public class LocalEraReiponse : LocalRootObject<IDictionary<string, IList<string>>>
    {
    }

    public class LocalSmallTour
    {
        public int id { get; set; }
        public string name { get; set; } = null!;
        public int shows_count { get; set; }
        public string starts_on { get; set; } = null!;
        public string ends_on { get; set; } = null!;
        public string slug { get; set; } = null!;
        public DateTime updated_at { get; set; }
    }

    public class LocalTour : LocalSmallTour
    {
        public List<LocalSmallShow> shows { get; set; } = null!;
    }

    public class LocalSmallShow
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

    public class LocalSmallVenue
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

    public class LocalVenue
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

    public class LocalSmallSong
    {
        public int id { get; set; }
        public string title { get; set; } = null!;
        public int? alias_for { get; set; }
        public int tracks_count { get; set; }
        public string slug { get; set; } = null!;
        public DateTime updated_at { get; set; }
    }

    public class LocalSong
    {
        public int id { get; set; }
        public string title { get; set; } = null!;
        public int? alias_for { get; set; }
        public int tracks_count { get; set; }
        public string slug { get; set; } = null!;
        public DateTime updated_at { get; set; }
        public List<LocalTrack> tracks { get; set; } = null!;
    }

    public class LocalTrack
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
        public string mp3 { get; set; } = null!;
        public LocalTrackTags tags { get; set; } = null!;
    }

    public class LocalTrackTags {
        public long duration { get; set; }
        public string title { get; set; } = null!;
        public LocalTrackTagsTrack track { get; set; } = null!;
    }

    public class LocalTrackTagsTrack {
        public int no { get; set; }
        public int of { get; set; }
    }

    public class LocalShowTrack : LocalTrack
    {
        public IList<int> song_ids { get; set; } = null!;
    }

    public class LocalShow
    {
        public int id { get; set; }
        public string date { get; set; } = null!;
        public string year { get; set; } = null!;
        public string month { get; set; } = null!;
        public string day { get; set; } = null!;
        public string dir { get; set; } = null!;
        public int duration { get; set; }
        public bool incomplete { get; set; }
        public bool missing { get; set; }
        public bool sbd { get; set; }
        public bool remastered { get; set; }
        public List<LocalTag> tags { get; set; } = null!;
        public int tour_id { get; set; }
        public string venue { get; set; } = null!;
        public string city { get; set; } = null!;
        public string state { get; set; } = null!;
        public string taper_notes { get; set; } = null!;
        public int likes_count { get; set; }
        public List<LocalShowTrack> tracks { get; set; } = null!;
        public DateTime updated_at { get; set; }
    }


    public class ShowMetadata
    {
        public int id { get; set; }
        public string date { get; set; } = null!;
        public string year { get; set; } = null!;
        public string month { get; set; } = null!;
        public string day { get; set; } = null!;
        public string dir { get; set; } = null!;
        public int duration { get; set; }
        public bool incomplete { get; set; }
        public bool missing { get; set; }
        public bool sbd { get; set; }
        public bool remastered { get; set; }
        public List<LocalTag> tags { get; set; } = null!;
        public int tour_id { get; set; }
        public LocalSmallVenue venue { get; set; } = null!;
        public string taper_notes { get; set; } = null!;
        public int likes_count { get; set; }
        public List<LocalShowTrack> tracks { get; set; } = null!;
        public DateTime updated_at { get; set; }
    }

    public class LocalTag
    {
        public long id { get; set; }
        public string name { get; set; } = null!;
        public long priority { get; set; }
        public string group { get; set; } = null!;
        public string color { get; set; } = null!;
        public object notes { get; set; } = null!;
    }
}
