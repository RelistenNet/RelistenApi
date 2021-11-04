using System;
using System.Collections.Generic;

namespace Relisten.Vendor.PhantasyTour
{
    public class PhantasyTourShowListing
    {
        public int id { get; set; }
        public DateTime dateTime { get; set; }
        public string url { get; set; }
        public PhantasyTourBand band { get; set; }
        public PhantasyTourVenue venue { get; set; }
        public string ticketUrl { get; set; }
        public bool hasSetlist { get; set; }
        public bool attended { get; set; }
    }


    public class PhantasyTourBand
    {
        public int id { get; set; }
        public string name { get; set; }
        public string url { get; set; }
    }

    public class PhantasyTourVenue
    {
        public int id { get; set; }
        public string name { get; set; }
        public string url { get; set; }
        public string city { get; set; }
        public string state { get; set; }
        public string country { get; set; }
        public string locale { get; set; }
    }

    public class PhantasyTourTour
    {
        public int id { get; set; }
        public string name { get; set; }
        public string url { get; set; }
    }

    public class PhantasyTourSource
    {
        public int id { get; set; }
        public string name { get; set; }
        public string url { get; set; }
    }

    public class PhantasyTourSong
    {
        public int id { get; set; }
        public string name { get; set; }
        public string url { get; set; }
        public bool segue { get; set; }
        public List<int> footnotes { get; set; }
    }

    public class PhantasyTourSet
    {
        public int id { get; set; }
        public string name { get; set; }
        public List<PhantasyTourSong> songs { get; set; }
    }

    public class PhantasyTourShow
    {
        public int id { get; set; }
        public object name { get; set; }
        public string url { get; set; }
        public DateTime dateTimeUtc { get; set; }
        public DateTime dateTime { get; set; }
        public string timezoneStandard { get; set; }
        public string timezone { get; set; }
        public object openers { get; set; }
        public object headliners { get; set; }
        public object credits { get; set; }
        public object prenotes { get; set; }
        public object postnotes { get; set; }
        public bool cancelled { get; set; }
        public bool soldout { get; set; }
        public object ticketUrl { get; set; }
        public PhantasyTourBand band { get; set; }
        public PhantasyTourVenue venue { get; set; }
        public object scene { get; set; }
        public PhantasyTourTour tour { get; set; }
        public PhantasyTourSource source { get; set; }
        public object festival { get; set; }
        public List<PhantasyTourSet> sets { get; set; }
        public List<PhantasyTourFootnote> footnotes { get; set; }
        public string timestamp { get; set; }
    }

    public class PhantasyTourFootnote
    {
        public int id { get; set; }
        public string note { get; set; }
    }

    public class PhantasyTourEnvelope
    {
        public PhantasyTourShow data { get; set; }
    }
}
