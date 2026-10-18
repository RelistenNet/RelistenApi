using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Projects catalog content into authored-work proposals using independently retained source
/// correspondence. Repertoire names supply retrieval context only. A global work key is emitted
/// only after a cited work correspondence survives the local artist/title and competitor checks.
/// No database, provider, model, policy activation, or publication route is used here.
/// </summary>
public sealed partial class ShadowCompositionAdapter : IShadowCatalogAdapter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
    private readonly ShadowCompositionEvidence evidence;
    private readonly IdentityCandidateMatcher matcher = new();
    private readonly Dictionary<Guid, ShadowCompositionProof[]> proofsByOriginal;
    private readonly Dictionary<
        (Guid Artist, string Title),
        ShadowCompositionProof[]
    > proofsByContext;
    private readonly Dictionary<(long Artist, string Title), HashSet<Guid>> repertoire = new();
    private long observedMusicRecords;

    // The batch checkpoints adapter versions. Changing admitted evidence must reject a
    // resumption rather than mixing already-written rows with a different proof wave.
    public string Version => "shadow-composition-v2/primary=" + (evidence.ManifestSha256 ?? "none");

    /// <param name="primaryEvidencePath">
    /// Optional caller-selected &lt;manifest-path&gt;#sha256=&lt;digest&gt;. Null omits the source-reviewed
    /// primary packet. Retained provider inputs are independently admitted through Observe.
    /// </param>
    public ShadowCompositionAdapter(string? primaryEvidencePath = null)
    {
        evidence = new ShadowCompositionEvidence(primaryEvidencePath);
        proofsByOriginal = evidence
            .Proofs.GroupBy(proof => proof.CatalogUuid)
            .ToDictionary(group => group.Key, group => group.ToArray());
        proofsByContext = evidence
            .Proofs.GroupBy(proof => (proof.ArtistUuid, NormalizeTitle(proof.Title)))
            .ToDictionary(group => group.Key, group => group.ToArray());
    }

    public IReadOnlyList<string> DomainsFor(string inputKind) =>
        inputKind is "song_sample" or "track_sample" or "setlist_sample" or "provider_input"
            ? ["compositions"]
            : [];

    public void Observe(JsonElement record)
    {
        ObserveCompositionContext(record);

        if (
            !record.TryGetProperty("row", out var row)
            || !record.TryGetProperty("kind", out var kind)
        )
        {
            return;
        }

        switch (kind.GetString())
        {
            case "song_sample":
                observedMusicRecords++;
                ObserveSong(row);
                break;
            case "track_sample":
                observedMusicRecords++;
                break;
            case "setlist_sample":
                observedMusicRecords++;
                foreach (
                    var membership in record.GetProperty("unordered_memberships").EnumerateArray()
                )
                {
                    if (
                        membership.TryGetProperty("song", out var song)
                        && song.ValueKind == JsonValueKind.Object
                    )
                    {
                        ObserveSong(song);
                    }
                }
                break;
        }
    }

    public IEnumerable<ShadowCatalogResult> Process(ShadowCatalogInput input)
    {
        if (DomainsFor(input.Kind).Count == 0)
        {
            yield break;
        }

        if (input.Kind == "provider_input")
        {
            yield return ProcessProvider(input);
            yield break;
        }

        var mentions = ReadMentions(input);
        var linked = mentions.Count(mention => mention.Outcome == "proposed");
        var unresolved = mentions.Count(mention => mention.Outcome == "unresolved");
        var outcome =
            linked > 0 ? "proposed"
            : mentions.Count > 0 && unresolved == 0 ? "not_applicable"
            : "unresolved";
        var reason =
            linked > 0
                ? unresolved > 0
                    ? "partial_supported_composition_links"
                    : "supported_composition_links"
                : unresolved > 0
                    ? "composition_evidence_required"
                    : "no_named_authored_content";
        var partitions = mentions
            .GroupBy(mention => mention.NextEvidencePartition)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var proposal = new ShadowCompositionProposal(mentions, partitions, false, false);
        var keys = mentions
            .SelectMany(mention => mention.RepertoireEntityKeys)
            .Concat(mentions.SelectMany(mention => mention.Links.Select(link => link.EntityKey)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        yield return ShadowCatalogResult.For(
            input,
            "compositions",
            outcome,
            Version,
            proposal,
            new
            {
                rawInputKey = input.Key,
                extractionPresent = input.Extraction.HasValue,
                evidenceManifestSha256 = evidence.ManifestSha256,
                admittedSourceCorrespondences = evidence.Proofs.Count,
                observedMusicRecords,
                failedProviderInputs = providerIndexFailures.Count,
                proofScope = "Source-supported composition drafts. Artist repertoire, audio identity, author-person identity, and publication remain separate.",
            },
            reason,
            keys
        );
    }

    private void ObserveSong(JsonElement row)
    {
        var artist = row.GetProperty("artist_id").GetInt64();
        var title = row.GetProperty("name").GetString() ?? "";
        var key = (artist, NormalizeTitle(title));
        if (!repertoire.TryGetValue(key, out var songs))
        {
            songs = [];
            repertoire.Add(key, songs);
        }

        songs.Add(row.GetProperty("uuid").GetGuid());
    }

    private IReadOnlyList<ShadowCompositionMention> ReadMentions(ShadowCatalogInput input)
    {
        if (input.Kind == "song_sample")
        {
            return
            [
                Resolve(
                    input,
                    input.Row.GetProperty("name").GetString(),
                    "song",
                    "/row/name",
                    null,
                    input.Uuid
                ),
            ];
        }

        if (input.Kind == "setlist_sample")
        {
            var mentions = new List<ShadowCompositionMention>();
            var index = 0;
            foreach (
                var membership in input.Record.GetProperty("unordered_memberships").EnumerateArray()
            )
            {
                var pointer = "/unordered_memberships/" + index++ + "/song/name";
                if (
                    membership.TryGetProperty("song", out var song)
                    && song.ValueKind == JsonValueKind.Object
                )
                {
                    if (song.GetProperty("artist_id").GetInt64() != input.Artist.Id)
                    {
                        mentions.Add(
                            Unresolved(
                                null,
                                "unknown",
                                pointer,
                                null,
                                [],
                                "membership_artist_conflict",
                                []
                            )
                        );
                        continue;
                    }

                    mentions.Add(
                        Resolve(
                            input,
                            song.GetProperty("name").GetString(),
                            "song",
                            pointer,
                            null,
                            song.GetProperty("uuid").GetGuid()
                        )
                    );
                }
                else
                {
                    mentions.Add(
                        Unresolved(null, "unknown", pointer, null, [], "missing_setlist_song", [])
                    );
                }
            }

            return mentions;
        }

        return TrackMentions(input);
    }

    private IReadOnlyList<ShadowCompositionMention> TrackMentions(ShadowCatalogInput input)
    {
        if (
            input.Extraction is not { } extraction
            || !extraction.TryGetProperty("titleExtraction", out var title)
            || title.ValueKind != JsonValueKind.Object
        )
        {
            return
            [
                Unresolved(
                    input.Row.GetProperty("title").GetString(),
                    "unknown",
                    "/row/title",
                    null,
                    [],
                    "local_title_extraction_missing",
                    []
                ),
            ];
        }

        if (
            (
                extraction.TryGetProperty("resourceUuid", out var resource)
                && resource.GetGuid() != input.Uuid
            )
            || (
                extraction.TryGetProperty("artistUuid", out var owner)
                && owner.GetGuid() != input.Artist.Uuid
            )
        )
        {
            throw new InvalidDataException(
                "Title extraction ownership differs from the retained file."
            );
        }

        var parsed =
            title.Deserialize<TrackTitleExtraction>(Json)
            ?? throw new InvalidDataException("Track title extraction is absent.");
        if (
            parsed.ArtistUuid != input.Artist.Uuid
            || parsed.OriginalTitle != input.Row.GetProperty("title").GetString()
        )
        {
            throw new InvalidDataException(
                "Track extraction belongs to a different source artist/title."
            );
        }

        var result = new List<ShadowCompositionMention>();
        foreach (var segment in parsed.Segments)
        {
            ValidateTitleEvidence(parsed.OriginalTitle, segment.Evidence);
            if (segment.Kind == TrackContentKind.Song && segment.Title != segment.Evidence.Quote)
            {
                throw new InvalidDataException(
                    "Song interpretation differs from its exact title evidence."
                );
            }
            var kind = JsonNamingPolicy.SnakeCaseLower.ConvertName(segment.Kind.ToString());
            if (segment.Kind is TrackContentKind.Speech or TrackContentKind.Nonmusic)
            {
                result.Add(
                    new(
                        segment.Title,
                        kind,
                        "/row/title",
                        segment.Evidence,
                        [],
                        "not_applicable",
                        "non_authored_content",
                        [],
                        [],
                        "The extraction reports speech or nonmusic; no authored-work identity follows."
                    )
                );
            }
            else if (
                segment.Kind != TrackContentKind.Song
                || string.IsNullOrWhiteSpace(segment.Title)
            )
            {
                result.Add(
                    Unresolved(
                        segment.Title,
                        kind,
                        "/row/title",
                        segment.Evidence,
                        [],
                        "named_content_required",
                        []
                    )
                );
            }
            else
            {
                result.Add(
                    Resolve(input, segment.Title, kind, "/row/title", segment.Evidence, null)
                );
            }
        }

        if (result.Count == 0)
        {
            result.Add(
                Unresolved(
                    parsed.OriginalTitle,
                    "unknown",
                    "/row/title",
                    null,
                    [],
                    "named_content_required",
                    []
                )
            );
        }

        return result;
    }

    private ShadowCompositionMention Resolve(
        ShadowCatalogInput input,
        string? title,
        string kind,
        string pointer,
        NormalizationTextEvidence? localEvidence,
        Guid? songUuid
    )
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Unresolved(
                title,
                kind,
                pointer,
                localEvidence,
                [],
                "named_content_required",
                []
            );
        }

        var known = repertoire.GetValueOrDefault((input.Artist.Id, NormalizeTitle(title))) ?? [];
        var repertoireKeys =
            songUuid is { } exactSong ? new[] { ShadowRepertoireAdapter.KnownEntityKey(exactSong) }
            : known.Count > 0
                ? known.Order().Select(ShadowRepertoireAdapter.KnownEntityKey).ToArray()
            : new[] { ShadowRepertoireAdapter.ProvisionalEntityKey(input.Artist.Uuid, title) };
        var anchor = songUuid ?? input.Uuid;
        var retainedCandidates =
            evidence.CandidateContexts.GetValueOrDefault((input.Artist.Uuid, NormalizeTitle(title)))
            ?? [];
        var direct = proofsByOriginal.GetValueOrDefault(anchor) ?? [];
        var contextual =
            proofsByContext.GetValueOrDefault((input.Artist.Uuid, NormalizeTitle(title))) ?? [];
        var matching = direct.Length > 0 ? direct : contextual;
        matching = matching
            .Where(proof =>
                proof.ArtistUuid == input.Artist.Uuid
                && NormalizeTitle(proof.Title) == NormalizeTitle(title)
            )
            .ToArray();

        if (direct.Length == 0 && known.Count > 1)
        {
            return Unresolved(
                title,
                kind,
                pointer,
                localEvidence,
                repertoireKeys,
                "ambiguous_artist_repertoire",
                matching.Select(proof => WorkKey(proof.WorkMbid)).Distinct().ToArray()
            ) with
            {
                RetainedCandidates = retainedCandidates,
            };
        }

        var providerCorrespondences = ProviderCorrespondences(input, title);
        var providerWorks = providerCorrespondences
            .Select(value => value.WorkMbid)
            .Distinct()
            .ToArray();
        if (matching.Length == 0 && providerCorrespondences.Count > 0)
        {
            return ResolveProviderCorrespondence(
                input,
                title,
                kind,
                pointer,
                localEvidence,
                repertoireKeys,
                providerCorrespondences,
                retainedCandidates
            );
        }
        var works = matching
            .Select(proof => proof.WorkMbid)
            .Concat(providerWorks)
            .Distinct()
            .ToArray();
        if (works.Length != 1)
        {
            var partition =
                works.Length > 1 ? "competing_supported_work_correspondences"
                : known.Count > 0 || songUuid.HasValue
                    ? "artist_repertoire_needs_primary_work_credits"
                : "named_content_needs_repertoire_and_work";
            return Unresolved(
                title,
                kind,
                pointer,
                localEvidence,
                repertoireKeys,
                partition,
                works.Select(WorkKey).ToArray()
            ) with
            {
                RetainedCandidates = retainedCandidates,
                ProviderCorrespondences = providerCorrespondences,
            };
        }

        var links = matching
            .Select(proof => new ShadowCompositionWorkLink(
                WorkKey(proof.WorkMbid),
                proof.WorkMbid,
                proof.WorkTitle,
                direct.Length > 0
                    ? "exact_catalog_original"
                    : "supported_artist_repertoire_context",
                proof.ClaimId,
                proof.ManifestSha256,
                proof.Evidence,
                proof.Writers,
                proof.ProviderWriterRoles,
                Compare(title, proof),
                proof.Rationale,
                proof.CompetingIdentityAssessment,
                proof.RemainingGaps
            ))
            .ToArray();
        return new(
            title,
            kind,
            pointer,
            localEvidence,
            repertoireKeys,
            "proposed",
            "source_supported_work",
            links,
            [],
            "A cited work correspondence supports this composition proposal; no recording or writer-person identity is inferred.",
            retainedCandidates
        );
    }

    private IdentityCandidateComparison Compare(string title, ShadowCompositionProof proof)
    {
        var identity = new MatchingExternalIdentity(
            "musicbrainz.org",
            "work",
            proof.WorkMbid.ToString("D"),
            true
        );
        return matcher.CompareCompositions(
            new(null, [new(title)], [identity], new HashSet<Guid>(), false),
            new(null, [new(proof.WorkTitle)], [identity], new HashSet<Guid>(), false)
        );
    }

    private static ShadowCompositionMention Unresolved(
        string? title,
        string kind,
        string pointer,
        NormalizationTextEvidence? evidence,
        IReadOnlyList<string> repertoireKeys,
        string partition,
        IReadOnlyList<string> candidates
    ) =>
        new(
            title,
            kind,
            pointer,
            evidence,
            repertoireKeys,
            "unresolved",
            partition,
            [],
            candidates,
            "Retain this input for the named evidence wave; missing work or author facts are not a negative match."
        );

    private static void ValidateTitleEvidence(string original, NormalizationTextEvidence evidence)
    {
        var runes = original.EnumerateRunes().ToArray();
        if (
            evidence.Field != "title"
            || evidence.JsonPointer != "/title"
            || evidence.StartCodePoint < 0
            || evidence.EndCodePoint <= evidence.StartCodePoint
            || evidence.EndCodePoint > runes.Length
            || string.Concat(
                runes
                    .Skip(evidence.StartCodePoint)
                    .Take(evidence.EndCodePoint - evidence.StartCodePoint)
                    .Select(rune => rune.ToString())
            ) != evidence.Quote
        )
        {
            throw new InvalidDataException(
                "Track extraction title evidence differs from the retained input."
            );
        }
    }

    internal static string NormalizeTitle(string title) =>
        string.Join(
            ' ',
            title
                .Normalize(NormalizationForm.FormC)
                .Replace('\u2019', '\'')
                .ToUpperInvariant()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        );

    public static string WorkKey(Guid musicBrainzWorkMbid) =>
        "composition:musicbrainz:" + musicBrainzWorkMbid.ToString("D");
}

