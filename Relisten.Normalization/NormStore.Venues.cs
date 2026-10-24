using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Relisten.Normalization.Venues;

namespace Relisten.Normalization;

public sealed record VenueRecord(
    Guid Uuid,
    string Name,
    string Kind = VenueKinds.Site,
    Guid? ParentUuid = null,
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
    Guid VenueUuid,
    string Name,
    string Kind = VenueNameKinds.Spelling,
    DateOnly? ValidFrom = null,
    DateOnly? ValidTo = null,
    float? Confidence = null
);

public sealed record VenueExternalIdRecord(
    string Provider,
    string Key,
    Guid VenueUuid,
    float? Confidence = null
);

public sealed record VenueEvidenceRecord(
    Guid LegacyVenueUuid,
    Guid VenueUuid,
    float? Confidence = null
);

/// A show's venue: derived from its legacy row's mapping (basis evidence) or a ruling.
public sealed record ShowVenueRecord(
    Guid ShowUuid,
    Guid VenueUuid,
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
    public async Task<int> WriteVenuesAsync(
        IReadOnlyList<VenueRecord> venues,
        NormWriteMode mode = NormWriteMode.Upsert
    )
    {
        if (venues.Count == 0)
        {
            return 0;
        }

        var slugs = await AssignVenueSlugs(venues);
        var update =
            mode == NormWriteMode.FillOnly
                ? "NOTHING"
                : $"""
                    UPDATE SET name = EXCLUDED.name, kind = EXCLUDED.kind, parent_uuid = EXCLUDED.parent_uuid,
                        city = EXCLUDED.city, region = EXCLUDED.region, country = EXCLUDED.country,
                        latitude = EXCLUDED.latitude, longitude = EXCLUDED.longitude,
                        coordinate_precision = EXCLUDED.coordinate_precision, author_type = EXCLUDED.author_type,
                        author_ref = EXCLUDED.author_ref, confidence = EXCLUDED.confidence,
                        run_uuid = EXCLUDED.run_uuid, updated_at = now()
                    WHERE {UpdateWhen(
                        mode,
                        "norm.venues",
                        "(norm.venues.name, norm.venues.kind, norm.venues.parent_uuid, norm.venues.city, norm.venues.region, norm.venues.country, norm.venues.latitude, norm.venues.longitude, norm.venues.coordinate_precision) IS DISTINCT FROM (EXCLUDED.name, EXCLUDED.kind, EXCLUDED.parent_uuid, EXCLUDED.city, EXCLUDED.region, EXCLUDED.country, EXCLUDED.latitude, EXCLUDED.longitude, EXCLUDED.coordinate_precision)"
                    )}
                    """;

        // Parents first: a room's site may be new in the same batch.
        var ordered = venues.OrderBy(v => v.ParentUuid == null ? 0 : 1).ToList();
        return await connection.ExecuteAsync(
            $"""
            INSERT INTO norm.venues (uuid, name, slug, kind, parent_uuid, city, region, country, latitude, longitude,
                                   coordinate_precision, author_type, author_ref, confidence, run_uuid)
            SELECT x.uuid, x.name, x.slug, x.kind, x.parent, x.city, x.region, x.country, x.lat, x.lng,
                   x.precision, @AuthorType, @AuthorRef, x.confidence, @Run
            FROM unnest(@uuids, @names, @slugs, @kinds, @parents, @cities, @regions, @countries, @lats, @lngs,
                        @precisions, @confidences)
                 AS x(uuid, name, slug, kind, parent, city, region, country, lat, lng, precision, confidence)
            ON CONFLICT (uuid) DO {update}
            """,
            new
            {
                AuthorType,
                AuthorRef,
                Run,
                uuids = ordered.Select(v => v.Uuid).ToArray(),
                names = ordered.Select(v => v.Name).ToArray(),
                slugs = ordered.Select(v => slugs[v.Uuid]).ToArray(),
                kinds = ordered.Select(v => v.Kind).ToArray(),
                parents = ordered.Select(v => v.ParentUuid).ToArray(),
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

    private async Task<Dictionary<Guid, string>> AssignVenueSlugs(IReadOnlyList<VenueRecord> venues)
    {
        string Base(VenueRecord v) => TitleNormalizer.Slug(v.Name);
        var bases = venues.Select(Base).Distinct().ToArray();
        var mine = venues.Select(v => v.Uuid).ToHashSet();
        var taken = (
            await connection.QueryAsync<(Guid Uuid, string Slug)>(
                "SELECT uuid, slug FROM norm.venues WHERE slug LIKE ANY(@patterns)",
                new { patterns = bases.Select(b => b + "%").ToArray() },
                tx
            )
        ).ToList();
        var existing = taken
            .Where(t => mine.Contains(t.Uuid))
            .ToDictionary(t => t.Uuid, t => t.Slug);
        var used = taken.Where(t => !mine.Contains(t.Uuid)).Select(t => t.Slug).ToHashSet();

        var slugs = new Dictionary<Guid, string>();
        foreach (var venue in venues.OrderBy(v => v.Uuid))
        {
            // A venue keeps its slug once it has one.
            if (existing.TryGetValue(venue.Uuid, out var kept))
            {
                slugs[venue.Uuid] = kept;
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

            slugs[venue.Uuid] = slug;
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
            .DistinctBy(n => (n.n.VenueUuid, n.Normalized, n.n.ValidFrom))
            .ToList();
        if (rows.Count == 0)
        {
            return 0;
        }

        return await connection.ExecuteAsync(
            $"""
            INSERT INTO norm.venue_names (venue_uuid, normalized, name, kind, valid_from, valid_to,
                                        author_type, author_ref, confidence, run_uuid)
            SELECT x.venue, x.normalized, x.name, x.kind, coalesce(x.valid_from, '-infinity'::date), x.valid_to,
                   @AuthorType, @AuthorRef, x.confidence, @Run
            FROM unnest(@venues, @normalized, @names, @kinds, @froms, @tos, @confidences)
                 AS x(venue, normalized, name, kind, valid_from, valid_to, confidence)
            ON CONFLICT (venue_uuid, normalized, valid_from) DO {(mode == NormWriteMode.FillOnly ? $"""
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
                venues = rows.Select(r => r.n.VenueUuid).ToArray(),
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
            INSERT INTO norm.venue_external_ids (provider, key, venue_uuid, author_type, author_ref, confidence, run_uuid)
            SELECT x.provider, x.key, x.venue, @AuthorType, @AuthorRef, x.confidence, @Run
            FROM unnest(@providers, @keys, @venues, @confidences) AS x(provider, key, venue, confidence)
            ON CONFLICT (provider, key) DO {(mode == NormWriteMode.FillOnly ? "NOTHING" : $"""
                UPDATE SET venue_uuid = EXCLUDED.venue_uuid, author_type = EXCLUDED.author_type,
                    author_ref = EXCLUDED.author_ref, confidence = EXCLUDED.confidence,
                    run_uuid = EXCLUDED.run_uuid, updated_at = now()
                WHERE {UpdateWhen(mode, "norm.venue_external_ids", "norm.venue_external_ids.venue_uuid IS DISTINCT FROM EXCLUDED.venue_uuid")}
                """)}
            """,
            new
            {
                AuthorType,
                AuthorRef,
                Run,
                providers = rows.Select(r => r.Provider).ToArray(),
                keys = rows.Select(r => r.Key).ToArray(),
                venues = rows.Select(r => r.VenueUuid).ToArray(),
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
            INSERT INTO norm.venue_evidence (legacy_venue_uuid, venue_uuid, author_type, author_ref, confidence, run_uuid)
            SELECT x.legacy, x.venue, @AuthorType, @AuthorRef, x.confidence, @Run
            FROM unnest(@legacy, @venues, @confidences) AS x(legacy, venue, confidence)
            ON CONFLICT (legacy_venue_uuid) DO {(mode == NormWriteMode.FillOnly ? "NOTHING" : $"""
                UPDATE SET venue_uuid = EXCLUDED.venue_uuid, author_type = EXCLUDED.author_type,
                    author_ref = EXCLUDED.author_ref, confidence = EXCLUDED.confidence,
                    run_uuid = EXCLUDED.run_uuid, updated_at = now()
                WHERE {UpdateWhen(mode, "norm.venue_evidence", "norm.venue_evidence.venue_uuid IS DISTINCT FROM EXCLUDED.venue_uuid")}
                """)}
            """,
            new
            {
                AuthorType,
                AuthorRef,
                Run,
                legacy = rows.Select(r => r.LegacyVenueUuid).ToArray(),
                venues = rows.Select(r => r.VenueUuid).ToArray(),
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
            INSERT INTO norm.show_venues (show_uuid, venue_uuid, basis, author_type, author_ref, confidence, run_uuid)
            SELECT x.show, x.venue, x.basis, @AuthorType, @AuthorRef, x.confidence, @Run
            FROM unnest(@shows, @venues, @bases, @confidences) AS x(show, venue, basis, confidence)
            ON CONFLICT (show_uuid) DO {(mode == NormWriteMode.FillOnly ? "NOTHING" : $"""
                UPDATE SET venue_uuid = EXCLUDED.venue_uuid, basis = EXCLUDED.basis, author_type = EXCLUDED.author_type,
                    author_ref = EXCLUDED.author_ref, confidence = EXCLUDED.confidence,
                    run_uuid = EXCLUDED.run_uuid, updated_at = now()
                WHERE {UpdateWhen(mode, "norm.show_venues", "(norm.show_venues.venue_uuid, norm.show_venues.basis) IS DISTINCT FROM (EXCLUDED.venue_uuid, EXCLUDED.basis)")}
                """)}
            """,
            new
            {
                AuthorType,
                AuthorRef,
                Run,
                shows = rows.Select(r => r.ShowUuid).ToArray(),
                venues = rows.Select(r => r.VenueUuid).ToArray(),
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
            FROM (SELECT DISTINCT ON (o.venue_uuid) o.venue_uuid, o.name, o.confidence
                  FROM norm.venue_names o
                  WHERE o.kind = 'official'
                    AND ((o.valid_from > '-infinity' OR o.valid_to IS NOT NULL)
                         OR 1 = (SELECT count(*) FROM norm.venue_names x
                                 WHERE x.venue_uuid = o.venue_uuid AND x.kind = 'official'))
                  ORDER BY o.venue_uuid, (o.valid_to IS NULL) DESC, o.valid_from DESC, o.valid_to DESC) n
            WHERE v.uuid = n.venue_uuid AND v.merged_into IS NULL AND v.name IS DISTINCT FROM n.name
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
                Guid Uuid,
                string Name,
                string Kind,
                Guid? Parent,
                string? City,
                string? Region,
                string? Country,
                string AuthorType,
                int Rows
            )>(
                """
                SELECT v.uuid, v.name, v.kind, v.parent_uuid, v.city, v.region, v.country, v.author_type,
                       (SELECT count(*) FROM norm.venue_evidence e WHERE e.venue_uuid = v.uuid)::int
                FROM norm.venues v WHERE v.merged_into IS NULL AND v.city IS NOT NULL
                """,
                transaction: tx
            )
        ).ToList();
        var names = (
            await connection.QueryAsync<(Guid Venue, string Normalized)>(
                "SELECT venue_uuid, normalized FROM norm.venue_names WHERE kind <> 'short'",
                transaction: tx
            )
        ).ToLookup(n => n.Venue, n => n.Normalized);
        // Rooms compare the sites they are in now: two sites merged make their rooms' parents one.
        var mergedInto = (
            await connection.QueryAsync<(Guid Uuid, Guid Into)>(
                "SELECT uuid, merged_into FROM norm.venues WHERE merged_into IS NOT NULL",
                transaction: tx
            )
        ).ToDictionary(m => m.Uuid, m => m.Into);
        Guid? Site(Guid? parent)
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

        var merged = new HashSet<Guid>();
        var count = 0;
        foreach (
            var town in venues
                .Where(v => v.AuthorType is not ("user" or "moderator"))
                .GroupBy(v => (new Locality(v.City!, v.Region, v.Country).Key, v.Kind, v.Parent))
                .Where(g => g.Count() > 1)
        )
        {
            var list = town.OrderByDescending(v => v.Rows).ThenBy(v => v.Uuid).ToList();
            foreach (var keep in list)
            {
                if (merged.Contains(keep.Uuid))
                {
                    continue;
                }

                var keepNames = names[keep.Uuid]
                    .Append(VenueNormalizer.Key(keep.Name))
                    .OfType<string>()
                    .ToList();
                foreach (
                    var twin in list.Where(v => v.Uuid != keep.Uuid && !merged.Contains(v.Uuid))
                )
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
                        WHERE (uuid = @from OR merged_into = @from) AND author_type NOT IN ('user', 'moderator')
                        """,
                        new
                        {
                            from = twin.Uuid,
                            into = keep.Uuid,
                            AuthorType,
                            AuthorRef,
                            Run,
                        },
                        tx
                    );
                    merged.Add(twin.Uuid);
                    count++;
                }
            }
        }

        return count;
    }

    /// An expert's merge: venue `from` is the same place as `into`. Like songs: the old venue
    /// points at the survivor (as do venues merged into it before), and its names, external
    /// ids and evidence resolve to the survivor wherever they are read. Only a person merges.
    public async Task MergeVenueAsync(Guid from, Guid into)
    {
        if (!Human)
        {
            throw new InvalidOperationException("Only a person can merge venues");
        }

        var survivor = await connection.QuerySingleOrDefaultAsync<(Guid Uuid, Guid? MergedInto)?>(
            "SELECT uuid, merged_into FROM norm.venues WHERE uuid = @into",
            new { into },
            tx
        );
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
            WHERE uuid = @from OR merged_into = @from
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
}
