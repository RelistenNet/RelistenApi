using System;
using System.Collections.Generic;
using System.Linq;

namespace Relisten.Search;

internal static class CatalogSearchRanking
{
    internal const int VerifiedIdentityPriority = 2;

    public static RankedCandidate ReviewedSong(RankedCandidate candidate, bool contextual)
    {
        var tier = contextual ? MatchTier.Exact : MatchTier.Prefix;
        if (candidate.Match.Tier >= tier)
        {
            return candidate;
        }

        // A confirmed nickname ranks as a title match when the artist supports
        // it. Unqualified nicknames can surface a song without displacing a
        // listener who typed another item's full name.
        return candidate with
        {
            Match = new(tier, ["song_nickname"], contextual ? 1 : .9),
            ExactPriority = contextual
                ? (candidate.Row.Kind == "song" ? VerifiedIdentityPriority : 1)
                : 0,
            Confidence = contextual ? .75 : 0,
        };
    }

    public static RankedCandidate Candidate(
        SearchDocument document,
        SearchInput input,
        SearchQuery query,
        CatalogQueryEvidence evidence,
        IReadOnlySet<string> venues,
        string? readingId
    )
    {
        var isEvidence = document.Kind is "recording" or "review";
        var row = document.ReviewExcerpt is { } excerpt
            ? document with
            {
                Highlights = new Dictionary<string, string> { ["review_text"] = excerpt },
            }
            : document;
        var title = document.Kind switch
        {
            "review" => .35,
            // The index uses the venue as a show's search name. It can
            // confirm the place, but the name alone doesn't identify a date.
            "show"
                when input.Scope == SearchScope.All
                    && !input.AddTracks
                    && !query.HasDate
                    && query.Tour == null => .55,
            _ => 1,
        };
        var fields = new (string, string?, double)[]
        {
            ("title", document.Name, title),
            ("label", document.Label, title),
            ("aliases", document.Aliases, .72),
            ("artist", document.Artist, .55),
            ("place", document.Place, .55),
            ("run", document.Run, .45),
            (
                "excerpt",
                document.Highlights == null ? null : string.Join(' ', document.Highlights.Values),
                .3
            ),
        };
        var match = MatchEvidence
            .Read(input.Text, fields)
            .WithTypoCoverage(document.TextMatch, isEvidence ? .3 : 1);
        var literalTitle = match.Tier == MatchTier.Exact;
        if (
            document.Kind == "recording"
            && query.Terms.Contains(' ')
            && document.Highlights?.Values.Any(excerpt =>
                (" " + SearchText.Fold(SearchText.Clean(excerpt)) + " ").Contains(
                    " " + query.Terms + " ",
                    StringComparison.Ordinal
                )
            ) == true
        )
        {
            // Matching the whole phrase in a tape note is better evidence
            // than matching its words independently in a title and a city.
            match = match with
            {
                Relevance = Math.Max(match.Relevance, .72),
            };
        }
        var related =
            query.Artist == null
            || document.ArtistSlug == query.Artist
            || document.ArtistSlugs.Contains(query.Artist);
        var atPlace = venues.Count == 0 || venues.Contains(document.VenueId);
        var meetsDate = !query.HasDate || query.MatchesDate(document.Date);
        var verified = related && atPlace && meetsDate;
        var remainder = MatchEvidence.Read(query.Terms, fields);
        var contextual =
            query.Artist != null || query.HasDate || venues.Count > 0 || query.Tour != null;
        var placeQuery =
            atPlace && venues.Count > 0 && evidence.Vocabulary.IsKnownPlace(query.Terms);
        var exactTitle = remainder.Tier == MatchTier.Exact;
        var browse =
            verified
            && (query.Terms.Length == 0 || placeQuery)
            && contextual
            && document.Kind is ("show" or "performance");
        var resolved = verified && (exactTitle && contextual || browse);
        var curated = document.CatalogQuality >= 5 && related && input.ReadingId != "literal";
        var qualifiedPerformance = resolved && exactTitle && (query.HasDate || venues.Count > 0);
        var verifiedArtistPrefix =
            document.Kind == "artist"
            && query.VerifiedArtistPrefix
            && query.ReadingArtist == document.ArtistSlug;
        if (document.CatalogMatch is "prefix" or "spelling" && match.Tier < MatchTier.Exact)
        {
            var tier = document.CatalogMatch == "prefix" ? MatchTier.Prefix : match.Tier;
            match = new(
                tier,
                [.. match.Fields, document.CatalogMatch],
                Math.Max(match.Relevance, document.CatalogMatch == "prefix" ? .9 : .95)
            );
        }
        if (
            (
                document.SuggestedQuery != null
                || verified && query.Tour != null && document.Kind == "tour"
            ) && !isEvidence
        )
        {
            // A browse route verified against indexed shows answers a run query.
            match = new(MatchTier.Exact, [.. match.Fields, "run"], 1);
        }
        if (
            input.Scope == SearchScope.All
            && !input.AddTracks
            && document.Kind is ("show" or "song")
            && document.ArtistSlug == query.NamedArtist
        )
        {
            // The artist page and its catalog answer this reading, even when a
            // song or show title does not contain the artist's name.
            match = new(MatchTier.Prefix, ["artist"], .9);
        }
        else if (document.CatalogMatch == "place_browse")
        {
            match = new(MatchTier.Prefix, ["place"], .9);
        }
        else if (document.CatalogMatch is "run_browse" or "dated_place_browse")
        {
            var context = document.CatalogMatch == "run_browse" ? "run" : "place";
            match = new(MatchTier.Exact, [context], 1);
        }
        else if (
            (document.Kind == "song" || document.SongId.Length > 0)
            && SearchVocabulary.AlternateSongTitle(query.Terms) == document.Name
            && document.Name.Length > 0
            && verified
        )
        {
            match = new(MatchTier.Exact, ["title", "title_variant"], 1);
        }
        else if (
            document.Kind == "artist"
            && query is { Reading: "artist_nickname" }
            && query.ReadingArtist == document.ArtistSlug
        )
        {
            match = new(MatchTier.Exact, ["artist", "nickname"], 1);
        }
        else if (verifiedArtistPrefix)
        {
            match = new(MatchTier.Exact, [.. match.Fields, "artist_name_prefix"], 1);
        }
        else if (
            verified
            && placeQuery
            && (query.HasDate || query.Artist != null)
            && document.Kind == "performance"
            && !exactTitle
        )
        {
            // Select a version from the requested show without treating a
            // venue query as an exact match on the song's title.
            match = new(MatchTier.Prefix, ["place"], .9);
        }
        else if (!isEvidence && (resolved || curated))
        {
            match = new(
                MatchTier.Exact,
                [.. remainder.Fields, "reading"],
                Math.Max(match.Relevance, .9)
            );
        }
        else if (
            verified
            && contextual
            && remainder.Tier == MatchTier.Prefix
            && match.Tier < MatchTier.Prefix
        )
        {
            match = remainder;
        }

        var boosted =
            (input.BoostArtistIds ?? []).Contains(row.ArtistId)
            || input.BoostArtistIds?.Any(id =>
                evidence.Vocabulary.ArtistSlugsById.GetValueOrDefault(id) == row.ArtistSlug
            ) == true;
        var priority =
            match.Tier != MatchTier.Exact ? 0
            : document.Kind == "performance"
                ? (
                    qualifiedPerformance && row.Preview != null && !row.Soundcheck ? 2
                    : row.Preview != null && !row.Soundcheck ? 1
                    : 0
                )
            : isEvidence ? 0
            : 2;
        return new(
            row,
            match,
            priority,
            document.PopularityPercentile,
            resolved && contextual || document.CatalogMatch == "name" ? .75 : 0,
            boosted ? 1 : 0,
            readingId is not (null or "query" or "literal") && query.Reading == "selected_place"
                    ? readingId
                : literalTitle || !verified
                    ? input.ReadingId == "literal" ? "literal"
                        : "query"
                : readingId ?? "query",
            document.Kind switch
            {
                "song" => evidence.SongPlays(document.Id),
                "performance" or "show" or "recording" => document.TrackPlays,
                _ => (long)document.Popularity,
            },
            NamedArtist: document.Kind is ("artist" or "show" or "song")
                && document.ArtistSlug == query.NamedArtist
        );
    }

