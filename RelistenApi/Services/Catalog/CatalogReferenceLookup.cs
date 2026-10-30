using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Relisten.Identifiers;

namespace Relisten.Catalog;

/// <summary>
/// Resolves validated identities in the caller's snapshot. Lookups group by kind and
/// scheme so UUID requests use indexed equality, not a broad OR across legacy aliases.
/// Imported identities remain imported; normalization never changes a saved target's kind.
/// </summary>
public sealed class CatalogReferenceLookup(ILogger<CatalogReferenceLookup> logger)
{
    public static bool Supports(CatalogReferenceKind kind, CatalogReferenceScheme scheme) =>
        scheme switch
        {
            CatalogReferenceScheme.V4 => Enum.IsDefined(kind),
            CatalogReferenceScheme.LegacyUuid => Enum.IsDefined(kind)
                && kind != CatalogReferenceKind.ArtistVenue,
            CatalogReferenceScheme.LegacyInteger => Enum.IsDefined(kind) && !IsShort(kind),
            CatalogReferenceScheme.LegacySlug => kind
                is CatalogReferenceKind.Artist
                    or CatalogReferenceKind.Track
                    or CatalogReferenceKind.Tour
                    or CatalogReferenceKind.ImportedVenue
                    or CatalogReferenceKind.ImportedSong
                    or CatalogReferenceKind.Venue
                    or CatalogReferenceKind.Song,
            _ => false,
        };

    /// <summary>The codec prefix, shared with request validation.</summary>
    public static string Prefix(CatalogReferenceKind kind) => Spec(kind).Prefix;

    public async Task<IReadOnlyList<CatalogReferenceResolution>> Read(
        CatalogReadSession session,
        IReadOnlyList<CatalogReference> references,
        CancellationToken cancellationToken
    )
    {
        var candidates = await ReadCandidates(session, references, cancellationToken);
        var rows = candidates.Values.SelectMany(value => value).ToArray();
        var venues = await ReadCanonicalIds(
            session,
            CatalogReferenceKind.Venue,
            rows.Where(row => row.Kind == CatalogReferenceKind.Venue)
                .Select(row => row.public_id!)
                .Concat(
                    rows.Where(row => row.Kind == CatalogReferenceKind.ArtistVenue && row.approved)
                        .Select(row => row.venue_id!)
                ),
            cancellationToken
        );
        var songs = await ReadCanonicalIds(
            session,
            CatalogReferenceKind.Song,
            rows.Where(row => row.Kind == CatalogReferenceKind.Song && row.approved)
                .Select(row => row.public_id!),
            cancellationToken
        );
        var artistVenues = await ReadArtistVenuePairs(session, rows, venues, cancellationToken);
        var retired = await ReadRetiredIds(session, candidates, cancellationToken);

        // Database work deduplicates identical requests; the response preserves caller
        // order and duplicates so clients can associate every result with their input.
        return references
            .Select(reference =>
                Resolve(reference, candidates[reference], venues, songs, artistVenues, retired)
            )
            .ToArray();
    }

