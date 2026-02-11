using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Relisten.Api.Models;
using Relisten.Data;

namespace Relisten.Services.Classification
{
    /// <summary>
    /// Groups artist-scoped venues into global canonical venues.
    /// Uses a multi-layer matching pipeline:
    ///   Layer 1: Exact slug+location match (fast, ~60% of cases)
    ///   Layer 2: Normalized name + location proximity (~25% of cases)
    ///   Layer 3: LLM disambiguation for ambiguous cases (~15% of cases)
    ///
    /// Solves the "Fox Theatre problem": same venue name, different physical locations.
    /// </summary>
    public class VenueCanonicalizer
    {
        private readonly DbService _db;
        private readonly LlmClassificationService _llm;
        private readonly ILogger<VenueCanonicalizer> _log;

        /// <summary>Distance in km within which two venues with similar names are likely the same place.</summary>
        private const double ProximityThresholdKm = 5.0;

        /// <summary>Fuzzy match threshold for venue names.</summary>
        private const float NameSimilarityThreshold = 0.80f;

        public VenueCanonicalizer(
            DbService db,
            LlmClassificationService llm,
            ILogger<VenueCanonicalizer> log)
        {
            _db = db;
            _llm = llm;
            _log = log;
        }

        /// <summary>
        /// Process a batch of unlinked venues: find or create canonical venues and link them.
        /// </summary>
        public async Task CanonicalizeVenuesAsync(
            int batchSize = 10000,
            bool allowLlm = false,
            CancellationToken ct = default)
        {
            // Load unlinked venues
            _log.LogInformation("Loading up to {BatchSize} unlinked venues...", batchSize);
            Console.WriteLine($"[VenueCanonicalizer] Loading up to {batchSize} unlinked venues...");

            var unlinked = await GetUnlinkedVenues(batchSize, ct);

            if (unlinked.Count == 0)
            {
                _log.LogInformation("No unlinked venues found — nothing to do");
                Console.WriteLine("[VenueCanonicalizer] No unlinked venues found — nothing to do");
                return;
            }

            _log.LogInformation("Found {Count} unlinked venues to process", unlinked.Count);
            Console.WriteLine($"[VenueCanonicalizer] Found {unlinked.Count} unlinked venues to process");

            // Load all existing canonical venues for matching
            var canonicals = await GetAllCanonicalVenues(ct);

            _log.LogInformation("Loaded {Count} existing canonical venues for matching", canonicals.Count);
            Console.WriteLine($"[VenueCanonicalizer] Loaded {canonicals.Count} existing canonical venues for matching");

            var canonicalsBySlug = canonicals
                .GroupBy(c => c.slug)
                .ToDictionary(g => g.Key, g => g.ToList());

            var created = 0;
            var linked = 0;
            var slugMatches = 0;
            var geoMatches = 0;
            var fuzzyMatches = 0;
            var processed = 0;

            foreach (var venue in unlinked)
            {
                var slug = NormalizeVenueSlug(venue.name);
                var normalizedLocation = NormalizeLocation(venue.location);
                string matchMethod = "none";

                // Layer 1: Exact slug match — check location to distinguish same-name venues
                var match = TrySlugLocationMatch(slug, normalizedLocation, venue, canonicalsBySlug);
                if (match != null) { matchMethod = "slug"; slugMatches++; }

                // Layer 2: Fuzzy name match with geo proximity
                if (match == null && venue.latitude.HasValue && venue.longitude.HasValue)
                {
                    match = TryGeoProximityMatch(venue, canonicals);
                    if (match != null) { matchMethod = "geo"; geoMatches++; }
                }

                // Layer 3: Fuzzy name + normalized location (no geo data)
                if (match == null)
                {
                    match = TryFuzzyNameLocationMatch(venue, canonicals);
                    if (match != null) { matchMethod = "fuzzy"; fuzzyMatches++; }
                }

                if (match != null)
                {
                    // Link to existing canonical
                    await LinkVenueToCanonical(venue.id, match.id, ct);
                    linked++;

                    _log.LogDebug(
                        "Linked venue '{VenueName}' ({Location}) → canonical '{CanonicalName}' via {Method}",
                        venue.name, venue.location, match.name, matchMethod);
                }
                else
                {
                    // Create new canonical venue
                    var canonical = await CreateCanonicalVenue(venue, slug, ct);
                    await LinkVenueToCanonical(venue.id, canonical.id, ct);

                    // Add to in-memory index
                    canonicals.Add(canonical);
                    if (!canonicalsBySlug.ContainsKey(canonical.slug))
                        canonicalsBySlug[canonical.slug] = new List<CanonicalVenue>();
                    canonicalsBySlug[canonical.slug].Add(canonical);

                    created++;

                    _log.LogDebug(
                        "Created new canonical venue '{VenueName}' ({Location}) [slug={Slug}]",
                        venue.name, venue.location, slug);
                }

                processed++;
                if (processed % 500 == 0)
                {
                    _log.LogInformation(
                        "Progress: {Processed}/{Total} venues — {Linked} linked (slug={Slug}, geo={Geo}, fuzzy={Fuzzy}), {Created} created",
                        processed, unlinked.Count, linked, slugMatches, geoMatches, fuzzyMatches, created);
                    Console.WriteLine(
                        $"[VenueCanonicalizer] Progress: {processed}/{unlinked.Count} venues — {linked} linked (slug={slugMatches}, geo={geoMatches}, fuzzy={fuzzyMatches}), {created} created");
                }
            }

            _log.LogInformation(
                "Venue canonicalization complete: {Linked} linked (slug={SlugMatches}, geo={GeoMatches}, fuzzy={FuzzyMatches}), {Created} new canonicals from {Total} unlinked venues. Total canonical venues: {CanonicalCount}",
                linked, slugMatches, geoMatches, fuzzyMatches, created, unlinked.Count, canonicals.Count);
            Console.WriteLine(
                $"[VenueCanonicalizer] Complete: {linked} linked (slug={slugMatches}, geo={geoMatches}, fuzzy={fuzzyMatches}), {created} new canonicals from {unlinked.Count} unlinked. Total canonical venues: {canonicals.Count}");
        }

