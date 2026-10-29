using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Relisten.Normalization.Venues;

namespace Relisten.Normalization;

public sealed record VenueRecord(
    VenueId Id,
    string Name,
    string Kind = VenueKinds.Site,
    VenueId? ParentId = null,
    string? City = null,
    string? Region = null,
    string? Country = null,
    double? Latitude = null,
    double? Longitude = null,
    string? CoordinatePrecision = null,
    float? Confidence = null
);

/// A venue name: official (with dates, end exclusive), short ("MSG") or a spelling seen in
/// sources.
public sealed record VenueNameRecord(
    VenueId VenueId,
    string Name,
    string Kind = VenueNameKinds.Spelling,
    DateOnly? ValidFrom = null,
    DateOnly? ValidTo = null,
    float? Confidence = null
);

public sealed record VenueExternalIdRecord(
    string Provider,
    string Key,
    VenueId VenueId,
    float? Confidence = null
);

public sealed record VenueEvidenceRecord(
    Guid LegacyVenueUuid,
    VenueId VenueId,
    float? Confidence = null
);

/// A show's venue: derived from its legacy row's mapping (basis evidence) or a ruling.
public sealed record ShowVenueRecord(
    Guid ShowUuid,
    VenueId VenueId,
    float? Confidence = null,
    string Basis = ShowVenueBasis.Ruling
);

public static class ShowVenueBasis
{
    public const string Evidence = "evidence";
    public const string Ruling = "ruling";
}

public static class VenueKinds
{
    public const string Site = "site";
    public const string Room = "room";
}

public static class VenueNameKinds
{
    public const string Official = "official";
    public const string Short = "short";
    public const string Spelling = "spelling";
}

/// The venue writes (string-theory ddl/002_norm.sql, venues). Same rules as songs: a write
/// changes a row only when a value differs, a machine skips people's rows, and fill-only
/// never changes an existing row.
public sealed partial class NormStore
{
    /// Venues, with a slug unique across all artists: the name, then the city when another
    /// venue has the name ("fox-theater-oakland").
    public Task<int> WriteVenuesAsync(
        IReadOnlyList<VenueRecord> venues,
        NormWriteMode mode = NormWriteMode.Upsert
    ) =>
        InsertVenuesAsync(
            venues,
            mode == NormWriteMode.FillOnly
                ? "ON CONFLICT (id) DO NOTHING"
                : $"""
                    ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name, kind = EXCLUDED.kind, parent_id = EXCLUDED.parent_id,
                        city = EXCLUDED.city, region = EXCLUDED.region, country = EXCLUDED.country,
                        latitude = EXCLUDED.latitude, longitude = EXCLUDED.longitude,
                        coordinate_precision = EXCLUDED.coordinate_precision, author_type = EXCLUDED.author_type,
                        author_ref = EXCLUDED.author_ref, confidence = EXCLUDED.confidence,
                        run_uuid = EXCLUDED.run_uuid, updated_at = now()
                    WHERE {UpdateWhen(
                        mode,
                        "norm.venues",
                        "(norm.venues.name, norm.venues.kind, norm.venues.parent_id, norm.venues.city, norm.venues.region, norm.venues.country, norm.venues.latitude, norm.venues.longitude, norm.venues.coordinate_precision) IS DISTINCT FROM (EXCLUDED.name, EXCLUDED.kind, EXCLUDED.parent_id, EXCLUDED.city, EXCLUDED.region, EXCLUDED.country, EXCLUDED.latitude, EXCLUDED.longitude, EXCLUDED.coordinate_precision)"
                    )}
                    """
        );

    /// Venues this run just minted ids for (NormIds). There is no conflict clause: an id that
    /// is already taken (a collision, about once in ten million mints) fails the write and the
    /// run with it, instead of overwriting or silently skipping the venue that holds the id.
    /// The step's retry plans again and mints fresh ids.
    public Task<int> CreateVenuesAsync(IReadOnlyList<VenueRecord> venues) =>
        InsertVenuesAsync(venues, onConflict: "");

