using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Relisten.Vendor.ArchiveOrg
{
    public class TolerantStringConverter : CustomCreationConverter<string>
    {
        public override string Create(Type objectType)
        {
            return "";
        }

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue,
            JsonSerializer serializer)
        {
            // Load JObject from stream 
            if (reader.TokenType == JsonToken.StartArray)
            {
                var l = new List<string>();
                serializer.Populate(reader, l);
                return l.FirstOrDefault();
            }

            if (reader.TokenType == JsonToken.StartObject)
            {
                // consume the blank object
                var l = new object();
                serializer.Populate(reader, l);

                return "";
            }

            return reader.Value;
        }
    }

    public class SingleOrArrayConverter<T> : JsonConverter
    {
        public override bool CanWrite => true;

        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(List<T>);
        }

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue,
            JsonSerializer serializer)
        {
            var token = JToken.Load(reader);
            if (token.Type == JTokenType.Array)
            {
                return token.ToObject<List<T>>();
            }

            return new List<T> {token.ToObject<T>()!};
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            var list = (List<T>)value!;
            if (list.Count == 1)
            {
                value = list[0];
            }

            serializer.Serialize(writer, value);
        }
    }

    public class TolerantArchiveDateTimeConverter : CustomCreationConverter<DateTime>
    {
        public override DateTime Create(Type objectType)
        {
            return new DateTime();
        }

        private int BoundedInt(string s, int start, int len, int min, int max)
        {
            int i;

            if (int.TryParse(s.Substring(start, len), out i))
            {
                return Math.Min(max, Math.Max(min, i));
            }

            return min;
        }

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue,
            JsonSerializer serializer)
        {
            // Load JObject from stream 
            if (reader.TokenType == JsonToken.String)
            {
                var s = reader.Value?.ToString();

                if (string.IsNullOrEmpty(s))
                {
                    return reader.Value;
                }

                // wtf is this archive.org??
                s = s.Replace("T::Z", "T00:00:00Z");

                // really. what the hell are you doing archive.org?!
                if (s == "0000-01-01T00:00:00Z")
                {
                    return null;
                }

                if (s.Length == 20)
                {
                    return new DateTime(BoundedInt(s, 0, 4, 1, 9999), BoundedInt(s, 5, 2, 1, 12),
                        BoundedInt(s, 8, 2, 1, 30), BoundedInt(s, 11, 2, 0, 23),
                        BoundedInt(s, 14, 2, 0, 59), BoundedInt(s, 17, 2, 0, 59),
                        DateTimeKind.Utc
                    );
                }

                return DateTime.Parse(s, null);
            }

            return reader.Value;
        }
    }

    public class SearchParams
    {
        public string q { get; set; } = null!;
        public string qin { get; set; } = null!;
        public string fl { get; set; } = null!;
        public string wt { get; set; } = null!;
        public string sort { get; set; } = null!;
        public string rows { get; set; } = null!;
        public int start { get; set; }
    }

    public class SearchResponseHeader
    {
        public int status { get; set; }
        public int QTime { get; set; }

        [JsonProperty("@params")] public SearchParams parameters { get; set; } = null!;
    }

    public class SearchDoc
    {
        private DateTime? _max;
        public DateTime date { get; set; }
        public string identifier { get; set; } = null!;
        public DateTime? addeddate { get; set; }
        public DateTime? publicdate { get; set; }

        [JsonConverter(typeof(SingleOrArrayConverter<DateTime>))]
        public List<DateTime> updatedate { get; set; } = null!;

        public DateTime? reviewdate { get; set; }
        public DateTime? indexdate { get; set; }

        public DateTime _iguana_index_date
        {
            get
            {
                if (_max == null)
                {
                    _max = new[] {addeddate, publicdate, reviewdate /*, indexdate*/}
                        .Where(d => d.HasValue).Max()!.Value;
                }

                return _max.Value;
            }
        }

        public DateTime _iguana_updated_at
        {
            get
            {
                var dates = new[]
                {
                    updatedate != null ? updatedate.Max() : (DateTime?)DateTime.MinValue, addeddate, publicdate
                }.Where(d => d.HasValue).ToList();

                if (dates.Count == 0)
                {
                    throw new Exception(
                        $"SearchDoc '{identifier}' has no addeddate, publicdate or updatedate...this shouldn't be possible...");
                }

                return dates.Max()!.Value;
            }
        }
    }

    public class SearchResponse
    {
        public int numFound { get; set; }
        public int start { get; set; }
        public IList<SearchDoc> docs { get; set; } = null!;
    }

    public class SearchRootObject
    {
        public SearchResponseHeader responseHeader { get; set; } = null!;
        public SearchResponse response { get; set; } = null!;
    }
}

namespace Relisten.Vendor.ArchiveOrg.Metadata
{
    public class File
    {
        public string name { get; set; } = null!;
        public string format { get; set; } = null!;
        public string size { get; set; } = null!;
        public string md5 { get; set; } = null!;
        public string length { get; set; } = null!;
        public string title { get; set; } = null!;
        public string track { get; set; } = null!;
        public string original { get; set; } = null!;
    }

    public class Metadata
    {
        public string identifier { get; set; } = null!;
        public string date { get; set; } = null!;
        public string title { get; set; } = null!;
        public string description { get; set; } = null!;
        public string venue { get; set; } = null!;
        public string coverage { get; set; } = null!;
        public string source { get; set; } = null!;
        public string lineage { get; set; } = null!;
        public string taper { get; set; } = null!;
        public string transferer { get; set; } = null!;
        public string notes { get; set; } = null!;
    }

    public class Review
    {
        public string reviewbody { get; set; } = null!;
        public string reviewtitle { get; set; } = null!;
        public string reviewer { get; set; } = null!;
        public DateTime reviewdate { get; set; }
        public DateTime createdate { get; set; }
        public int stars { get; set; }
    }

    public class RootObject
    {
        public int created { get; set; }
        public bool? is_dark { get; set; }
        public string d1 { get; set; } = null!;
        public string d2 { get; set; } = null!;
        public string dir { get; set; } = null!;
        public List<File> files { get; set; } = null!;
        public int files_count { get; set; }
        public long item_size { get; set; }
        public Metadata metadata { get; set; } = null!;
        public List<Review> reviews { get; set; } = null!;
        public string server { get; set; } = null!;
        public int uniq { get; set; }
        public int updated { get; set; }
        public List<string> workable_servers { get; set; } = null!;
    }
}