    private static async Task<Dictionary<CatalogReference, List<LookupRow>>> ReadCandidates(
        CatalogReadSession session,
        IReadOnlyList<CatalogReference> references,
        CancellationToken cancellationToken
    )
    {
        var result = references
            .Distinct()
            .ToDictionary(reference => reference, _ => new List<LookupRow>());
        foreach (var group in result.Keys.GroupBy(reference => (reference.Kind, reference.Scheme)))
        {
            var requests = group.ToArray();
            var kind = group.Key.Kind;
            var scheme = group.Key.Scheme;
            var spec = Spec(kind);
            var ids = requests
                .Select(reference =>
                    scheme == CatalogReferenceScheme.V4 && !IsShort(kind)
                        ? UuidTypeId.Parse(reference.Id, spec.Prefix).ToString("D")
                        : reference.Id
                )
                .ToArray();
            var identityColumn = scheme switch
            {
                CatalogReferenceScheme.V4 => IsShort(kind) ? "e.id" : "e.uuid",
                CatalogReferenceScheme.LegacyUuid => IsShort(kind) ? "e.retired_uuid" : "e.uuid",
                CatalogReferenceScheme.LegacyInteger => "e.id",
                CatalogReferenceScheme.LegacySlug => "e.slug",
                _ => throw new InvalidOperationException("Unsupported validated identity scheme."),
            };
            var inputCast = scheme switch
            {
                CatalogReferenceScheme.LegacyUuid => "::uuid",
                CatalogReferenceScheme.LegacyInteger => "::bigint",
                CatalogReferenceScheme.V4 when !IsShort(kind) => "::uuid",
                _ => "",
            };
            var shortIdentity = IsShort(kind);
            var approval = kind is CatalogReferenceKind.Song or CatalogReferenceKind.ArtistVenue
                ? "COALESCE(f.norm_catalogue,false)"
                : "true";
            var ownerFilter = kind is CatalogReferenceKind.Artist or CatalogReferenceKind.Venue
                ? "true"
                : $"(r.artist_uuid IS NULL OR {spec.Owner}=r.artist_uuid)";
            // Every SQL fragment comes from the fixed kind/scheme map; caller values
            // remain parameters, including numeric and slug aliases.
            var found = await session.Connection.QueryAsync<LookupRow>(
                session.Command(
                    $"""
                    WITH requested AS (
                        SELECT id,artist_uuid,(ordinality-1)::int request_index
                        FROM unnest(@ids::text[],@artists::uuid[]) WITH ORDINALITY r(id,artist_uuid,ordinality)
                    )
                    SELECT r.request_index,
                           {(shortIdentity ? "e.id" : "NULL::text")} public_id,
                           {(shortIdentity ? "NULL::uuid" : "e.uuid")} uuid,
                           {spec.Owner} artist_uuid,{approval} approved,
                           {(
                        kind == CatalogReferenceKind.ArtistVenue ? "e.venue_id" : "NULL::text"
                    )} venue_id
                    FROM {spec.From} JOIN requested r ON {identityColumn}=r.id{inputCast}
                    WHERE {ownerFilter} AND {(
                        kind == CatalogReferenceKind.Track ? "NOT e.is_orphaned" : "true"
                    )}
                    """,
                    new
                    {
                        ids,
                        artists = requests.Select(reference => reference.ArtistUuid).ToArray(),
                    },
                    cancellationToken
                )
            );
            foreach (var row in found)
            {
                row.Kind = kind;
                result[requests[row.request_index]].Add(row);
            }
        }
        return result;
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadCanonicalIds(
        CatalogReadSession session,
        CatalogReferenceKind kind,
        IEnumerable<string> requestedIds,
        CancellationToken cancellationToken
    )
    {
        var ids = requestedIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<string, string>();
        }
        var song = kind == CatalogReferenceKind.Song;
        var table = song ? "norm.songs" : "norm.venues";
        var owners = song ? "e.artist_uuid" : "NULL::uuid";
        var rows = await session.Connection.QueryAsync<CanonicalRow>(
            session.Command(
                $"""
                WITH RECURSIVE chain AS (
                    SELECT e.id requested_id,e.id,{owners} artist_uuid,e.merged_into,ARRAY[e.id] path
                    FROM {table} e WHERE e.id=ANY(@ids)
                    UNION ALL
                    SELECT c.requested_id,e.id,{owners},e.merged_into,c.path || e.id
                    FROM chain c JOIN {table} e ON e.id=c.merged_into
                    WHERE cardinality(c.path)<32 AND NOT e.id=ANY(c.path)
                      AND {(song ? "e.artist_uuid=c.artist_uuid" : "true")}
                )
                SELECT requested_id,id FROM chain WHERE merged_into IS NULL
                """,
                new { ids },
                cancellationToken
            )
        );
        return rows.ToDictionary(row => row.requested_id, row => row.id);
    }

