using System;
using System.Collections.Generic;

namespace Relisten.Vendor.PhantasyTour
{
    public class PhantasyTourShowListing
    {
        public int id { get; set; }
        public DateTime dateTime { get; set; }
        public string url { get; set; } = null!;
        public PhantasyTourBand band { get; set; } = null!;
        public PhantasyTourVenue venue { get; set; } = null!;
        public string ticketUrl { get; set; } = null!;
        public bool hasSetlist { get; set; }
        public bool attended { get; set; }
    }


    public class PhantasyTourBand
    {
        public int id { get; set; }
        public string name { get; set; } = null!;
        public string url { get; set; } = null!;
    }

    public class PhantasyTourVenue
    {
        public int id { get; set; }
        public string name { get; set; } = null!;
        public string url { get; set; } = null!;
        public string city { get; set; } = null!;
        public string state { get; set; } = null!;
        public string country { get; set; } = null!;
        public string locale { get; set; } = null!;
    }

    public class PhantasyTourTour
    {
        public int id { get; set; }
        public string name { get; set; } = null!;
        public string url { get; set; } = null!;
    }

    public class PhantasyTourSource
    {
        public int id { get; set; }
        public string name { get; set; } = null!;
        public string url { get; set; } = null!;
    }

    public class PhantasyTourSong
    {
        public int id { get; set; }
        public string name { get; set; } = null!;
        public string url { get; set; } = null!;
        public bool segue { get; set; }
        public List<int> footnotes { get; set; } = null!;
    }

    public class PhantasyTourSet
    {
        public int id { get; set; }
        public string name { get; set; } = null!;
        public List<PhantasyTourSong> songs { get; set; } = null!;
    }

    public class PhantasyTourShow
    {
        public int id { get; set; }
        public object name { get; set; } = null!;
        public string url { get; set; } = null!;
        public DateTime dateTimeUtc { get; set; }
        public DateTime dateTime { get; set; }
        public string timezoneStandard { get; set; } = null!;
        public string timezone { get; set; } = null!;
        public object openers { get; set; } = null!;
        public object headliners { get; set; } = null!;
        public object credits { get; set; } = null!;
        public object prenotes { get; set; } = null!;
        public object postnotes { get; set; } = null!;
        public bool cancelled { get; set; }
        public bool soldout { get; set; }
        public object ticketUrl { get; set; } = null!;
        public PhantasyTourBand band { get; set; } = null!;
        public PhantasyTourVenue venue { get; set; } = null!;
        public object scene { get; set; } = null!;
        public PhantasyTourTour tour { get; set; } = null!;
        public PhantasyTourSource source { get; set; } = null!;
        public object festival { get; set; } = null!;
        public List<PhantasyTourSet> sets { get; set; } = null!;
        public List<PhantasyTourFootnote> footnotes { get; set; } = null!;
        public string timestamp { get; set; } = null!;
    }

    public class PhantasyTourFootnote
    {
        public int id { get; set; }
        public string note { get; set; } = null!;
    }

    public class PhantasyTourEnvelope
    {
        public PhantasyTourShow data { get; set; } = null!;
    }
}
