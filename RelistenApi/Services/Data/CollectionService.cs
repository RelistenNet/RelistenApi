using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;
using Relisten.Import;
using Relisten.Services.Collections;

namespace Relisten.Data;

public class CollectionService : RelistenDataServiceBase, IArchiveCollectionResolverRepository
{
    private readonly ArtistService artistService;
    private readonly UpstreamSourceService upstreamSourceService;

    public CollectionService(DbService db, ArtistService artistService, UpstreamSourceService upstreamSourceService) :
        base(db)
    {
        this.artistService = artistService;
        this.upstreamSourceService = upstreamSourceService;
    }

    public async Task<ArchiveCollection> EnsureAadamJacobsCollection()
    {
        var upstreamSource = await upstreamSourceService.FindUpstreamSourceByName(ArchiveOrgImporter.DataSourceName);
        if (upstreamSource == null)
        {
            throw new InvalidOperationException("archive.org upstream source was not found.");
        }

        return await db.WithWriteConnection(con => con.QuerySingleAsync<ArchiveCollection>(@"
            INSERT INTO collections
                (
                    uuid,
                    slug,
                    upstream_source_id,
                    upstream_identifier,
                    collection_type,
                    name,
                    description
                )
            VALUES
                (
                    md5('root::collection::aadamjacobs')::uuid,
                    'aadam-jacobs',
                    @upstreamSourceId,
                    'aadamjacobs',
                    'taper_archive',
                    'Aadam Jacobs Collection',
                    'A curated Archive.org collection imported into playable Relisten sources.'
                )
            ON CONFLICT (slug) DO UPDATE SET
                upstream_source_id = EXCLUDED.upstream_source_id,
                upstream_identifier = EXCLUDED.upstream_identifier,
                collection_type = EXCLUDED.collection_type,
                name = EXCLUDED.name,
                description = EXCLUDED.description,
                updated_at = timezone('utc'::text, now())
            RETURNING *
        ", new {upstreamSourceId = upstreamSource.id}));
    }

    public Task<CollectionArtistMapping?> FindMapping(Guid collectionUuid, string creatorName)
    {
        return db.WithConnection(con => con.QuerySingleOrDefaultAsync<CollectionArtistMapping>(@"
            SELECT *
            FROM collection_artist_mappings
            WHERE collection_uuid = @collectionUuid
              AND creator_name = @creatorName
        ", new {collectionUuid, creatorName}));
    }

    public Task<Artist?> FindArtistByUuid(Guid artistUuid)
    {
        return artistService.FindArtistByUuid(artistUuid);
    }

    public async Task<IReadOnlyList<Artist>> FindArtistsBySourceIdentifier(string upstreamIdentifier)
    {
        var artists = await QueryArtists(@"
            JOIN sources s ON s.artist_id = a.id
            WHERE s.upstream_identifier = @upstreamIdentifier
        ", new {upstreamIdentifier});

        return artists.ToList();
    }

    public async Task<Artist?> FindArtistByExactName(string name)
    {
        var artists = await QueryArtists(@"
            WHERE a.name = @name
            LIMIT 1
        ", new {name});

        return artists.FirstOrDefault();
    }

    public async Task<Artist?> FindArtistByNormalizedName(string normalizedName)
    {
        var artists = await QueryArtists(@"
            WHERE regexp_replace(lower(a.name), '[^a-z0-9]+', '', 'g') = @normalizedName
            ORDER BY a.featured DESC, a.name
            LIMIT 1
        ", new {normalizedName});

        return artists.FirstOrDefault();
    }

    public async Task<IReadOnlyList<string>> LoadArtistSlugs()
    {
        var slugs = await db.WithConnection(con => con.QueryAsync<string>(@"
            SELECT slug
            FROM artists
        "));

        return slugs.ToList();
    }

    public Task<SlimArtistWithFeatures> SaveArtist(SlimArtistWithFeatures artist)
    {
        return artistService.Save(artist);
    }

    public Task SaveMapping(CollectionArtistMapping mapping)
    {
        return db.WithWriteConnection(con => con.ExecuteAsync(@"
            INSERT INTO collection_artist_mappings
                (
                    collection_uuid,
                    creator_name,
                    artist_uuid,
                    canonical_name,
                    blocked,
                    block_reason,
                    decision_source,
                    updated_at
                )
            VALUES
                (
                    @collection_uuid,
                    @creator_name,
                    @artist_uuid,
                    @canonical_name,
                    @blocked,
                    @block_reason,
                    @decision_source,
                    timezone('utc'::text, now())
                )
            ON CONFLICT (collection_uuid, creator_name) DO UPDATE SET
                artist_uuid = EXCLUDED.artist_uuid,
                canonical_name = EXCLUDED.canonical_name,
                blocked = EXCLUDED.blocked,
                block_reason = EXCLUDED.block_reason,
                decision_source = EXCLUDED.decision_source,
                updated_at = EXCLUDED.updated_at
        ", mapping));
    }

    public Task TouchApiUpdatedAt(int artistId)
    {
        return artistService.TouchApiUpdatedAt(artistId);
    }

    private async Task<IEnumerable<Artist>> QueryArtists(string fromAndWhere, object parameters)
    {
        var artists = await db.WithConnection(con => con.QueryAsync<Artist, Features, Artist>($@"
            SELECT DISTINCT
                a.*, f.*
            FROM artists a
            LEFT JOIN features f ON f.artist_id = a.id
            {fromAndWhere}
        ", (artist, features) =>
        {
            artist.features = features;
            artist.upstream_sources = Array.Empty<ArtistUpstreamSource>();
            return artist;
        }, parameters));

        return artists;
    }
}