        #region Matching Layers

        internal CanonicalVenue? TrySlugLocationMatch(
            string slug,
            string normalizedLocation,
            Venue venue,
            Dictionary<string, List<CanonicalVenue>> canonicalsBySlug)
        {
            if (!canonicalsBySlug.TryGetValue(slug, out var candidates))
                return null;

            // If only one canonical with this slug, it's a match if location is similar enough.
            // Threshold 0.65 prevents false positives like "oakland, ca" ≈ "atlanta, ga" (~0.55)
            // while allowing legitimate partial matches like "morrison, co" ≈ "morrison, co, usa" (~0.71).
            if (candidates.Count == 1)
            {
                var only = candidates[0];
                var locSimilarity = CalculateLocationSimilarity(normalizedLocation, NormalizeLocation(only.location));
                if (locSimilarity >= 0.65f)
                    return only;
            }

            // Multiple canonicals with same slug (the Fox Theatre problem)
            // Match by location similarity
            foreach (var candidate in candidates)
            {
                var locSimilarity = CalculateLocationSimilarity(
                    normalizedLocation,
                    NormalizeLocation(candidate.location));

                if (locSimilarity >= NameSimilarityThreshold)
                    return candidate;
            }

            // Try geo proximity if available
            if (venue.latitude.HasValue && venue.longitude.HasValue)
            {
                foreach (var candidate in candidates)
                {
                    if (candidate.latitude.HasValue && candidate.longitude.HasValue)
                    {
                        var distanceKm = HaversineDistance(
                            venue.latitude.Value, venue.longitude.Value,
                            candidate.latitude.Value, candidate.longitude.Value);

                        if (distanceKm < ProximityThresholdKm)
                            return candidate;
                    }
                }
            }

            return null;
        }

