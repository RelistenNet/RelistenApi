using System.Text;
using System.Text.RegularExpressions;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Applies the shared title/dictionary resolver to frozen scoped descriptors. All
/// context is observed before decisions, so input order cannot select a display
/// spelling or hide a later qualified-work contradiction. Results remain shadow
/// interpretations; original keys and their file relationships are never rewritten.
/// </summary>
public sealed class MusicRepertoireConsolidator
{
    public const string Version = "music-repertoire-consolidation-v2";
    private static readonly Regex CompoundSeparator = new(
        @"\s+(?:-|\+|&)\s+",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking
    );
    private readonly TrackTitleExtractor extractor = new();
    private readonly Dictionary<string, ShadowRepertoireAdapter> scopes = new(
        StringComparer.Ordinal
    );
    private readonly HashSet<(string Scope, string Key)> originals = [];
    private readonly Dictionary<(string Scope, string Key), PreferredTitle> preferred = [];
    private readonly Dictionary<(string Scope, string Key), HashSet<string>> works = [];
    private bool frozen;

    public long DescriptorCount => originals.Count;

    public void ObserveDictionary(MusicDictionaryName name)
    {
        RequireObservationPhase();
        var expectedKey = ShadowRepertoireAdapter.KnownEntityKey(name.LegacySongUuid);
        if (name.SongKey == null && name.Witness.DocumentKind == "catalog_context")
        {
            name = name with { SongKey = expectedKey };
        }
        if (name.SongKey != expectedKey)
        {
            throw new InvalidDataException(
                "A dictionary key does not describe its retained song UUID."
            );
        }
        Dictionary(name.Scope).ObserveDictionary(name);
        ObserveWorks(name.Scope, expectedKey, name.WorkKeys);
    }

    public void ObserveDescriptor(MusicRepertoireDescriptor descriptor)
    {
        RequireObservationPhase();
        if (!originals.Add((descriptor.Scope, descriptor.OriginalKey)))
        {
            throw new InvalidDataException("A descriptor bundle repeats an original scoped key.");
        }
        if (
            descriptor.LegacySongUuid is { } legacy
            && descriptor.OriginalKey != ShadowRepertoireAdapter.KnownEntityKey(legacy)
        )
        {
            throw new InvalidDataException(
                "A descriptor key does not describe its retained song UUID."
            );
        }
        ObserveWorks(descriptor.Scope, descriptor.OriginalKey, descriptor.WorkKeys);
        if (descriptor.Title == null || descriptor.LegacySongUuid != null)
        {
            return;
        }

        // Retain only compact display choices, not every extraction/proof graph.
        // An already existing base descriptor wins; other spellings sort ordinally.
        foreach (var segment in extractor.Extract(descriptor.ArtistUuid, descriptor.Title).Segments)
        {
            if (segment.Kind != TrackContentKind.Song)
            {
                continue;
            }
            RememberTitle(descriptor, segment.Title!);
            if (segment.CoreTitle is { } core)
            {
                RememberTitle(descriptor, core.Title);
            }
        }
    }

    public void CompleteObservation() => frozen = true;