    private async Task<int> InsertVenuesAsync(IReadOnlyList<VenueRecord> venues, string onConflict)
    {
        if (venues.Count == 0)
        {
            return 0;
        }

        var slugs = await AssignVenueSlugs(venues);

        // Parents first: a room's site may be new in the same batch.
        var ordered = venues.OrderBy(v => v.ParentId == null ? 0 : 1).ToList();
        return await connection.ExecuteAsync(
            $"""
            INSERT INTO norm.venues (id, name, slug, kind, parent_id, city, region, country, latitude, longitude,
                                   coordinate_precision, author_type, author_ref, confidence, run_uuid)
            SELECT x.id, x.name, x.slug, x.kind, x.parent, x.city, x.region, x.country, x.lat, x.lng,
                   x.precision, @AuthorType, @AuthorRef, x.confidence, @Run
            FROM unnest(@ids, @names, @slugs, @kinds, @parents, @cities, @regions, @countries, @lats, @lngs,
                        @precisions, @confidences)
                 AS x(id, name, slug, kind, parent, city, region, country, lat, lng, precision, confidence)
            {onConflict}
            """,
            new
            {
                AuthorType,
                AuthorRef,
                Run,
                ids = ordered.Select(v => v.Id.Value).ToArray(),
                names = ordered.Select(v => v.Name).ToArray(),
                slugs = ordered.Select(v => slugs[v.Id]).ToArray(),
                kinds = ordered.Select(v => v.Kind).ToArray(),
                parents = ordered.Select(v => v.ParentId?.Value).ToArray(),
                cities = ordered.Select(v => v.City).ToArray(),
                regions = ordered.Select(v => v.Region).ToArray(),
                countries = ordered.Select(v => v.Country).ToArray(),
                lats = ordered.Select(v => v.Latitude).ToArray(),
                lngs = ordered.Select(v => v.Longitude).ToArray(),
                precisions = ordered.Select(v => v.CoordinatePrecision).ToArray(),
                confidences = ordered.Select(v => v.Confidence).ToArray(),
            },
            tx
        );
    }

    private async Task<Dictionary<VenueId, string>> AssignVenueSlugs(
        IReadOnlyList<VenueRecord> venues
    )
    {
        string Base(VenueRecord v) => TitleNormalizer.Slug(v.Name);
        var bases = venues.Select(Base).Distinct().ToArray();
        var mine = venues.Select(v => v.Id).ToHashSet();
        var taken = (
            await connection.QueryAsync<(VenueId Id, string Slug)>(
                "SELECT id, slug FROM norm.venues WHERE slug LIKE ANY(@patterns)",
                new { patterns = bases.Select(b => b + "%").ToArray() },
                tx
            )
        ).ToList();
        var existing = taken.Where(t => mine.Contains(t.Id)).ToDictionary(t => t.Id, t => t.Slug);
        var used = taken.Where(t => !mine.Contains(t.Id)).Select(t => t.Slug).ToHashSet();

        var slugs = new Dictionary<VenueId, string>();
        foreach (var venue in venues.OrderBy(v => v.Id))
        {
            // A venue keeps its slug once it has one.
            if (existing.TryGetValue(venue.Id, out var kept))
            {
                slugs[venue.Id] = kept;
                used.Add(kept);
                continue;
            }

            var slug = Base(venue);
            if (used.Contains(slug) && venue.City != null)
            {
                slug = $"{slug}-{TitleNormalizer.Slug(venue.City)}";
            }

            var stem = slug;
            for (var n = 2; !used.Add(slug); n++)
            {
                slug = $"{stem}-{n}";
            }

            slugs[venue.Id] = slug;
        }

        return slugs;
    }