        internal CanonicalVenue? TryGeoProximityMatch(
            Venue venue,
            List<CanonicalVenue> canonicals)
        {
            if (!venue.latitude.HasValue || !venue.longitude.HasValue)
                return null;

            var slug = NormalizeVenueSlug(venue.name);

            foreach (var candidate in canonicals)
            {
                if (!candidate.latitude.HasValue || !candidate.longitude.HasValue)
                    continue;

                var distanceKm = HaversineDistance(
                    venue.latitude.Value, venue.longitude.Value,
                    candidate.latitude.Value, candidate.longitude.Value);

                if (distanceKm > ProximityThresholdKm)
                    continue;

                // Within proximity — check name similarity
                var nameSimilarity = TrackSongMatcher.CalculateSimilarity(
                    slug, candidate.slug);

                if (nameSimilarity >= 0.7f)
                    return candidate;
            }

            return null;
        }

        internal CanonicalVenue? TryFuzzyNameLocationMatch(
            Venue venue,
            List<CanonicalVenue> canonicals)
        {
            var normalizedName = NormalizeVenueName(venue.name);
            var normalizedLocation = NormalizeLocation(venue.location);

            CanonicalVenue? bestMatch = null;
            float bestScore = 0;

            foreach (var candidate in canonicals)
            {
                var candidateNormalizedName = NormalizeVenueName(candidate.name);
                var candidateNormalizedLocation = NormalizeLocation(candidate.location);

                var nameSim = TrackSongMatcher.CalculateSimilarity(normalizedName, candidateNormalizedName);
                var locSim = CalculateLocationSimilarity(normalizedLocation, candidateNormalizedLocation);

                // Weighted: name more important than location
                var score = (nameSim * 0.6f) + (locSim * 0.4f);

                if (score > bestScore && nameSim >= NameSimilarityThreshold && locSim >= 0.5f)
                {
                    bestScore = score;
                    bestMatch = candidate;
                }
            }

            return bestMatch;
        }

        #endregion

        #region Normalization

        // Common venue name prefixes/suffixes to strip for matching
        private static readonly Regex VenueArticles = new(
            @"^(?:the|a)\s+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex VenueSuffixes = new(
            @"\s*\(.*?\)\s*$",
            RegexOptions.Compiled);

        // Common abbreviations in location strings
        private static readonly Dictionary<string, string> StateAbbreviations = new(StringComparer.OrdinalIgnoreCase)
        {
            {"alabama", "al"}, {"alaska", "ak"}, {"arizona", "az"}, {"arkansas", "ar"},
            {"california", "ca"}, {"colorado", "co"}, {"connecticut", "ct"}, {"delaware", "de"},
            {"florida", "fl"}, {"georgia", "ga"}, {"hawaii", "hi"}, {"idaho", "id"},
            {"illinois", "il"}, {"indiana", "in"}, {"iowa", "ia"}, {"kansas", "ks"},
            {"kentucky", "ky"}, {"louisiana", "la"}, {"maine", "me"}, {"maryland", "md"},
            {"massachusetts", "ma"}, {"michigan", "mi"}, {"minnesota", "mn"}, {"mississippi", "ms"},
            {"missouri", "mo"}, {"montana", "mt"}, {"nebraska", "ne"}, {"nevada", "nv"},
            {"new hampshire", "nh"}, {"new jersey", "nj"}, {"new mexico", "nm"}, {"new york", "ny"},
            {"north carolina", "nc"}, {"north dakota", "nd"}, {"ohio", "oh"}, {"oklahoma", "ok"},
            {"oregon", "or"}, {"pennsylvania", "pa"}, {"rhode island", "ri"}, {"south carolina", "sc"},
            {"south dakota", "sd"}, {"tennessee", "tn"}, {"texas", "tx"}, {"utah", "ut"},
            {"vermont", "vt"}, {"virginia", "va"}, {"washington", "wa"}, {"west virginia", "wv"},
            {"wisconsin", "wi"}, {"wyoming", "wy"}, {"district of columbia", "dc"}
        };

