using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Projects captured artist dictionaries and actual title extractions into a shadow
/// repertoire. Exact title correspondence is a proposal, not an evaluated identity
/// acceptance rule. Missing dictionary matches may produce provisional local songs;
/// they never establish new compositions or merge songs across artists.
/// </summary>
public sealed class ShadowRepertoireAdapter : IShadowCatalogAdapter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
    private static readonly Regex OpaqueOrUnparsedTitle = new(
        @"^(?:https?://|(?:disc|track|t)\s*\d+\b|\d{4}[-_.]\d{2}[-_.]\d{2})|^(?:\d+|[a-z])$|^\d{1,3}\s+-\s+|\b(?:unknown|unidentified|interpolat(?:ion|ed)|tease|recorded by|taped by)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );
    private readonly Dictionary<
        (long ArtistId, string Title),
        Dictionary<Guid, CapturedSong>
    > songs = [];
    private readonly IdentityCandidateMatcher matcher = new();

    public string Version => "shadow-repertoire-v2";

    public IReadOnlyList<string> DomainsFor(string inputKind) =>
        inputKind is "song_sample" or "track_sample" ? ["repertoire"] : [];

    /// <summary>Retains each legacy identity even when several entries share a title.</summary>
    public void Observe(JsonElement record)
    {
        if (!record.TryGetProperty("row", out var row))
        {
            return;
        }

        var kind = record.GetProperty("kind").GetString();
        if (kind == "song_sample")
        {
            AddSong(
                row,
                "song_sample:" + row.GetProperty("uuid").GetGuid().ToString("D"),
                "/row/name"
            );
        }
        else if (
            kind == "setlist_sample"
            && record.TryGetProperty("unordered_memberships", out var memberships)
        )
        {
            var inputKey = "setlist_sample:" + row.GetProperty("uuid").GetGuid().ToString("D");
            var index = 0;
            foreach (var membership in memberships.EnumerateArray())
            {
                if (
                    membership.TryGetProperty("song", out var song)
                    && song.ValueKind == JsonValueKind.Object
                )
                {
                    AddSong(song, inputKey, $"/unordered_memberships/{index}/song/name");
                }
                index++;
            }
        }
    }

    public IEnumerable<ShadowCatalogResult> Process(ShadowCatalogInput input)
    {
        if (input.Kind == "song_sample")
        {
            var name = input.Row.GetProperty("name").GetString() ?? "";
            var song = KnownSong(input.Artist.Uuid, input.Uuid, name);
            yield return ShadowCatalogResult.For(
                input,
                "repertoire",
                NormalizeTitle(name).Length == 0 ? "unresolved" : "proposed",
                Version,
                song,
                new ShadowRepertoireDictionaryEvidence(input.Key, "/row/name", name),
                NormalizeTitle(name).Length == 0 ? "empty_dictionary_title" : null,
                [song.EntityKey]
            );
        }
        else if (input.Kind == "track_sample")
        {
            yield return ProcessTrack(input);
        }
    }

    private ShadowCatalogResult ProcessTrack(ShadowCatalogInput input)
    {
        var title = input.Row.GetProperty("title").GetString() ?? "";
        var sourceUuid =
            input.Record.TryGetProperty("source_context", out var source)
            && source.ValueKind == JsonValueKind.Object
            && source.TryGetProperty("uuid", out var sourceId)
                ? sourceId.GetGuid()
                : (Guid?)null;
        var extraction = ReadExtraction(input, title);
        var segments =
            extraction
                ?.Segments.Select(segment =>
                    ResolveTitleMention(input.Artist.Id, input.Artist.Uuid, segment)
                )
                .ToArray()
            ?? [];
        var wholeFileCandidates = Candidates(input.Artist.Id, input.Artist.Uuid, title);
        var proposal = new ShadowRepertoireTrack(
            input.Uuid,
            sourceUuid,
            TrackTitleExtractor.Version,
            title,
            extraction?.IsResolved ?? false,
            segments,
            wholeFileCandidates,
            extraction
        );

        var outcome =
            extraction == null
            || segments.Length == 0
            || segments.Any(segment => segment.Outcome == "unresolved")
                ? "unresolved"
            : segments.All(segment => segment.Outcome == "not_applicable") ? "not_applicable"
            : "proposed";
        // Syntax resolution is not completeness. An unresolved producer response must
        // stay visible even when its supported mentions have useful local links.
        if (extraction?.IsResolved == false && outcome == "proposed")
        {
            outcome = "unresolved";
        }

        var entityKeys = segments
            .SelectMany(segment =>
                segment.Song != null
                    ? new[] { segment.Song.EntityKey }
                    : segment.CandidateSongs.Select(song => song.EntityKey)
            )
            .Concat(wholeFileCandidates.Select(candidate => candidate.Song.EntityKey))
            .Distinct()
            .Order()
            .ToArray();
        return ShadowCatalogResult.For(
            input,
            "repertoire",
            outcome,
            Version + "/" + TrackTitleExtractor.Version,
            proposal,
            new
            {
                InputKey = input.Key,
                JsonPointer = "/row/title",
                Quote = title,
            },
            extraction == null ? "missing_title_extraction"
                : outcome == "unresolved"
                    ? extraction.Reason ?? "one_or_more_title_mentions_need_review"
                : null,
            entityKeys
        );
    }

    private static TrackTitleExtraction? ReadExtraction(ShadowCatalogInput input, string title)
    {
        if (
            input.Extraction is not { } record
            || !record.TryGetProperty("titleExtraction", out var value)
            || value.ValueKind == JsonValueKind.Null
        )
        {
            return null;
        }

        if (
            record.GetProperty("resourceUuid").GetGuid() != input.Uuid
            || record.GetProperty("artistUuid").GetGuid() != input.Artist.Uuid
        )
        {
            throw new InvalidDataException(
                "Title extraction ownership differs from the retained file."
            );
        }
        var extraction = value.Deserialize<TrackTitleExtraction>(Json)!;
        if (extraction.ArtistUuid != input.Artist.Uuid || extraction.OriginalTitle != title)
        {
            throw new InvalidDataException(
                "Title extraction does not describe the retained artist and title."
            );
        }

        var codePoints = title.EnumerateRunes().ToArray();
        foreach (var segment in extraction.Segments)
        {
            var evidence = segment.Evidence;
            if (
                evidence.Field != "title"
                || evidence.JsonPointer != "/title"
                || evidence.StartCodePoint < 0
                || evidence.EndCodePoint <= evidence.StartCodePoint
                || evidence.EndCodePoint > codePoints.Length
                || string.Concat(
                    codePoints[evidence.StartCodePoint..evidence.EndCodePoint]
                        .Select(rune => rune.ToString())
                ) != evidence.Quote
                || (segment.Kind == TrackContentKind.Song && segment.Title != evidence.Quote)
            )
            {
                throw new InvalidDataException(
                    "A title mention lacks exact Unicode source evidence."
                );
            }
        }
        return extraction;
    }

    /// <summary>
    /// Proposes an artist-local correspondence for an already evidenced content mention.
    /// Callers retain responsibility for its source coordinates and content kind. This
    /// shared path lets decoded provider setlist passages use the same dictionary and
    /// provisional-title policy as file mentions without inventing a playback file.
    /// </summary>
    public ShadowRepertoireSegment ResolveTitleMention(
        long artistId,
        Guid artistUuid,
        TrackTitleSegment segment
    )
    {
        var title = segment.Title ?? segment.Evidence.Quote;
        if (segment.Kind is TrackContentKind.Nonmusic or TrackContentKind.Speech)
        {
            return Segment("not_applicable", "extractor_reports_non_song_content");
        }
        if (segment.Kind == TrackContentKind.Unknown)
        {
            return Segment("unresolved", "extractor_reports_unknown_content");
        }

        if (artistUuid == Guid.Empty)
        {
            return Segment("unresolved", "missing_artist_identity");
        }

        // Decorated controls can reach the producer's default Song class. Do not
        // turn them into songs merely because an imported dictionary has that label.
        var normalized = NormalizeTitle(title);
        if (
            normalized
            is "banter"
                or "stage banter"
                or "tuning"
                or "crowd"
                or "applause"
                or "band introductions"
                or "encore break"
        )
        {
            return Segment("not_applicable", "shadow_guard_explicit_control_label");
        }

        var candidates = Candidates(artistId, artistUuid, title);
        if (candidates.Count > 1)
        {
            return Segment(
                "unresolved",
                "multiple_artist_dictionary_entries",
                candidates: candidates
            );
        }
        if (candidates.Count == 1)
        {
            return Segment(
                "proposed",
                "unique_title_correspondence_is_not_identity_acceptance",
                candidates[0].Song,
                candidates
            );
        }
        if (segment.Kind == TrackContentKind.Jam)
        {
            return Segment("unresolved", "generic_jam_has_no_dictionary_identity");
        }

        // The current extractor's default class is Song. These shadow guards prevent
        // obvious controls and opaque labels from becoming provisional song identities;
        // they preserve the actual extraction rather than rewriting its producer output.
        if (
            normalized.Length == 0
            || normalized is "intro" or "intros" or "outro" or "interlude"
            || OpaqueOrUnparsedTitle.IsMatch(title)
            || title.Contains('/')
            || title.Count(character => character == '(')
                != title.Count(character => character == ')')
        )
        {
            return Segment("unresolved", "shadow_guard_title_needs_interpretation");
        }

        return Segment(
            "proposed",
            "title_derived_identity_within_captured_artist_dictionary",
            new(ProvisionalEntityKey(artistUuid, title), artistUuid, null, title, "title_derived")
        );

        ShadowRepertoireSegment Segment(
            string outcome,
            string reason,
            ShadowRepertoireSong? song = null,
            IReadOnlyList<ShadowRepertoireCandidate>? candidates = null
        ) =>
            new(
                segment.Position,
                segment.Kind,
                segment.Title,
                segment.Evidence,
                outcome,
                reason,
                song,
                candidates?.Select(candidate => candidate.Song).ToArray() ?? [],
                candidates ?? []
            );
    }

    private IReadOnlyList<ShadowRepertoireCandidate> Candidates(
        long artistId,
        Guid artistUuid,
        string title
    )
    {
        if (!songs.TryGetValue((artistId, NormalizeTitle(title)), out var matches))
        {
            return [];
        }

        var claim = new RepertoireMatchRecord(null, artistUuid, [new(title)], []);
        return matches
            .Values.OrderBy(song => song.Uuid)
            .Select(song => new ShadowRepertoireCandidate(
                KnownSong(artistUuid, song.Uuid, song.Name),
                song.Evidence.ToArray(),
                matcher.CompareRepertoire(claim, new(song.Uuid, artistUuid, [new(song.Name)], []))
            ))
            .ToArray();
    }

    private void AddSong(JsonElement row, string inputKey, string pointer)
    {
        var name = row.GetProperty("name").GetString() ?? "";
        var key = (row.GetProperty("artist_id").GetInt64(), NormalizeTitle(name));
        if (!songs.TryGetValue(key, out var matches))
        {
            matches = [];
            songs.Add(key, matches);
        }
        var uuid = row.GetProperty("uuid").GetGuid();
        if (!matches.TryGetValue(uuid, out var song))
        {
            song = new(uuid, name, []);
            matches.Add(uuid, song);
        }
        var evidence = new ShadowRepertoireDictionaryEvidence(inputKey, pointer, name);
        if (!song.Evidence.Contains(evidence))
        {
            song.Evidence.Add(evidence);
        }
    }

    private static ShadowRepertoireSong KnownSong(Guid artistUuid, Guid songUuid, string title) =>
        new(KnownEntityKey(songUuid), artistUuid, songUuid, title, "captured_artist_dictionary");

    /// <summary>References a retained catalog identity, preserving its original UUID.</summary>
    public static string KnownEntityKey(Guid songUuid) =>
        ShadowCatalogIdentity.Key("repertoire", songUuid.ToString("D"));

    /// <summary>Scopes a title-derived shadow identity to one artist, never a global work.</summary>
    public static string ProvisionalEntityKey(Guid artistUuid, string title) =>
        ShadowCatalogIdentity.Key("repertoire", $"{artistUuid:D}\n{NormalizeTitle(title)}");

    /// <summary>
    /// Retrieves exact word-normalized title candidates. Accents remain significant;
    /// punctuation and case are not identity evidence. The public matcher supplies diagnostics.
    /// </summary>
    public static string NormalizeTitle(string title)
    {
        var result = new StringBuilder();
        var separator = false;
        foreach (var rune in title.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            if (
                Rune.IsLetterOrDigit(rune)
                || Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark
            )
            {
                if (separator && result.Length > 0)
                {
                    result.Append(' ');
                }
                result.Append(Rune.ToLowerInvariant(rune).ToString());
                separator = false;
            }
            else
            {
                separator = true;
            }
        }
        return result.ToString();
    }

    private sealed record CapturedSong(
        Guid Uuid,
        string Name,
        List<ShadowRepertoireDictionaryEvidence> Evidence
    );
}