    public async Task<int> WriteVenueNamesAsync(
        IReadOnlyList<VenueNameRecord> names,
        NormWriteMode mode = NormWriteMode.Upsert
    )
    {
        var rows = names
            .Select(n => (n, Normalized: VenueNormalizer.Key(n.Name)))
            .Where(n => n.Normalized != null)
            .DistinctBy(n => (n.n.VenueId, n.Normalized, n.n.ValidFrom))
            .ToList();
        if (rows.Count == 0)
        {
            return 0;
        }

        return await connection.ExecuteAsync(
            $"""
            INSERT INTO norm.venue_names (venue_id, normalized, name, kind, valid_from, valid_to,
                                        author_type, author_ref, confidence, run_uuid)
            SELECT x.venue, x.normalized, x.name, x.kind, coalesce(x.valid_from, '-infinity'::date), x.valid_to,
                   @AuthorType, @AuthorRef, x.confidence, @Run
            FROM unnest(@venues, @normalized, @names, @kinds, @froms, @tos, @confidences)
                 AS x(venue, normalized, name, kind, valid_from, valid_to, confidence)
            ON CONFLICT (venue_id, normalized, valid_from) DO {(mode == NormWriteMode.FillOnly ? $"""
                -- Fill-only still lets a machine's plain spelling learn that it is an official or
                -- short name, with its dates ("Spectrum" until 1994): nothing else changes.
                UPDATE SET kind = EXCLUDED.kind, valid_to = EXCLUDED.valid_to,
                    author_type = EXCLUDED.author_type, author_ref = EXCLUDED.author_ref,
                    confidence = EXCLUDED.confidence, run_uuid = EXCLUDED.run_uuid, updated_at = now()
                WHERE norm.venue_names.kind = 'spelling' AND EXCLUDED.kind <> 'spelling'
                  AND norm.venue_names.author_type NOT IN ('user', 'moderator')
                """ : $"""
                UPDATE SET name = EXCLUDED.name, kind = EXCLUDED.kind,
                    valid_to = EXCLUDED.valid_to, author_type = EXCLUDED.author_type,
                    author_ref = EXCLUDED.author_ref, confidence = EXCLUDED.confidence,
                    run_uuid = EXCLUDED.run_uuid, updated_at = now()
                WHERE {UpdateWhen(mode, "norm.venue_names", "(norm.venue_names.name, norm.venue_names.kind, norm.venue_names.valid_to) IS DISTINCT FROM (EXCLUDED.name, EXCLUDED.kind, EXCLUDED.valid_to)")}
                """)}
            """,
            new
            {
                AuthorType,
                AuthorRef,
                Run,
                venues = rows.Select(r => r.n.VenueId.Value).ToArray(),
                normalized = rows.Select(r => r.Normalized).ToArray(),
                names = rows.Select(r => r.n.Name.Trim()).ToArray(),
                kinds = rows.Select(r => r.n.Kind).ToArray(),
                froms = rows.Select(r => r.n.ValidFrom?.ToDateTime(TimeOnly.MinValue)).ToArray(),
                tos = rows.Select(r => r.n.ValidTo?.ToDateTime(TimeOnly.MinValue)).ToArray(),
                confidences = rows.Select(r => r.n.Confidence).ToArray(),
            },
            tx
        );
    }