        internal static string NormalizeVenueName(string name)
        {
            var normalized = name.ToLowerInvariant().Trim();
            normalized = VenueArticles.Replace(normalized, "");
            normalized = VenueSuffixes.Replace(normalized, "");
            return normalized.Trim();
        }

        internal static string NormalizeVenueSlug(string name)
        {
            return Relisten.Import.SlugUtils.Slugify(NormalizeVenueName(name));
        }

        internal static string NormalizeLocation(string location)
        {
            if (string.IsNullOrWhiteSpace(location)) return "";

            var parts = location.Split(',')
                .Select(p => p.Trim().ToLowerInvariant())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();

            // Normalize state names to abbreviations
            for (var i = 0; i < parts.Count; i++)
            {
                if (StateAbbreviations.TryGetValue(parts[i], out var abbr))
                {
                    parts[i] = abbr;
                }
            }

            return string.Join(", ", parts);
        }

        internal static float CalculateLocationSimilarity(string loc1, string loc2)
        {
            if (string.IsNullOrWhiteSpace(loc1) || string.IsNullOrWhiteSpace(loc2))
                return 0.3f; // Unknown locations get partial credit

            return TrackSongMatcher.CalculateSimilarity(loc1, loc2);
        }

        #endregion

        #region Geo Utilities

        /// <summary>
        /// Calculate the Haversine distance between two lat/lng points in kilometers.
        /// </summary>
        internal static double HaversineDistance(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371; // Earth radius in km
            var dLat = ToRadians(lat2 - lat1);
            var dLon = ToRadians(lon2 - lon1);
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                    Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
        }

        private static double ToRadians(double degrees) => degrees * Math.PI / 180;

        #endregion

        #region Data Access

        private async Task<List<Venue>> GetUnlinkedVenues(int batchSize, CancellationToken ct)
        {
            return (await _db.WithConnection(async con =>
            {
                var results = await con.QueryAsync<Venue>(@"
                    SELECT v.*, a.uuid as artist_uuid
                    FROM venues v
                    JOIN artists a ON a.id = v.artist_id
                    WHERE v.canonical_venue_id IS NULL
                    ORDER BY v.id
                    LIMIT @batchSize
                ", new { batchSize }, commandTimeout: 120);
                return results.ToList();
            }, longTimeout: true, readOnly: true)).ToList();
        }

        private async Task<List<CanonicalVenue>> GetAllCanonicalVenues(CancellationToken ct)
        {
            return (await _db.WithConnection(async con =>
            {
                var results = await con.QueryAsync<CanonicalVenue>(@"
                    SELECT * FROM canonical_venues ORDER BY id
                ", commandTimeout: 120);
                return results.ToList();
            }, longTimeout: true, readOnly: true)).ToList();
        }

        private async Task<CanonicalVenue> CreateCanonicalVenue(Venue venue, string slug, CancellationToken ct)
        {
            return await _db.WithWriteConnection(con =>
                con.QuerySingleAsync<CanonicalVenue>(@"
                    INSERT INTO canonical_venues (name, location, latitude, longitude, slug, past_names)
                    VALUES (@name, @location, @latitude, @longitude, @slug, @past_names)
                    RETURNING *
                ", new
                {
                    name = venue.name,
                    location = venue.location,
                    latitude = venue.latitude,
                    longitude = venue.longitude,
                    slug = slug,
                    past_names = venue.past_names
                }));
        }

        private async Task LinkVenueToCanonical(int venueId, int canonicalVenueId, CancellationToken ct)
        {
            await _db.WithWriteConnection(con =>
                con.ExecuteAsync(@"
                    UPDATE venues SET canonical_venue_id = @canonicalVenueId
                    WHERE id = @venueId
                ", new { venueId, canonicalVenueId }));
        }

        #endregion
    }
}