    public static IEnumerable<RankedCandidate> RecordingsAsShows(
        IEnumerable<RankedCandidate> candidates,
        IReadOnlyDictionary<string, SearchDocument> shows
    )
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Row is not SearchDocument { Kind: "recording" } recording)
            {
                yield return candidate;
                continue;
            }

            // A recording without an indexed parent cannot supply a show card.
            if (!shows.TryGetValue(recording.ShowId, out var show))
            {
                continue;
            }

            // The recording explains the match and supplies the tap target.
            // Playback and percentile describe the show the listener sees.
            yield return candidate with
            {
                Row = show with
                {
                    ArtistId = show.ArtistId ?? recording.ArtistId,
                    SourceId = recording.Id,
                    SourceUpstream = recording.SourceUpstream,
                    TaperLabel = recording.TaperLabel,
                    TransferrerLabel = recording.TransferrerLabel,
                    Soundboard = recording.Soundboard,
                    Matrix = recording.Matrix,
                    Highlights = recording.Highlights,
                },
                Popularity = show.PopularityPercentile,
                SameKindCount = show.TrackPlays,
            };
        }
    }

    public static RankedCandidate Passage(SearchPassage passage, string reading) =>
        new(
            passage,
            new(MatchTier.Exact, ["segue"], 1),
            2,
            Popularity: passage.PopularityPercentile,
            ReadingId: reading,
            SameKindCount: passage.TrackPlays
        );
}