    public async Task<int> WriteVenueExternalIdsAsync(
        IReadOnlyList<VenueExternalIdRecord> ids,
        NormWriteMode mode = NormWriteMode.Upsert
    )
    {
        var rows = ids.DistinctBy(i => (i.Provider, i.Key)).ToList();
        if (rows.Count == 0)
        {
            return 0;
        }

        return await connection.ExecuteAsync(
            $"""
            INSERT INTO norm.venue_external_ids (provider, key, venue_id, author_type, author_ref, confidence, run_uuid)
            SELECT x.provider, x.key, x.venue, @AuthorType, @AuthorRef, x.confidence, @Run
            FROM unnest(@providers, @keys, @venues, @confidences) AS x(provider, key, venue, confidence)
            ON CONFLICT (provider, key) DO {(mode == NormWriteMode.FillOnly ? "NOTHING" : $"""
                UPDATE SET venue_id = EXCLUDED.venue_id, author_type = EXCLUDED.author_type,
                    author_ref = EXCLUDED.author_ref, confidence = EXCLUDED.confidence,
                    run_uuid = EXCLUDED.run_uuid, updated_at = now()
                WHERE {UpdateWhen(mode, "norm.venue_external_ids", "norm.venue_external_ids.venue_id IS DISTINCT FROM EXCLUDED.venue_id")}
                """)}
            """,
            new
            {
                AuthorType,
                AuthorRef,
                Run,
                providers = rows.Select(r => r.Provider).ToArray(),
                keys = rows.Select(r => r.Key).ToArray(),
                venues = rows.Select(r => r.VenueId.Value).ToArray(),
                confidences = rows.Select(r => r.Confidence).ToArray(),
            },
            tx
        );
    }

    public async Task<int> WriteVenueEvidenceAsync(
        IReadOnlyList<VenueEvidenceRecord> evidence,
        NormWriteMode mode = NormWriteMode.Upsert
    )
    {
        var rows = evidence.DistinctBy(e => e.LegacyVenueUuid).ToList();
        if (rows.Count == 0)
        {
            return 0;
        }

        return await connection.ExecuteAsync(
            $"""
            INSERT INTO norm.venue_evidence (legacy_venue_uuid, venue_id, author_type, author_ref, confidence, run_uuid)
            SELECT x.legacy, x.venue, @AuthorType, @AuthorRef, x.confidence, @Run
            FROM unnest(@legacy, @venues, @confidences) AS x(legacy, venue, confidence)
            ON CONFLICT (legacy_venue_uuid) DO {(mode == NormWriteMode.FillOnly ? "NOTHING" : $"""
                UPDATE SET venue_id = EXCLUDED.venue_id, author_type = EXCLUDED.author_type,
                    author_ref = EXCLUDED.author_ref, confidence = EXCLUDED.confidence,
                    run_uuid = EXCLUDED.run_uuid, updated_at = now()
                WHERE {UpdateWhen(mode, "norm.venue_evidence", "norm.venue_evidence.venue_id IS DISTINCT FROM EXCLUDED.venue_id")}
                """)}
            """,
            new
            {
                AuthorType,
                AuthorRef,
                Run,
                legacy = rows.Select(r => r.LegacyVenueUuid).ToArray(),
                venues = rows.Select(r => r.VenueId.Value).ToArray(),
                confidences = rows.Select(r => r.Confidence).ToArray(),
            },
            tx
        );
    }

    /// Shows' venues. A machine never replaces a ruling with a derived row: callers leave
    /// ruled shows out (the people guard refuses a machine over a person's ruling anyway).
    public async Task<int> WriteShowVenuesAsync(
        IReadOnlyList<ShowVenueRecord> shows,
        NormWriteMode mode = NormWriteMode.Upsert
    )
    {
        var rows = shows.DistinctBy(s => s.ShowUuid).ToList();
        if (rows.Count == 0)
        {
            return 0;
        }

        return await connection.ExecuteAsync(
            $"""
            INSERT INTO norm.show_venues (show_uuid, venue_id, basis, author_type, author_ref, confidence, run_uuid)
            SELECT x.show, x.venue, x.basis, @AuthorType, @AuthorRef, x.confidence, @Run
            FROM unnest(@shows, @venues, @bases, @confidences) AS x(show, venue, basis, confidence)
            ON CONFLICT (show_uuid) DO {(mode == NormWriteMode.FillOnly ? "NOTHING" : $"""
                UPDATE SET venue_id = EXCLUDED.venue_id, basis = EXCLUDED.basis, author_type = EXCLUDED.author_type,
                    author_ref = EXCLUDED.author_ref, confidence = EXCLUDED.confidence,
                    run_uuid = EXCLUDED.run_uuid, updated_at = now()
                WHERE {UpdateWhen(mode, "norm.show_venues", "(norm.show_venues.venue_id, norm.show_venues.basis) IS DISTINCT FROM (EXCLUDED.venue_id, EXCLUDED.basis)")}
                """)}
            """,
            new
            {
                AuthorType,
                AuthorRef,
                Run,
                shows = rows.Select(r => r.ShowUuid).ToArray(),
                venues = rows.Select(r => r.VenueId.Value).ToArray(),
                bases = rows.Select(r => r.Basis).ToArray(),
                confidences = rows.Select(r => r.Confidence).ToArray(),
            },
            tx
        );
    }

