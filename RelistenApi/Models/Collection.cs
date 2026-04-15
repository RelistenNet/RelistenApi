using System;
using System.Collections.Generic;
using Relisten.Api.Models.Api;

namespace Relisten.Api.Models
{
    public enum ArchiveCollectionItemImportStatus
    {
        Pending = 0,
        LinkedExistingSource = 1,
        ImportedSource = 2,
        Skipped = 3,
        ImportError = 4
    }

    public sealed class ArchiveCollection : BaseRelistenModel, IHasPersistentIdentifier
    {
        public Guid uuid { get; set; }
        public string slug { get; set; } = null!;
        public int upstream_source_id { get; set; }
        public string upstream_identifier { get; set; } = null!;
        public string collection_type { get; set; } = null!;
        public string name { get; set; } = null!;
        public string? description { get; set; }
        public int item_count { get; set; }
        public DateTime? indexed_at { get; set; }
        public DateTime? last_imported_at { get; set; }
    }

    public sealed class CollectionItem
    {
        public Guid collection_uuid { get; set; }
        public string upstream_identifier { get; set; } = null!;
        public string title { get; set; } = null!;
        public string? creator_raw { get; set; }
        public string? date_raw { get; set; }
        public string? display_date { get; set; }
        public int? year { get; set; }
        public Guid? artist_uuid { get; set; }
        public Guid? show_uuid { get; set; }
        public Guid? source_uuid { get; set; }
        public ArchiveCollectionItemImportStatus import_status { get; set; }
        public string? import_error { get; set; }
        public DateTime last_seen_at { get; set; }
        public DateTime? removed_at { get; set; }
        public DateTime? last_imported_at { get; set; }
        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }
    }

    public sealed class CollectionArtistMapping
    {
        public Guid collection_uuid { get; set; }
        public string creator_name { get; set; } = null!;
        public Guid? artist_uuid { get; set; }
        public string canonical_name { get; set; } = null!;
        public bool blocked { get; set; }
        public string? block_reason { get; set; }
        public string decision_source { get; set; } = null!;
        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }
    }

    public class CollectionSummary
    {
        public Guid uuid { get; set; }
        public string slug { get; set; } = null!;
        public string upstream_identifier { get; set; } = null!;
        public string name { get; set; } = null!;
        public int item_count { get; set; }
        public int artist_count { get; set; }
        public int show_count { get; set; }
        public int source_count { get; set; }
        public DateTime? indexed_at { get; set; }
    }

    public sealed class CollectionDetail : CollectionSummary
    {
        public string? description { get; set; }
    }

    public class CollectionYear
    {
        public Guid uuid { get; set; }
        public Guid collection_uuid { get; set; }
        public string year { get; set; } = null!;
        public int artist_count { get; set; }
        public int show_count { get; set; }
        public int source_count { get; set; }
        public long duration { get; set; }
        public double? avg_duration { get; set; }
        public double? avg_rating { get; set; }
        public PopularityMetrics? popularity { get; set; }
    }

    public sealed class CollectionYearWithShows : CollectionYear
    {
        public List<Show> shows { get; set; } = new();
    }

    public sealed class CollectionPopularTrendingShowsResponse
    {
        public Guid collection_uuid { get; set; }
        public string collection_slug { get; set; } = null!;
        public string collection_name { get; set; } = null!;
        public IReadOnlyList<Show> popular_shows { get; set; } = Array.Empty<Show>();
        public IReadOnlyList<Show> trending_shows { get; set; } = Array.Empty<Show>();
    }

    public sealed class ArchiveItemImportResult
    {
        public ArchiveCollectionItemImportStatus status { get; set; }
        public Guid? source_uuid { get; set; }
        public string? skip_reason { get; set; }
        public string? error_message { get; set; }
    }

    public sealed class ArchiveOrgImportContext
    {
        public int upstream_source_id { get; set; } = 1;
    }
}