    public MusicRepertoireCorrespondence Resolve(MusicRepertoireDescriptor descriptor)
    {
        if (!frozen)
        {
            throw new InvalidOperationException(
                "Observe the complete source population before resolving titles."
            );
        }
        if (descriptor.Title == null)
        {
            return Result("unresolved", "indexed_reference_without_title", "unknown", [], null);
        }

        var title = descriptor.Title;
        var dictionary = Dictionary(descriptor.Scope);
        var extraction = extractor.Extract(descriptor.ArtistUuid, title);
        if (descriptor.LegacySongUuid is { } legacy)
        {
            var candidate = dictionary.DictionaryIdentity(descriptor.ArtistUuid, legacy);
            var song =
                candidate?.Song
                ?? new ShadowRepertoireSong(
                    descriptor.OriginalKey,
                    descriptor.ArtistUuid,
                    legacy,
                    title,
                    descriptor.Basis
                );
            return Result(
                "proposed",
                "retained_dictionary_identity",
                "single",
                [
                    Target(
                        song,
                        MusicRepertoireRelation.IdentityAlias,
                        "proposed",
                        "retained_dictionary_identity",
                        Evidence(title, 0, title.Length),
                        extraction.Qualifiers,
                        candidate == null ? [] : [candidate]
                    ),
                ],
                extraction
            );
        }

        // Literal dictionary identities, including titles such as Tweezer Reprise,
        // precede syntax-derived base-title or compound interpretations.
        if (dictionary.DictionaryCandidates(descriptor.ArtistUuid, title).Count > 0)
        {
            var whole = dictionary.ResolveTitleMention(
                descriptor.ArtistUuid,
                Segment(title, 0, title.Length)
            );
            var targets = Interpret(
                whole,
                MusicRepertoireRelation.IdentityAlias,
                extraction.Qualifiers
            );
            return Finish(targets, "single", extraction);
        }

        var resolved = new List<MusicRepertoireTarget>();
        var sequence =
            extraction.Sequence == TrackTitleSequence.TitleOrder ? "source_title_order" : "single";
        var hasUnresolved = !extraction.IsResolved;
        var hasMusical = false;
        foreach (var segment in extraction.Segments)
        {
            var relation =
                extraction.Segments.Count > 1
                    ? MusicRepertoireRelation.CompoundMember
                    : MusicRepertoireRelation.IdentityAlias;
            if (
                dictionary
                    .DictionaryCandidates(
                        descriptor.ArtistUuid,
                        segment.Title ?? segment.Evidence.Quote
                    )
                    .Count > 0
            )
            {
                var retained = dictionary.ResolveTitleMention(descriptor.ArtistUuid, segment);
                resolved.AddRange(Interpret(retained, relation, extraction.Qualifiers));
                hasUnresolved |= retained.Outcome == "unresolved";
                hasMusical |= retained.Outcome != "not_applicable";
                continue;
            }
            if (segment.CoreTitle is { } core)
            {
                var interpreted = dictionary.ResolveTitleMention(
                    descriptor.ArtistUuid,
                    segment with
                    {
                        Title = core.Title,
                        Evidence = core.Evidence,
                        CoreTitle = null,
                    }
                );
                foreach (var variant in core.Variants)
                {
                    var qualifier = new TrackTitleQualifier(
                        "named_" + variant.Kind.ToString().ToLowerInvariant(),
                        variant.Evidence
                    );
                    resolved.AddRange(
                        Interpret(
                            interpreted,
                            Relation(variant.Kind),
                            [.. extraction.Qualifiers, qualifier]
                        )
                    );
                }
                hasMusical = true;
                hasUnresolved |= interpreted.Outcome == "unresolved";
                continue;
            }

            if (
                segment.Kind == TrackContentKind.Song
                && TryCompound(
                    segment,
                    dictionary,
                    descriptor,
                    extraction.Qualifiers,
                    out var compound
                )
            )
            {
                resolved.AddRange(compound);
                sequence = "inferred_title_membership";
                hasMusical = true;
                continue;
            }

            var interpretation = dictionary.ResolveTitleMention(
                descriptor.ArtistUuid,
                segment,
                extraction.Segments.Count == 1 ? title : null
            );
            resolved.AddRange(Interpret(interpretation, relation, extraction.Qualifiers));
            hasUnresolved |= interpretation.Outcome == "unresolved";
            hasMusical |= interpretation.Outcome != "not_applicable";
        }

        if (resolved.Count == 0)
        {
            return Result(
                hasMusical || hasUnresolved ? "unresolved" : "not_applicable",
                hasMusical || hasUnresolved ? "title_requires_interpretation" : "non_song_content",
                sequence,
                [],
                extraction
            );
        }
        return Finish(resolved, sequence, extraction, hasUnresolved);

        MusicRepertoireCorrespondence Result(
            string disposition,
            string reason,
            string order,
            IReadOnlyList<MusicRepertoireTarget> targets,
            TrackTitleExtraction? source
        ) =>
            new(
                descriptor.Scope,
                descriptor.ArtistUuid,
                descriptor.OriginalKey,
                descriptor.Title,
                disposition,
                reason,
                order,
                targets,
                descriptor.Witness,
                source
            );

        MusicRepertoireCorrespondence Finish(
            IReadOnlyList<MusicRepertoireTarget> targets,
            string order,
            TrackTitleExtraction source,
            bool incomplete = false
        )
        {
            var accepted = targets.Any(target => target.Disposition == "proposed");
            var review = incomplete || targets.Any(target => target.Disposition != "proposed");
            return Result(
                review ? "review"
                    : accepted ? "proposed"
                    : "unresolved",
                review ? "one_or_more_interpretations_need_review" : "shared_title_correspondence",
                order,
                targets,
                source
            );
        }

        IReadOnlyList<MusicRepertoireTarget> Interpret(
            ShadowRepertoireSegment interpretation,
            MusicRepertoireRelation relation,
            IReadOnlyList<TrackTitleQualifier> qualifiers
        )
        {
            if (interpretation.Song is { } song)
            {
                var conflict = WorkConflict(descriptor, song.EntityKey, relation);
                return
                [
                    Target(
                        song,
                        relation,
                        conflict ? "review" : "proposed",
                        conflict
                            ? "qualified_work_contradiction"
                            : interpretation.Reason ?? "shared_title_correspondence",
                        interpretation.Evidence,
                        qualifiers,
                        interpretation.ComparisonDiagnostics
                    ),
                ];
            }
            return interpretation
                .CandidateSongs.Select(song =>
                    Target(
                        song,
                        relation,
                        "review",
                        interpretation.Reason ?? "ambiguous_artist_dictionary",
                        interpretation.Evidence,
                        qualifiers,
                        interpretation.ComparisonDiagnostics
                    )
                )
                .ToArray();
        }

        MusicRepertoireTarget Target(
            ShadowRepertoireSong song,
            MusicRepertoireRelation relation,
            string disposition,
            string reason,
            NormalizationTextEvidence evidence,
            IReadOnlyList<TrackTitleQualifier> qualifiers,
            IReadOnlyList<ShadowRepertoireCandidate> comparisons
        )
        {
            if (
                song.LegacySongUuid == null
                && preferred.TryGetValue((descriptor.Scope, song.EntityKey), out var name)
            )
            {
                song = song with { Name = name.Name };
            }
            return new(song, relation, disposition, reason, evidence, qualifiers, comparisons);
        }
    }