    /// A venue's display name follows its official names: the current one (no end), else the
    /// last one of a closed venue ("Wachovia Spectrum"). Only dated names choose, or a single
    /// undated one ("Red Rock Ampitheater", the spelling that created it, becomes "Red Rocks
    /// Amphitheatre"); several undated names choose nothing. A machine leaves a person's venue
    /// alone.
    public Task<int> RefreshVenueNamesAsync() =>
        connection.ExecuteAsync(
            $"""
            UPDATE norm.venues v SET name = n.name, author_type = @AuthorType, author_ref = @AuthorRef,
                confidence = coalesce(v.confidence, n.confidence, 0.9), run_uuid = @Run, updated_at = now()
            FROM (SELECT DISTINCT ON (o.venue_id) o.venue_id, o.name, o.confidence
                  FROM norm.venue_names o
                  WHERE o.kind = 'official'
                    AND ((o.valid_from > '-infinity' OR o.valid_to IS NOT NULL)
                         OR 1 = (SELECT count(*) FROM norm.venue_names x
                                 WHERE x.venue_id = o.venue_id AND x.kind = 'official'))
                  ORDER BY o.venue_id, (o.valid_to IS NULL) DESC, o.valid_from DESC, o.valid_to DESC) n
            WHERE v.id = n.venue_id AND v.merged_into IS NULL AND v.name IS DISTINCT FROM n.name
              {(Human ? "" : "AND v.author_type NOT IN ('user', 'moderator')")}
            """,
            new
            {
                AuthorType,
                AuthorRef,
                Run,
            },
            tx
        );

    /// Removes derived show venues whose mapping is gone (a ruling stays).
    public Task<int> ClearDerivedShowVenuesAsync(IReadOnlyCollection<Guid> shows) =>
        connection.ExecuteAsync(
            "DELETE FROM norm.show_venues WHERE show_uuid = ANY(@shows) AND basis = 'evidence'",
            new { shows = shows.ToArray() },
            tx
        );

    /// Closes open venue questions about these spellings. A machine closes only machines'.
    public Task<int> CloseVenueReviewItemsAsync(
        IReadOnlyCollection<string> keys,
        string resolution
    ) =>
        connection.ExecuteAsync(
            $"""
            UPDATE norm.review_items SET resolved_at = now(), resolved_by = @AuthorRef, resolution = @resolution,
                author_type = @AuthorType, author_ref = @AuthorRef, run_uuid = @Run, updated_at = now()
            WHERE subject = 'venue' AND resolved_at IS NULL AND key = ANY(@keys)
              {(Human ? "" : "AND author_type NOT IN ('user', 'moderator')")}
            """,
            new
            {
                keys = keys.ToArray(),
                resolution,
                AuthorType,
                AuthorRef,
                Run,
            },
            tx
        );