public sealed record ShadowCompositionProposal(
    IReadOnlyList<ShadowCompositionMention> Mentions,
    IReadOnlyDictionary<string, int> NextEvidencePartitions,
    bool ExternalUniverseComplete,
    bool PublicationPermitted
);

public sealed record ShadowCompositionMention(
    string? Title,
    string ContentKind,
    string SourcePointer,
    NormalizationTextEvidence? LocalTitleEvidence,
    IReadOnlyList<string> RepertoireEntityKeys,
    string Outcome,
    string NextEvidencePartition,
    IReadOnlyList<ShadowCompositionWorkLink> Links,
    IReadOnlyList<string> CompetingWorkKeys,
    string Reason,
    IReadOnlyList<ShadowCompositionCandidate>? RetainedCandidates = null,
    IReadOnlyList<ShadowCompositionProviderCorrespondence>? ProviderCorrespondences = null
);

/// <summary>Discovery facts are inspectable without creating a catalog work link.</summary>
public sealed record ShadowCompositionCandidate(
    Guid MusicBrainzWorkMbid,
    string Title,
    JsonElement OriginalEvidence,
    JsonElement WriterRoleAssertions,
    string Scope
);

public sealed record ShadowCompositionWorkLink(
    string EntityKey,
    Guid MusicBrainzWorkMbid,
    string Title,
    string CorrespondenceBasis,
    string SourceClaimId,
    string? EvidenceManifestSha256,
    JsonElement PrimaryEvidence,
    JsonElement PrimaryWriterClaims,
    JsonElement? ProviderWriterRoleAssertions,
    IdentityCandidateComparison MatchingDiagnostics,
    string Rationale,
    string CompetingIdentityAssessment,
    IReadOnlyList<string> RemainingGaps,
    ShadowCompositionProviderCorrespondence? ProviderEvidence = null
);