    private bool TryCompound(
        TrackTitleSegment source,
        ShadowRepertoireAdapter dictionary,
        MusicRepertoireDescriptor descriptor,
        IReadOnlyList<TrackTitleQualifier> qualifiers,
        out IReadOnlyList<MusicRepertoireTarget> targets
    )
    {
        targets = [];
        var title = source.Title!;
        var separators = CompoundSeparator.Matches(title);
        if (separators.Count == 0)
        {
            return false;
        }
        var parts = new List<TrackTitleSegment>();
        var rawStart = descriptor
            .Title!.EnumerateRunes()
            .Take(source.Evidence.StartCodePoint)
            .Sum(rune => rune.Utf16SequenceLength);
        var start = 0;
        foreach (Match separator in separators)
        {
            parts.Add(Segment(descriptor.Title, rawStart + start, rawStart + separator.Index));
            start = separator.Index + separator.Length;
        }
        parts.Add(Segment(descriptor.Title, rawStart + start, rawStart + title.Length));
        // A recognizable separator alone supplies no identity. Every part must be
        // uniquely supported by the complete same-artist dictionary before we infer
        // multiple file contents. Unknown compounds never prefix-collapse to a song.
        var resolvedParts = parts
            .Select(part => dictionary.ResolveTitleMention(descriptor.ArtistUuid, part))
            .ToArray();
        if (
            resolvedParts.Any(part =>
                part.Outcome != "proposed" || part.Song?.LegacySongUuid == null
            )
        )
        {
            targets =
            [
                new(
                    new(
                        descriptor.OriginalKey,
                        descriptor.ArtistUuid,
                        null,
                        descriptor.Title,
                        "title_derived"
                    ),
                    MusicRepertoireRelation.CompoundMember,
                    "review",
                    "compound_has_unresolved_members",
                    source.Evidence,
                    qualifiers,
                    []
                ),
            ];
            return true;
        }
        targets = resolvedParts
            .Select(interpretation =>
            {
                var conflict = WorkConflict(
                    descriptor,
                    interpretation.Song!.EntityKey,
                    MusicRepertoireRelation.CompoundMember
                );
                return new MusicRepertoireTarget(
                    interpretation.Song,
                    MusicRepertoireRelation.CompoundMember,
                    conflict ? "review" : "proposed",
                    conflict
                        ? "qualified_work_contradiction"
                        : "inferred_dictionary_supported_compound",
                    interpretation.Evidence,
                    qualifiers,
                    interpretation.ComparisonDiagnostics
                );
            })
            .ToArray();
        return true;
    }