    /// Twins a machine made: two machine venues of one kind in one town, one carrying the
    /// other's name ("Xfinity Center" created from its own rows, and the venue the LLM learned
    /// is Great Woods → Tweeter → Comcast → Xfinity). The docs' auto-merge: an exact normalised
    /// name in the same place. The venue with fewer rows points at the other; a person's venue
    /// is never merged or merged into. Returns how many merged.
    public async Task<int> MergeTwinVenuesAsync()
    {
        var venues = (
            await connection.QueryAsync<(
                VenueId Id,
                string Name,
                string Kind,
                VenueId? Parent,
                string? City,
                string? Region,
                string? Country,
                string AuthorType,
                int Rows
            )>(
                """
                SELECT v.id, v.name, v.kind, v.parent_id, v.city, v.region, v.country, v.author_type,
                       (SELECT count(*) FROM norm.venue_evidence e WHERE e.venue_id = v.id)::int
                FROM norm.venues v WHERE v.merged_into IS NULL AND v.city IS NOT NULL
                """,
                transaction: tx
            )
        ).ToList();
        var names = (
            await connection.QueryAsync<(VenueId Venue, string Normalized)>(
                "SELECT venue_id, normalized FROM norm.venue_names WHERE kind <> 'short'",
                transaction: tx
            )
        ).ToLookup(n => n.Venue, n => n.Normalized);
        // Rooms compare the sites they are in now: two sites merged make their rooms' parents one.
        var mergedInto = (
            await connection.QueryAsync<(VenueId Id, VenueId Into)>(
                "SELECT id, merged_into FROM norm.venues WHERE merged_into IS NOT NULL",
                transaction: tx
            )
        ).ToDictionary(m => m.Id, m => m.Into);
        VenueId? Site(VenueId? parent)
        {
            for (
                var depth = 0;
                parent is { } p && depth < 20 && mergedInto.TryGetValue(p, out var next);
                depth++
            )
            {
                parent = next;
            }

            return parent;
        }

        venues = venues.Select(v => v with { Parent = Site(v.Parent) }).ToList();

        var merged = new HashSet<VenueId>();
        var count = 0;
        foreach (
            var town in venues
                .Where(v => v.AuthorType is not ("user" or "moderator"))
                .GroupBy(v => (new Locality(v.City!, v.Region, v.Country).Key, v.Kind, v.Parent))
                .Where(g => g.Count() > 1)
        )
        {
            var list = town.OrderByDescending(v => v.Rows).ThenBy(v => v.Id).ToList();
            foreach (var keep in list)
            {
                if (merged.Contains(keep.Id))
                {
                    continue;
                }

                var keepNames = names[keep.Id]
                    .Append(VenueNormalizer.Key(keep.Name))
                    .OfType<string>()
                    .ToList();
                foreach (var twin in list.Where(v => v.Id != keep.Id && !merged.Contains(v.Id)))
                {
                    var twinName = VenueNormalizer.Key(twin.Name);
                    if (
                        twinName == null
                        || !keepNames.Any(k => VenueNormalizer.SameName(k, twinName))
                    )
                    {
                        continue;
                    }

                    await connection.ExecuteAsync(
                        """
                        UPDATE norm.venues SET merged_into = @into, author_type = @AuthorType, author_ref = @AuthorRef,
                            run_uuid = @Run, updated_at = now()
                        WHERE (id = @from OR merged_into = @from) AND author_type NOT IN ('user', 'moderator')
                        """,
                        new
                        {
                            from = twin.Id,
                            into = keep.Id,
                            AuthorType,
                            AuthorRef,
                            Run,
                        },
                        tx
                    );
                    merged.Add(twin.Id);
                    count++;
                }
            }
        }

        return count;
    }

