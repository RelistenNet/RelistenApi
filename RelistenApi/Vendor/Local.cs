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
        public IDictionary<string, IList<T>> data { get; set; }
    }

    public class LocalShowObject
    {
        public List<LocalTrack> tracks { get; set; }
        public List<string> txts { get; set; }
    }

    public class LocalEraReiponse : LocalRootObject<IDictionary<string, IList<string>>>
    {
    }

    public class LocalSmallTour
    {
        public int id { get; set; }
        public string name { get; set; }
        public int shows_count { get; set; }
        public string starts_on { get; set; }
        public string ends_on { get; set; }
        public string slug { get; set; }
        public DateTime updated_at { get; set; }
    }

    public class LocalTour : LocalSmallTour
    {
        public List<LocalSmallShow> shows { get; set; }
    }

    public class LocalSmallShow
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

    public class LocalSmallVenue
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

    public class LocalVenue
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

    public class LocalSmallSong
    {
        public int id { get; set; }
        public string title { get; set; }
        public int? alias_for { get; set; }
        public int tracks_count { get; set; }
        public string slug { get; set; }
        public DateTime updated_at { get; set; }
    }

    public class LocalSong
    {
        public int id { get; set; }
        public string title { get; set; }
        public int? alias_for { get; set; }
        public int tracks_count { get; set; }
        public string slug { get; set; }
        public DateTime updated_at { get; set; }
        public List<LocalTrack> tracks { get; set; }
    }

    public class LocalTrack
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
        public LocalTrackTags tags { get; set; }
    }

    public class LocalTrackTags {
        public long duration { get; set; }
        public string title { get; set; }
        public LocalTrackTagsTrack track { get; set; }
    }

    public class LocalTrackTagsTrack {
        public int no { get; set; }
        public int of { get; set; }
    }

    public class LocalShowTrack : LocalTrack
    {
        public IList<int> song_ids { get; set; }
    }

    public class LocalShow
    {
        public int id { get; set; }
        public string date { get; set; }
        public string year { get; set; }
        public string month { get; set; }
        public string day { get; set; }
        public string dir { get; set; }
        public int duration { get; set; }
        public bool incomplete { get; set; }
        public bool missing { get; set; }
        public bool sbd { get; set; }
        public bool remastered { get; set; }
        public List<LocalTag> tags { get; set; }
        public int tour_id { get; set; }
        public string venue { get; set; }
        public string city { get; set; }
        public string state { get; set; }
        public string taper_notes { get; set; }
        public int likes_count { get; set; }
        public List<LocalShowTrack> tracks { get; set; }
        public DateTime updated_at { get; set; }
    }


    public class ShowMetadata
    {
        public int id { get; set; }
        public string date { get; set; }
        public string year { get; set; }
        public string month { get; set; }
        public string day { get; set; }
        public string dir { get; set; }
        public int duration { get; set; }
        public bool incomplete { get; set; }
        public bool missing { get; set; }
        public bool sbd { get; set; }
        public bool remastered { get; set; }
        public List<LocalTag> tags { get; set; }
        public int tour_id { get; set; }
        public LocalSmallVenue venue { get; set; }
        public string taper_notes { get; set; }
        public int likes_count { get; set; }
        public List<LocalShowTrack> tracks { get; set; }
        public DateTime updated_at { get; set; }
    }

    public class LocalTag
    {
        public long id { get; set; }
        public string name { get; set; }
        public long priority { get; set; }
        public string group { get; set; }
        public string color { get; set; }
        public object notes { get; set; }
    }
}