    private bool WorkConflict(
        MusicRepertoireDescriptor descriptor,
        string target,
        MusicRepertoireRelation relation
    )
    {
        if (
            !works.TryGetValue((descriptor.Scope, descriptor.OriginalKey), out var sourceWorks)
            || !works.TryGetValue((descriptor.Scope, target), out var targetWorks)
        )
        {
            return false;
        }
        return relation == MusicRepertoireRelation.CompoundMember
            ? !sourceWorks.Overlaps(targetWorks)
            : !sourceWorks.SetEquals(targetWorks);
    }

    private void RememberTitle(MusicRepertoireDescriptor descriptor, string title)
    {
        var key = ShadowRepertoireAdapter.ProvisionalEntityKey(descriptor.ArtistUuid, title);
        var candidate = new PreferredTitle(title, descriptor.OriginalKey == key ? 0 : 1);
        if (
            !preferred.TryGetValue((descriptor.Scope, key), out var existing)
            || candidate.Priority < existing.Priority
            || candidate.Priority == existing.Priority
                && StringComparer.Ordinal.Compare(candidate.Name, existing.Name) < 0
        )
        {
            preferred[(descriptor.Scope, key)] = candidate;
        }
    }

    private void ObserveWorks(string scope, string key, IReadOnlyList<string> workKeys)
    {
        if (workKeys.Count == 0)
        {
            return;
        }
        if (!works.TryGetValue((scope, key), out var identities))
        {
            identities = [];
            works.Add((scope, key), identities);
        }
        identities.UnionWith(workKeys);
    }

    private ShadowRepertoireAdapter Dictionary(string scope)
    {
        if (!scopes.TryGetValue(scope, out var dictionary))
        {
            dictionary = new();
            scopes.Add(scope, dictionary);
        }
        return dictionary;
    }

    private void RequireObservationPhase()
    {
        if (frozen)
        {
            throw new InvalidOperationException(
                "The observed music context is immutable during resolution."
            );
        }
    }

    private static TrackTitleSegment Segment(string title, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(title[start]))
        {
            start++;
        }
        while (end > start && char.IsWhiteSpace(title[end - 1]))
        {
            end--;
        }
        return new(
            1,
            TrackContentKind.Song,
            title[start..end],
            Evidence(title, start, end),
            TitleCoverage.Unknown,
            TitleTransition.Unknown
        );
    }

    private static NormalizationTextEvidence Evidence(string title, int start, int end) =>
        new(
            "title",
            "/title",
            title[start..end],
            title[..start].EnumerateRunes().Count(),
            title[..end].EnumerateRunes().Count()
        );

    private static MusicRepertoireRelation Relation(TrackTitleVariantKind kind) =>
        kind switch
        {
            TrackTitleVariantKind.Jam => MusicRepertoireRelation.Jam,
            TrackTitleVariantKind.Tease => MusicRepertoireRelation.Tease,
            TrackTitleVariantKind.Reprise => MusicRepertoireRelation.Reprise,
            TrackTitleVariantKind.Practice => MusicRepertoireRelation.Practice,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private sealed record PreferredTitle(string Name, int Priority);
}