    /// An expert's merge: venue `from` is the same place as `into`. Like songs: the old venue
    /// points at the survivor (as do venues merged into it before), and its names, external
    /// ids and evidence resolve to the survivor wherever they are read. Only a person merges.
    public async Task MergeVenueAsync(VenueId from, VenueId into)
    {
        if (!Human)
        {
            throw new InvalidOperationException("Only a person can merge venues");
        }

        // Both venues locked, in one order, before checking: reciprocal merges can't commit a
        // cycle.
        await connection.ExecuteAsync(
            "SELECT 1 FROM norm.venues WHERE id = ANY(@ids) ORDER BY id FOR UPDATE",
            new { ids = new[] { from.Value, into.Value } },
            tx
        );
        var survivor = await connection.QuerySingleOrDefaultAsync<(
            VenueId Id,
            VenueId? MergedInto
        )?>("SELECT id, merged_into FROM norm.venues WHERE id = @into", new { into }, tx);
        if (from == into || survivor == null || survivor.Value.MergedInto != null)
        {
            throw new InvalidOperationException(
                "A venue merges into another venue that isn't merged itself"
            );
        }

        await connection.ExecuteAsync(
            """
            UPDATE norm.venues SET merged_into = @into, author_type = @AuthorType, author_ref = @AuthorRef,
                confidence = NULL, run_uuid = @Run, updated_at = now()
            WHERE id = @from OR merged_into = @from
            """,
            new
            {
                from,
                into,
                AuthorType,
                AuthorRef,
                Run,
            },
            tx
        );
    }

    /// Forgets a machine's placements of these legacy venue rows so they are placed again:
    /// their evidence (their shows follow at the next match), and the provider id each taught
    /// its venue when no row still placed there carries it (the matcher trusts an id first,
    /// so it would put the row straight back). Spellings stay until the run is over
    /// (PruneVenueSpellingsAsync): they are how a row placed right finds its venue again.
    /// People's rows are kept. A row already forgotten is skipped, so a retry changes
    /// nothing. Returns how many were forgotten.
    public async Task<int> ForgetVenueRowsAsync(IReadOnlyCollection<Guid> rows)
    {
        var gone = (
            await connection.QueryAsync<(VenueId Venue, string Name, string? Upstream)>(
                """
                WITH gone AS (
                    DELETE FROM norm.venue_evidence e
                    WHERE e.legacy_venue_uuid = ANY(@rows) AND e.author_type NOT IN ('user', 'moderator')
                    RETURNING e.legacy_venue_uuid, e.venue_id
                )
                SELECT g.venue_id, v.name, v.upstream_identifier
                FROM gone g JOIN venues v ON v.uuid = g.legacy_venue_uuid
                """,
                new { rows = rows.ToArray() },
                tx
            )
        ).ToList();
        var venues = gone.Select(g => g.Venue.Value).Distinct().ToArray();
        if (venues.Length == 0)
        {
            return 0;
        }

        var kept = (
            await connection.QueryAsync<(VenueId Venue, string Name, string? Upstream)>(
                """
                SELECT e.venue_id, v.name, v.upstream_identifier FROM norm.venue_evidence e
                JOIN venues v ON v.uuid = e.legacy_venue_uuid
                WHERE e.venue_id = ANY(@venues)
                """,
                new { venues },
                tx
            )
        ).ToList();
        var keptIds = kept.Select(k => (k.Venue, Id: VenueState.ParseExternalId(k.Upstream)))
            .ToHashSet();
        var ids = gone.Select(g => (g.Venue, Id: VenueState.ParseExternalId(g.Upstream)))
            .Where(g => g.Id != null && !keptIds.Contains(g))
            .Distinct()
            .ToList();
        await connection.ExecuteAsync(
            """
            DELETE FROM norm.venue_external_ids i
            USING unnest(@venues, @providers, @keys) AS x(venue, provider, key)
            WHERE i.venue_id = x.venue AND i.provider = x.provider AND i.key = x.key
              AND i.author_type NOT IN ('user', 'moderator')
            """,
            new
            {
                venues = ids.Select(i => i.Venue.Value).ToArray(),
                providers = ids.Select(i => i.Id!.Value.Provider).ToArray(),
                keys = ids.Select(i => i.Id!.Value.Key).ToArray(),
            },
            tx
        );
        return gone.Count;
    }

