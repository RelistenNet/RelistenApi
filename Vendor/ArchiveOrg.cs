
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Relisten.Vendor.ArchiveOrg
{
    class TolerantStringConverter : Newtonsoft.Json.Converters.CustomCreationConverter<string>
    {
        public override string Create(Type objectType)
        {
            return "";
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            // Load JObject from stream 
            if (reader.TokenType == JsonToken.StartArray)
            {
                var l = new List<string>();
                serializer.Populate(reader, l);
                return l.FirstOrDefault();
            }

            return reader.Value;
        }
    }
    public class SearchParams
    {
        public string q { get; set; }
        public string qin { get; set; }
        public string fl { get; set; }
        public string wt { get; set; }
        public string sort { get; set; }
        public string rows { get; set; }
        public int start { get; set; }
    }

    public class SearchResponseHeader
    {
        public int status { get; set; }
        public int QTime { get; set; }
        [JsonProperty("@params")]
        public SearchParams parameters { get; set; }
    }

    public class SearchDoc
    {
        public DateTime date { get; set; }
        public string identifier { get; set; }
        public IList<DateTime> oai_updatedate { get; set; }

        private DateTime? _maxOaiUpdateDate = null;
        public DateTime _iguana_updated_at
        {
            get
            {
                if (!_maxOaiUpdateDate.HasValue)
                {
                    _maxOaiUpdateDate = oai_updatedate.Max();
                }
                return _maxOaiUpdateDate.Value;
            }
        }
    }

    public class SearchResponse
    {
        public int numFound { get; set; }
        public int start { get; set; }
        public IList<SearchDoc> docs { get; set; }
    }

    public class SearchRootObject
    {
        public SearchResponseHeader responseHeader { get; set; }
        public SearchResponse response { get; set; }
    }

}

namespace Relisten.Vendor.ArchiveOrg.Metadata
{
    public class File
    {
        public string name { get; set; }
        public string format { get; set; }
        public string size { get; set; }
        public string md5 { get; set; }
        public string length { get; set; }
        public string title { get; set; }
        public string track { get; set; }
        public string original { get; set; }
    }

    public class Metadata
    {
        public string identifier { get; set; }
        public string date { get; set; }
        public string title { get; set; }
        public string description { get; set; }
        public string venue { get; set; }
        public string coverage { get; set; }
        public string source { get; set; }
        public string lineage { get; set; }
        public string taper { get; set; }
        public string transferer { get; set; }
        public string notes { get; set; }
    }

    public class Review
    {
        public string reviewbody { get; set; }
        public string reviewtitle { get; set; }
        public string reviewer { get; set; }
        public DateTime reviewdate { get; set; }
        public DateTime createdate { get; set; }
        public int stars { get; set; }
    }

    public class RootObject
    {
        public int created { get; set; }
        public string d1 { get; set; }
        public string d2 { get; set; }
        public string dir { get; set; }
        public List<File> files { get; set; }
        public int files_count { get; set; }
        public long item_size { get; set; }
        public Metadata metadata { get; set; }
        public List<Review> reviews { get; set; }
        public string server { get; set; }
        public int uniq { get; set; }
        public int updated { get; set; }
        public List<string> workable_servers { get; set; }
    }

}