    private static async Task<
        IReadOnlyDictionary<(Guid Artist, string Venue), string>
    > ReadArtistVenuePairs(
        CatalogReadSession session,
        IReadOnlyList<LookupRow> rows,
        IReadOnlyDictionary<string, string> venues,
        CancellationToken cancellationToken
    )
    {
        var pairs = rows.Where(row => row.Kind == CatalogReferenceKind.ArtistVenue && row.approved)
            .Where(row => venues.ContainsKey(row.venue_id!))
            .Select(row => (Artist: row.artist_uuid!.Value, Venue: venues[row.venue_id!]))
            .Distinct()
            .ToArray();
        if (pairs.Length == 0)
        {
            return new Dictionary<(Guid, string), string>();
        }
        var current = await session.Connection.QueryAsync<ArtistVenuePair>(
            session.Command(
                """
                SELECT av.id,av.artist_uuid,av.venue_id
                FROM unnest(@artists::uuid[],@venues::text[]) r(artist_uuid,venue_id)
                JOIN norm.artist_venues av ON av.artist_uuid=r.artist_uuid AND av.venue_id=r.venue_id
                """,
                new
                {
                    artists = pairs.Select(pair => pair.Artist).ToArray(),
                    venues = pairs.Select(pair => pair.Venue).ToArray(),
                },
                cancellationToken
            )
        );
        return current.ToDictionary(row => (row.artist_uuid, row.venue_id), row => row.id);
    }