    /// After a forget's run: the machine's plain spellings of these venues that no row placed
    /// there is written as any more (a row that moved took its spelling with it). The venue's
    /// own name and people's names stay.
    public async Task<int> PruneVenueSpellingsAsync(IReadOnlyCollection<VenueId> venues)
    {
        var spellings = (
            await connection.QueryAsync<(VenueId Venue, string Normalized, string Name)>(
                """
                SELECT n.venue_id, n.normalized, v.name FROM norm.venue_names n
                JOIN norm.venues v ON v.id = n.venue_id
                WHERE n.venue_id = ANY(@venues) AND n.kind = 'spelling'
                  AND n.author_type NOT IN ('user', 'moderator')
                """,
                new { venues = venues.Select(v => v.Value).ToArray() },
                tx
            )
        ).ToList();
        var placed = (
            await connection.QueryAsync<(VenueId Venue, string Name)>(
                """
                SELECT e.venue_id, lv.name FROM norm.venue_evidence e
                JOIN venues lv ON lv.uuid = e.legacy_venue_uuid
                WHERE e.venue_id = ANY(@venues)
                """,
                new { venues = venues.Select(v => v.Value).ToArray() },
                tx
            )
        ).Select(p => (p.Venue, Key: VenueNormalizer.Key(p.Name))).ToHashSet();
        var stale = spellings
            .Where(s =>
                !placed.Contains((s.Venue, s.Normalized))
                && VenueNormalizer.Key(s.Name) != s.Normalized
            )
            .ToList();
        return await connection.ExecuteAsync(
            """
            DELETE FROM norm.venue_names n USING unnest(@venues, @keys) AS x(venue, normalized)
            WHERE n.venue_id = x.venue AND n.normalized = x.normalized
              AND n.kind = 'spelling' AND n.author_type NOT IN ('user', 'moderator')
            """,
            new
            {
                venues = stale.Select(s => s.Venue.Value).ToArray(),
                keys = stale.Select(s => s.Normalized).ToArray(),
            },
            tx
        );
    }

    /// Retires machine venues of these that nothing points at any more (no rows, shows,
    /// rooms, merges or questions, no names or ids from people): a venue made for a row
    /// that was forgotten and placed elsewhere. Their machine names and ids go with them.
    public async Task<int> RetireEmptyVenuesAsync(IReadOnlyCollection<VenueId> venues)
    {
        var empty = (
            await connection.QueryAsync<string>(
                """
                SELECT v.id FROM norm.venues v
                WHERE v.id = ANY(@venues) AND v.author_type NOT IN ('user', 'moderator')
                  AND v.merged_into IS NULL
                  AND NOT EXISTS (SELECT 1 FROM norm.venue_evidence e WHERE e.venue_id = v.id)
                  AND NOT EXISTS (SELECT 1 FROM norm.show_venues s WHERE s.venue_id = v.id)
                  AND NOT EXISTS (SELECT 1 FROM norm.venues o WHERE o.parent_id = v.id OR o.merged_into = v.id)
                  AND NOT EXISTS (SELECT 1 FROM norm.review_items r WHERE r.venue_id = v.id)
                  AND NOT EXISTS (SELECT 1 FROM norm.venue_names n WHERE n.venue_id = v.id AND n.author_type IN ('user', 'moderator'))
                  AND NOT EXISTS (SELECT 1 FROM norm.venue_external_ids i WHERE i.venue_id = v.id AND i.author_type IN ('user', 'moderator'))
                FOR UPDATE
                """,
                new { venues = venues.Select(v => v.Value).ToArray() },
                tx
            )
        ).ToArray();
        if (empty.Length == 0)
        {
            return 0;
        }

        await connection.ExecuteAsync(
            """
            DELETE FROM norm.venue_names WHERE venue_id = ANY(@empty);
            DELETE FROM norm.venue_external_ids WHERE venue_id = ANY(@empty);
            DELETE FROM norm.venues WHERE id = ANY(@empty);
            """,
            new { empty },
            tx
        );
        return empty.Length;
    }
}
