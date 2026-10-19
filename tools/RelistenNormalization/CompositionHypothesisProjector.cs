using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Retrieves global external works for a normalized artist-song target. The
/// existing matcher supplies diagnostics, not acceptance: external writer IDs
/// cannot fill its canonical AuthorUuids, and this retained pool is incomplete.
/// Existing supported composition links are deliberately outside this projection.
/// </summary>
public sealed class CompositionHypothesisProjector(RetainedCompositionCandidateIndex index)
{
    public const string Version = "composition-hypotheses-v1";
    private readonly IdentityCandidateMatcher matcher = new();

    public CompositionHypothesisResult Project(CompositionHypothesisSong song)
    {
        var titles = new[] { song.Song.Name }
            .Concat(song.DictionaryNames.Select(name => name.Quote))
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var titleKeys = titles
            .Select(ShadowCompositionAdapter.NormalizeTitle)
            .ToHashSet(StringComparer.Ordinal);
        var candidates = titles
            .SelectMany(index.Find)
            .DistinctBy(work => work.EntityKey)
            .OrderBy(work => work.EntityKey, StringComparer.Ordinal)
            .Select(work => Compare(titles, titleKeys, work))
            .ToArray();
        return new(
            song,
            candidates.Length > 0 ? "hypotheses" : "missing_retained_candidates",
            candidates.Length > 0
                ? "literal_title_or_explicit_alias_retrieval_requires_correspondence_evidence"
                : "no_literal_title_or_explicit_alias_in_the_retained_work_pool",
            candidates,
            Version
        );
    }

    private CompositionWorkHypothesis Compare(
        IReadOnlyList<string> titles,
        IReadOnlySet<string> titleKeys,
        RetainedCompositionWork work
    )
    {
        var matchingPrimary = work
            .Titles.Where(title =>
                titleKeys.Contains(ShadowCompositionAdapter.NormalizeTitle(title.Value))
            )
            .ToArray();
        var matchingAliases = work
            .Aliases.Where(alias =>
                alias.Name != null
                && titleKeys.Contains(ShadowCompositionAdapter.NormalizeTitle(alias.Name.Value))
            )
            .Select(alias => alias.Name!)
            .ToArray();
        var comparison = matcher.CompareCompositions(
            new CompositionMatchRecord(
                null,
                titles.Select(title => new IdentityMatchingName(title)).ToArray(),
                [],
                new HashSet<Guid>(),
                false
            ),
            new CompositionMatchRecord(
                null,
                work.Titles.Select(title => title.Value)
                    .Concat(
                        work.Aliases.Where(alias => alias.Name != null)
                            .Select(alias => alias.Name!.Value)
                    )
                    .Distinct(StringComparer.Ordinal)
                    .Select(title => new IdentityMatchingName(title))
                    .ToArray(),
                [
                    new MatchingExternalIdentity(
                        work.Identity.Identity.Provider,
                        work.Identity.Identity.ResourceType,
                        work.Identity.Identity.ProviderKey,
                        IdentifiesEntity: true
                    ),
                ],
                new HashSet<Guid>(),
                false
            )
        );
        return new(
            work.EntityKey,
            work.Identity,
            [.. matchingPrimary, .. matchingAliases],
            matchingPrimary.Length > 0
                ? "retained_literal_work_title"
                : "retained_explicit_work_alias",
            comparison
        );
    }

    /// <summary>
    /// An inference adapter may abstain or select supplied IDs. It cannot invent
    /// IDs, silently duplicate alternatives, or turn this check into acceptance.
    /// The inference method/receipt remains separately visible to the consumer.
    /// </summary>
    public static void ValidateSelection(
        CompositionHypothesisResult hypotheses,
        CompositionHypothesisSelection selection
    )
    {
        var supplied = hypotheses
            .Candidates.Select(candidate => Guid.Parse(candidate.WorkIdentity.Identity.ProviderKey))
            .ToHashSet();
        if (
            selection.SelectedWorkMbid is { } selected && !supplied.Contains(selected)
            || selection.AlternativeWorkMbids.Any(alternative => !supplied.Contains(alternative))
            || selection.AlternativeWorkMbids.Distinct().Count()
                != selection.AlternativeWorkMbids.Count
            || selection.SelectedWorkMbid is { } winner
                && selection.AlternativeWorkMbids.Contains(winner)
        )
        {
            throw new InvalidDataException(
                "Composition inference must retain only distinct supplied work IDs."
            );
        }
    }
}