    private static async Task<HashSet<string>> ReadRetiredIds(
        CatalogReadSession session,
        IReadOnlyDictionary<CatalogReference, List<LookupRow>> candidates,
        CancellationToken cancellationToken
    )
    {
        var ids = candidates
            .Where(pair =>
                pair.Value.Count == 0
                && pair.Key.Scheme == CatalogReferenceScheme.V4
                && pair.Key.Kind is CatalogReferenceKind.Venue or CatalogReferenceKind.Song
            )
            .Select(pair => pair.Key.Id)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return [];
        }
        return (
            await session.Connection.QueryAsync<string>(
                session.Command(
                    "SELECT id FROM norm.public_ids WHERE id=ANY(@ids) AND retired_at IS NOT NULL",
                    new { ids },
                    cancellationToken
                )
            )
        ).ToHashSet(StringComparer.Ordinal);
    }

    private CatalogReferenceResolution Resolve(
        CatalogReference reference,
        IReadOnlyList<LookupRow> candidates,
        IReadOnlyDictionary<string, string> venues,
        IReadOnlyDictionary<string, string> songs,
        IReadOnlyDictionary<(Guid Artist, string Venue), string> artistVenues,
        IReadOnlySet<string> retired
    )
    {
        CatalogReferenceResolution Unavailable(string reason) =>
            new(reference, CatalogReferenceAvailability.Unavailable, null, reason);
        if (candidates.Count == 0)
        {
            return retired.Contains(reference.Id)
                ? new(reference, CatalogReferenceAvailability.Retired, null, null)
                : Unavailable("not_found");
        }
        if (candidates.Count > 1)
        {
            return Unavailable("ambiguous");
        }
        var row = candidates[0];
        if (!row.approved)
        {
            return Unavailable("disabled");
        }
        var id = row.public_id ?? UuidTypeId.Format(Prefix(reference.Kind), row.uuid!.Value);
        var resolvedId = id;
        if (reference.Kind is CatalogReferenceKind.Song or CatalogReferenceKind.Venue)
        {
            var canonical = reference.Kind == CatalogReferenceKind.Song ? songs : venues;
            if (!canonical.TryGetValue(id, out resolvedId))
            {
                logger.LogWarning(
                    "Invalid normalized merge chain while resolving {Kind} {Id}",
                    reference.Kind,
                    id
                );
                return Unavailable("normalization_unavailable");
            }
        }
        if (reference.Kind == CatalogReferenceKind.ArtistVenue)
        {
            if (!venues.TryGetValue(row.venue_id!, out var venue))
            {
                logger.LogWarning(
                    "Invalid normalized venue chain while resolving artist venue {Id}",
                    id
                );
                return Unavailable("normalization_unavailable");
            }
            if (!artistVenues.TryGetValue((row.artist_uuid!.Value, venue), out resolvedId))
            {
                return Unavailable("not_found");
            }
        }
        return new(
            reference,
            id == resolvedId
                ? CatalogReferenceAvailability.Available
                : CatalogReferenceAvailability.Redirected,
            new(reference.Kind, resolvedId, row.uuid),
            null
        );
    }

    private static bool IsShort(CatalogReferenceKind kind) =>
        kind
            is CatalogReferenceKind.Venue
                or CatalogReferenceKind.Song
                or CatalogReferenceKind.ArtistVenue;

    private static EntitySpec Spec(CatalogReferenceKind kind) =>
        kind switch
        {
            CatalogReferenceKind.Artist => new(ArtistId.Prefix, "artists e", "e.uuid"),
            CatalogReferenceKind.Show => new(
                ShowId.Prefix,
                "shows e JOIN artists a ON a.id=e.artist_id",
                "a.uuid"
            ),
            CatalogReferenceKind.Source => new(
                SourceId.Prefix,
                "sources e JOIN artists a ON a.id=e.artist_id JOIN shows sh ON sh.id=e.show_id",
                "a.uuid"
            ),
            CatalogReferenceKind.SourceSet => new(
                SourceSetId.Prefix,
                "source_sets e JOIN sources src ON src.id=e.source_id JOIN artists a ON a.id=src.artist_id",
                "a.uuid"
            ),
            CatalogReferenceKind.Track => new(
                TrackId.Prefix,
                "source_tracks e JOIN sources src ON src.id=e.source_id JOIN source_sets ss ON ss.id=e.source_set_id AND ss.source_id=src.id JOIN shows sh ON sh.id=src.show_id JOIN artists a ON a.id=src.artist_id",
                "a.uuid"
            ),
            CatalogReferenceKind.Year => new(
                YearId.Prefix,
                "years e JOIN artists a ON a.id=e.artist_id",
                "a.uuid"
            ),
            CatalogReferenceKind.Tour => new(
                TourId.Prefix,
                "tours e JOIN artists a ON a.id=e.artist_id",
                "a.uuid"
            ),
            CatalogReferenceKind.ImportedVenue => new(
                ImportedVenueId.Prefix,
                "venues e JOIN artists a ON a.id=e.artist_id",
                "a.uuid"
            ),
            CatalogReferenceKind.ImportedSong => new(
                ImportedSongId.Prefix,
                "setlist_songs e JOIN artists a ON a.id=e.artist_id",
                "a.uuid"
            ),
            CatalogReferenceKind.SetlistShow => new(
                SetlistShowId.Prefix,
                "setlist_shows e JOIN artists a ON a.id=e.artist_id",
                "a.uuid"
            ),
            CatalogReferenceKind.SourceReview => new(
                SourceReviewId.Prefix,
                "source_reviews e JOIN sources src ON src.id=e.source_id JOIN artists a ON a.id=src.artist_id",
                "a.uuid"
            ),
            CatalogReferenceKind.Venue => new(
                Relisten.Normalization.VenueId.Prefix,
                "norm.venues e",
                "NULL::uuid"
            ),
            CatalogReferenceKind.Song => new(
                Relisten.Normalization.SongId.Prefix,
                "norm.songs e JOIN artists a ON a.uuid=e.artist_uuid LEFT JOIN features f ON f.artist_id=a.id",
                "a.uuid"
            ),
            CatalogReferenceKind.ArtistVenue => new(
                Relisten.Normalization.ArtistVenueId.Prefix,
                "norm.artist_venues e JOIN artists a ON a.uuid=e.artist_uuid LEFT JOIN features f ON f.artist_id=a.id",
                "a.uuid"
            ),
            _ => throw new InvalidOperationException("Unsupported validated catalog kind."),
        };

    private sealed record EntitySpec(string Prefix, string From, string Owner);

    private sealed class LookupRow
    {
        public CatalogReferenceKind Kind { get; set; }
        public int request_index { get; set; }
        public string? public_id { get; set; }
        public Guid? uuid { get; set; }
        public Guid? artist_uuid { get; set; }
        public bool approved { get; set; }
        public string? venue_id { get; set; }
    }

    private sealed class CanonicalRow
    {
        public string requested_id { get; set; } = null!;
        public string id { get; set; } = null!;
    }

    private sealed class ArtistVenuePair
    {
        public string id { get; set; } = null!;
        public Guid artist_uuid { get; set; }
        public string venue_id { get; set; } = null!;
    }
}
