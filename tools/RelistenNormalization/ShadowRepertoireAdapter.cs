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
    private static readonly Regex BanterLabel = new(
        @"^(?:\d{1,3}[.)]?\s+)?(?:stage\s+)?banter(?:\s*[-:–—]\s*(?<description>.+))?[.!]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );
    private static readonly Regex RecordingFilename = new(
        @"^\S*(?:\d{4}|\d{2})[-_.]\d{2}[-_.]\d{2}(?:[._-]?[sd]\d+)?[._-]?t\d+(?:[._-][\p{L}\p{N}._-]+)*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );
    private static readonly Regex CompactDateRecordingLabel = new(
        @"^[\p{L}\s._-]*(?<date>\d{8}|\d{6})[a-z]?(?:[\s._-]*(?:side|set|disc|track|part|[sdt])[\s._-]*(?:\d+|[a-d]|[ivx]+))+(?:[\s._-]+[\p{L}\p{N}._-]+)*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );
    private const string ControlPassageTerm =
        @"(?:intro(?:s|duction(?:s)?)?|outro|sound\s?check|interviews?|(?:stage\s)?banter|tuning|crowd|applause|band\sintroductions?|announcements?|talk)";
    private static readonly Regex GenericControlPassage = new(
        "^"
            + ControlPassageTerm
            + @"(?:(?:\s+and\s+|\s*[&,;/+]\s*)"
            + ControlPassageTerm
            + ")*[.!]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );
    private static readonly Regex UnnamedContentLabel = new(
        @"^untitled(?:\s+(?:song|track|tune|piece))?(?:\s+\d+)?$",
        RegexOptions.CultureInvariant
    );
    private static readonly Regex RecordingDivision = new(
        @"^(?:set|disc|part)\s*(?:\d+|[ivx]+)(?:\s*[-,:]?\s*(?:set|part)\s*(?:\d+|[ivx]+))*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );
    private static readonly Regex NumericAnnotation = new(
        @"^\(\s*\d+(?:\s*[,;]\s*\d+)*\s*\)$",
        RegexOptions.CultureInvariant
    );
    private static readonly Regex DurationSuffix = new(
        @"\(\s*\d{1,3}:\d{2}(?:[.:]\d+)?\s*\)\s*$",
        RegexOptions.CultureInvariant
    );
    private static readonly Regex TransitionResidue = new(
        @"^[<>→]+|[<>→]+$",
        RegexOptions.CultureInvariant
    );
    private readonly Dictionary<
        (long ArtistId, string Title),
        Dictionary<Guid, CapturedSong>
    > songs = [];

    // A descriptor bundle has artist UUIDs and explicit source scopes, not native
    // numeric artist IDs. Each consolidation scope owns a separate adapter instance.
    private readonly Dictionary<
        (Guid ArtistUuid, string Title),
        Dictionary<Guid, CapturedSong>
    > descriptorSongs = [];
    private readonly Dictionary<
        (Guid ArtistUuid, Guid SongUuid),
        CapturedSong
    > descriptorIdentities = [];

    // A boundary-only spelling is a fallback candidate, never a rewrite of a
    // retained name. Separate indexes preserve exact-title priority and every rival.
    private readonly Dictionary<
        (long ArtistId, string Title),
        Dictionary<Guid, CapturedSong>
    > songsWithoutWordBoundaries = [];
    private readonly Dictionary<
        (Guid ArtistUuid, string Title),
        Dictionary<Guid, CapturedSong>
    > descriptorSongsWithoutWordBoundaries = [];
    private readonly IdentityCandidateMatcher matcher = new();

    public string Version => "shadow-repertoire-v7";

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
                    ResolveTitleMention(
                        input.Artist.Id,
                        input.Artist.Uuid,
                        segment,
                        extraction.Segments.Count == 1 ? title : null
                    )
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
        TrackTitleSegment segment,
        string? originalTitle = null
    ) =>
        ResolveTitleMentionCore(
            artistUuid,
            segment,
            Candidates(artistId, artistUuid, segment.Title ?? segment.Evidence.Quote),
            originalTitle == null ? [] : Candidates(artistId, artistUuid, originalTitle),
            originalTitle
        );

    /// <summary>Uses a complete, explicitly scoped descriptor dictionary without inventing native artist IDs.</summary>
    public ShadowRepertoireSegment ResolveTitleMention(
        Guid artistUuid,
        TrackTitleSegment segment,
        string? originalTitle = null
    ) =>
        ResolveTitleMentionCore(
            artistUuid,
            segment,
            DictionaryCandidates(artistUuid, segment.Title ?? segment.Evidence.Quote),
            originalTitle == null ? [] : DictionaryCandidates(artistUuid, originalTitle),
            originalTitle
        );

    public void ObserveDictionary(MusicDictionaryName value)
    {
        var title = NormalizeTitle(value.Title);
        if (title.Length == 0)
        {
            return;
        }
        if (!descriptorSongs.TryGetValue((value.ArtistUuid, title), out var matches))
        {
            matches = [];
            descriptorSongs.Add((value.ArtistUuid, title), matches);
        }
        var evidence = new ShadowRepertoireDictionaryEvidence(
            value.InputKey,
            value.Witness.ValuePointer!,
            value.Title
        )
        {
            RetainedWitness = value.Witness,
        };
        if (
            !descriptorIdentities.TryGetValue(
                (value.ArtistUuid, value.LegacySongUuid),
                out var identity
            )
        )
        {
            identity = new(value.LegacySongUuid, evidence);
            descriptorIdentities.Add((value.ArtistUuid, value.LegacySongUuid), identity);
        }
        else
        {
            identity.AddWitness(evidence);
        }
        matches[value.LegacySongUuid] = identity;

        var boundaryKey = (
            value.ArtistUuid,
            IdentityCandidateMatcher.RepertoireTitleBoundaryCandidateKey(value.Title)
        );
        if (!descriptorSongsWithoutWordBoundaries.TryGetValue(boundaryKey, out var boundaryMatches))
        {
            boundaryMatches = [];
            descriptorSongsWithoutWordBoundaries.Add(boundaryKey, boundaryMatches);
        }
        boundaryMatches[value.LegacySongUuid] = identity;
    }

    public ShadowRepertoireCandidate? DictionaryIdentity(Guid artistUuid, Guid songUuid)
    {
        return descriptorIdentities.TryGetValue((artistUuid, songUuid), out var identity)
            ? CompareCandidates(artistUuid, identity.Name, new() { [songUuid] = identity })[0]
            : null;
    }

    public IReadOnlyList<ShadowRepertoireCandidate> DictionaryCandidates(
        Guid artistUuid,
        string title
    )
    {
        if (!descriptorSongs.TryGetValue((artistUuid, NormalizeTitle(title)), out var matches))
        {
            descriptorSongsWithoutWordBoundaries.TryGetValue(
                (artistUuid, IdentityCandidateMatcher.RepertoireTitleBoundaryCandidateKey(title)),
                out matches
            );
        }
        return CompareCandidates(artistUuid, title, matches);
    }

    private static ShadowRepertoireSegment ResolveTitleMentionCore(
        Guid artistUuid,
        TrackTitleSegment segment,
        IReadOnlyList<ShadowRepertoireCandidate> candidates,
        IReadOnlyList<ShadowRepertoireCandidate> originalCandidates,
        string? originalTitle
    )
    {
        var title = segment.Title ?? segment.Evidence.Quote;
        var trimmedTitle = title.Trim();
        var normalized = NormalizeTitle(title);
        // Syntax may remove a recording annotation, but a retained literal dictionary
        // identity for the complete title still has priority over that interpretation.
        if (
            originalTitle != null
            && originalCandidates.Any(candidate =>
                candidate.Evidence.Any(evidence =>
                    LiteralTitleKey(evidence.Quote) == LiteralTitleKey(originalTitle)
                )
            )
        )
        {
            candidates = originalCandidates;
            return DictionarySegment();
        }
        // Literal dictionary wording preserves an existing identity even when the producer
        // calls that wording a control label. Punctuation-normalized correspondence alone
        // cannot override an explicit content label (for example Banter, versus Banter).
        var literalTitle = LiteralTitleKey(title);
        var literalDictionaryMatch = candidates.Any(candidate =>
            candidate.Evidence.Any(evidence => LiteralTitleKey(evidence.Quote) == literalTitle)
        );
        if (literalDictionaryMatch)
        {
            return DictionarySegment();
        }
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
        if (normalized.Length == 0)
        {
            return Segment("unresolved", "shadow_guard_title_needs_interpretation");
        }

        // Decorated controls can reach the producer's default Song class. A normalized
        // imported label supplies no additional evidence that such a mention is musical.
        if (IsExplicitControlLabel(normalized))
        {
            return Segment("not_applicable", "shadow_guard_explicit_control_label");
        }

        var banter = BanterLabel.Match(trimmedTitle);
        if (banter.Success)
        {
            var description = banter.Groups["description"].Value;
            var clearlySpoken =
                description.Length == 0
                || description.StartsWith("inviting out ", StringComparison.OrdinalIgnoreCase);
            return Segment(
                clearlySpoken ? "not_applicable" : "unresolved",
                clearlySpoken
                    ? "shadow_guard_explicit_banter_label"
                    : "shadow_guard_banter_description"
            );
        }

        // A passage label can describe music or speech without naming an authored song.
        // Match the whole label so titles such as Soundcheck Blues remain usable. Literal
        // dictionary assertions above still preserve actual catalog identities.
        // A boundary spelling of a known control supplies no additional musical
        // evidence. Reuse the same control grammar; literal dictionary wording won above.
        if (
            GenericControlPassage.IsMatch(trimmedTitle)
            || candidates.Any(candidate =>
                candidate.Evidence.Any(evidence =>
                    IsExplicitControlLabel(NormalizeTitle(evidence.Quote))
                    || GenericControlPassage.IsMatch(evidence.Quote.Trim())
                )
            )
        )
        {
            return Segment("unresolved", "shadow_guard_generic_control_passage");
        }
        if (UnnamedContentLabel.IsMatch(normalized))
        {
            return Segment("unresolved", "shadow_guard_unnamed_content");
        }

        if (candidates.Count > 0)
        {
            return DictionarySegment();
        }
        if (segment.Kind == TrackContentKind.Jam)
        {
            return Segment("unresolved", "generic_jam_has_no_dictionary_identity");
        }

        // The current extractor's default class is Song. These shadow guards prevent
        // obvious controls and opaque labels from becoming provisional song identities;
        // they preserve the actual extraction rather than rewriting its producer output.
        if (
            normalized is "intro" or "intros" or "outro" or "interlude"
            || OpaqueOrUnparsedTitle.IsMatch(title)
            || RecordingFilename.IsMatch(trimmedTitle)
            || originalTitle != null
                && RecordingFilename.IsMatch(originalTitle.Trim())
                && !title.Any(char.IsWhiteSpace)
            || IsCompactDateRecordingLabel(trimmedTitle)
            || RecordingDivision.IsMatch(trimmedTitle)
            || NumericAnnotation.IsMatch(trimmedTitle)
            || DurationSuffix.IsMatch(title)
            || TransitionResidue.IsMatch(trimmedTitle)
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

        ShadowRepertoireSegment DictionarySegment() =>
            candidates.Count > 1
                ? Segment(
                    "unresolved",
                    "multiple_artist_dictionary_entries",
                    candidates: candidates
                )
                : Segment(
                    "proposed",
                    candidates[0]
                        .Evidence.Any(evidence => NormalizeTitle(evidence.Quote) == normalized)
                        ? "unique_title_correspondence_is_not_identity_acceptance"
                        : "unique_boundary_spelling_correspondence_is_not_identity_acceptance",
                    candidates[0].Song,
                    candidates
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
        var normalized = NormalizeTitle(title);
        if (normalized.Length == 0)
        {
            return [];
        }
        if (!songs.TryGetValue((artistId, normalized), out var matches))
        {
            songsWithoutWordBoundaries.TryGetValue(
                (artistId, IdentityCandidateMatcher.RepertoireTitleBoundaryCandidateKey(title)),
                out matches
            );
        }
        return CompareCandidates(artistUuid, title, matches);
    }

    private IReadOnlyList<ShadowRepertoireCandidate> CompareCandidates(
        Guid artistUuid,
        string title,
        Dictionary<Guid, CapturedSong>? matches
    )
    {
        if (matches == null)
        {
            return [];
        }
        var claim = new RepertoireMatchRecord(null, artistUuid, [new(title)], []);
        return matches
            .Values.OrderBy(song => song.Uuid)
            .Select(song => new ShadowRepertoireCandidate(
                KnownSong(artistUuid, song.Uuid, song.Name),
                song.Witnesses.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => pair.Value)
                    .ToArray(),
                matcher.CompareRepertoire(
                    claim,
                    new(
                        song.Uuid,
                        artistUuid,
                        song.Witnesses.Values.Select(witness => new IdentityMatchingName(
                                witness.Quote
                            ))
                            .ToArray(),
                        []
                    )
                )
            ))
            .ToArray();
    }

    private void AddSong(JsonElement row, string inputKey, string pointer)
    {
        var name = row.GetProperty("name").GetString() ?? "";
        var normalized = NormalizeTitle(name);
        // Keep the imported identity in dictionary/setlist results, but an empty lookup
        // value cannot distinguish that row from unrelated punctuation-only file titles.
        if (normalized.Length == 0)
        {
            return;
        }

        var key = (row.GetProperty("artist_id").GetInt64(), normalized);
        if (!songs.TryGetValue(key, out var matches))
        {
            matches = [];
            songs.Add(key, matches);
        }
        var uuid = row.GetProperty("uuid").GetGuid();
        var evidence = new ShadowRepertoireDictionaryEvidence(inputKey, pointer, name);
        AddWitness(matches, uuid, evidence);

        var boundaryKey = (
            key.Item1,
            IdentityCandidateMatcher.RepertoireTitleBoundaryCandidateKey(name)
        );
        if (!songsWithoutWordBoundaries.TryGetValue(boundaryKey, out var boundaryMatches))
        {
            boundaryMatches = [];
            songsWithoutWordBoundaries.Add(boundaryKey, boundaryMatches);
        }
        AddWitness(boundaryMatches, uuid, evidence);
    }

    private static void AddWitness(
        Dictionary<Guid, CapturedSong> matches,
        Guid uuid,
        ShadowRepertoireDictionaryEvidence evidence
    )
    {
        if (!matches.TryGetValue(uuid, out var song))
        {
            matches.Add(uuid, new(uuid, evidence));
        }
        else
        {
            song.AddWitness(evidence);
        }
    }

    private static bool PreferWitness(
        ShadowRepertoireDictionaryEvidence candidate,
        ShadowRepertoireDictionaryEvidence current
    )
    {
        // A dictionary row is the direct title assertion. Repeated setlist memberships
        // retain their own complete results; copying every occurrence into each file's
        // identity proof made the full catalog output grow with files × occurrences.
        var candidateIsDictionary = candidate.InputKey.StartsWith(
            "song_sample:",
            StringComparison.Ordinal
        );
        var currentIsDictionary = current.InputKey.StartsWith(
            "song_sample:",
            StringComparison.Ordinal
        );
        if (candidateIsDictionary != currentIsDictionary)
        {
            return candidateIsDictionary;
        }

        var inputOrder = StringComparer.Ordinal.Compare(candidate.InputKey, current.InputKey);
        if (inputOrder != 0)
        {
            return inputOrder < 0;
        }
        var pointerOrder = StringComparer.Ordinal.Compare(
            candidate.JsonPointer,
            current.JsonPointer
        );
        return pointerOrder != 0
            ? pointerOrder < 0
            : StringComparer.Ordinal.Compare(candidate.Quote, current.Quote) < 0;
    }

    private static bool IsExplicitControlLabel(string normalized) =>
        normalized
            is "banter"
                or "stage banter"
                or "tuning"
                or "crowd"
                or "applause"
                or "band introductions"
                or "encore break";

    private static string LiteralTitleKey(string title) =>
        title.Normalize(NormalizationForm.FormC).Trim().ToUpperInvariant();

    private static bool IsCompactDateRecordingLabel(string title)
    {
        var match = CompactDateRecordingLabel.Match(title);
        if (!match.Success)
        {
            return false;
        }

        var date = match.Groups["date"].Value;
        return DateOnly.TryParseExact(
            date,
            date.Length == 8 ? "yyyyMMdd" : "yyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _
        );
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

    private sealed class CapturedSong(Guid uuid, ShadowRepertoireDictionaryEvidence witness)
    {
        public Guid Uuid { get; } = uuid;
        public string Name => preferredWitness.Quote;
        public Dictionary<string, ShadowRepertoireDictionaryEvidence> Witnesses { get; } =
            new(StringComparer.Ordinal) { [LiteralTitleKey(witness.Quote)] = witness };

        private ShadowRepertoireDictionaryEvidence preferredWitness = witness;

        public void AddWitness(ShadowRepertoireDictionaryEvidence candidate)
        {
            // Punctuation-distinct spellings retain their own proof, so preferring a direct
            // display name cannot erase a literal membership's control-label override.
            var literalTitle = LiteralTitleKey(candidate.Quote);
            if (
                !Witnesses.TryGetValue(literalTitle, out var current)
                || PreferWitness(candidate, current)
            )
            {
                Witnesses[literalTitle] = candidate;
            }

            if (PreferWitness(candidate, preferredWitness))
            {
                preferredWitness = candidate;
            }
        }
    }
}