internal sealed record CatalogReading(
    SearchReading Option,
    SearchQuery Query,
    IReadOnlySet<string> Venues
)
{
    public static CatalogReading[] Options(
        SearchInput input,
        SearchQuery query,
        IReadOnlySet<string> venues,
        CatalogQueryEvidence evidence
    )
    {
        var vocabulary = evidence.Vocabulary;
        var literal = SearchInterpretationReader.Read(input, evidence, literal: true);
        var options = new List<CatalogReading>
        {
            new(new("literal", []), literal, new HashSet<string>()),
        };
        if (ReadPlaceAlias(input, query, vocabulary) is { } placeAlias)
        {
            options.Add(placeAlias);
        }
        if (
            (query.ImpliedArtist ?? query.ReadingArtist) is { } artist
            && vocabulary.ArtistDocuments.TryGetValue(artist, out var document)
        )
        {
            options.Add(
                new(
                    new($"artist:{document.Id}", [new("artist", document.Label, document.Id)]),
                    query with
                    {
                        ImpliedArtist = artist,
                    },
                    venues
                )
            );
        }
        if (input.Ignore?.Contains("artist") != true)
        {
            if (
                query.ImpliedArtist == null
                && query.ReadingArtist == null
                && vocabulary.ReadArtistInitialism(query.Terms, query.SelectedArtist)
                    is { } initialism
            )
            {
                var artistQuery = query with
                {
                    ImpliedArtist = initialism.Artist.ArtistSlug,
                    Terms = initialism.Remainder,
                    Reading = "artist_initialism",
                };
                options.Add(
                    new(
                        new(
                            $"artist:{initialism.Artist.Id}",
                            [new("artist", initialism.Artist.Label, initialism.Artist.Id)]
                        ),
                        artistQuery,
                        venues
                    )
                );
            }

            foreach (
                var alternative in vocabulary.ArtistOptions(literal.Terms, query.SelectedArtist)
            )
            {
                var id = $"artist:{alternative.Id}";
                if (options.All(option => option.Option.Id != id))
                {
                    options.Add(
                        new(
                            new(id, [new("artist", alternative.Label, alternative.Id)]),
                            query with
                            {
                                ImpliedArtist = alternative.ArtistSlug,
                            },
                            venues
                        )
                    );
                }
            }
        }
        if (input.Ignore?.Contains("venue") != true)
        {
            foreach (var venue in vocabulary.VenueOptions(query.Terms).Take(2))
            {
                var label = venue.LocationLabel ?? venue.CityLabel;
                options.Add(
                    new(
                        new(
                            $"venue:{venue.VenueId}",
                            [new("place", $"{venue.Label} · {label}", venue.VenueId)]
                        ),
                        query with
                        {
                            Reading = "selected_place",
                        },
                        new HashSet<string> { venue.VenueId }
                    )
                );
            }
        }
        if (input.Ignore?.Contains("venue") != true && options.Count < 3)
        {
            var place = vocabulary
                .Places(query.Terms)
                .OrderByDescending(value => value.Length)
                .FirstOrDefault();
            if (place != null && options.All(option => option.Option.Id != $"place:{place}"))
            {
                var matches = vocabulary.VenuesFor(place).ToArray();
                var label = matches
                    .Select(venue => venue.CityLabel)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (label is [var city] && city.Length > 0)
                {
                    options.Add(
                        new(
                            new($"place:{place}", [new("place", city, place)]),
                            query with
                            {
                                Reading = "selected_place",
                            },
                            matches.Select(venue => venue.VenueId).ToHashSet(StringComparer.Ordinal)
                        )
                    );
                }
            }
        }
        return options.Take(3).ToArray();
    }

    private static CatalogReading? ReadPlaceAlias(
        SearchInput input,
        SearchQuery query,
        SearchVocabulary vocabulary
    )
    {
        if (
            input.Ignore?.Contains("venue") == true
            || input.Scope is SearchScope.Artists or SearchScope.Songs
            || vocabulary.ReadPlaceAlias(query.Terms, query.Artist) is not { } reading
        )
        {
            return null;
        }

        var (name, place, remainder) = reading;
        var artist = vocabulary.FullArtist(remainder, query.Artist);
        var hasArtist = artist.Kind == "artist_identity";
        return new(
            new($"place:{name}", [new("place", place.Label, name)]),
            query with
            {
                Terms = hasArtist ? "" : remainder,
                ImpliedArtist = hasArtist ? artist.Artist : query.ImpliedArtist,
                Reading = "place_alias",
            },
            place.VenueIds
        );
    }
}
