using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Applies direct reviewed account correspondences to an observed scoped repertoire.
/// Raw dictionary candidates remain evidence; a reviewed link never merges their UUIDs.
/// </summary>
public sealed class ReviewedRepertoireCorrespondences
{
    public const string Version = "reviewed-repertoire-correspondences-v1";
    private readonly ExpertKnowledgeResolver resolver;
    private readonly Dictionary<
        (string Scope, Guid Artist, string Key),
        ShadowRepertoireSong
    > songs = [];

    public ReviewedRepertoireCorrespondences(ExpertKnowledgeSnapshot snapshot)
    {
        resolver = new(snapshot);
    }

    public static string StableKey(string scope, string entityKey) =>
        JsonSerializer.Serialize(new[] { scope, entityKey });

    public void Observe(string scope, ShadowRepertoireSong song) =>
        songs[(scope, song.ArtistUuid, song.EntityKey)] = song;

    public ShadowRepertoireSegment Apply(string scope, Guid artist, ShadowRepertoireSegment segment)
    {
        IReadOnlyList<ShadowRepertoireSong> originals = segment.Song is { } song
            ? new[] { song }
            : segment.CandidateSongs;
        if (originals.Count == 0)
        {
            return segment;
        }

        var mapped = new List<ShadowRepertoireSong>();
        var evaluations = new List<ExpertKnowledgeEvaluation>();
        var blocked = false;
        foreach (var original in originals)
        {
            var evaluation = resolver.Evaluate(
                new("songs", "repertoire", StableKey(scope, original.EntityKey), ArtistUuid: artist)
            );
            if (evaluation.AppliedRules.Count == 0)
            {
                mapped.Add(original);
                continue;
            }
            evaluations.Add(evaluation);
            if (
                evaluation.Outcome == "reviewed_correspondence"
                && TryTarget(evaluation.Target!, scope, artist, out var target)
            )
            {
                mapped.Add(target!);
            }
            else
            {
                // A rejection is a veto of its named target, not permission to choose
                // a rival. Missing targets and conflicting reviews need another review.
                mapped.Add(original);
                blocked |=
                    evaluation.Outcome == "conflict"
                    || evaluation.Outcome == "reviewed_correspondence"
                    || evaluation.ExcludedTargets.Any(excluded =>
                        excluded.StableKey == StableKey(scope, original.EntityKey)
                    );
            }
        }
        if (evaluations.Count == 0)
        {
            return segment;
        }

        var distinct = mapped.DistinctBy(value => value.EntityKey).ToArray();
        var unique = !blocked && distinct.Length == 1;
        return segment with
        {
            Outcome = unique ? "proposed" : "unresolved",
            Reason = unique
                ? "reviewed_artist_song_correspondence"
                : "reviewed_song_correspondence_requires_review",
            Song = unique ? distinct[0] : null,
            CandidateSongs = distinct,
            Knowledge = evaluations,
        };
    }

    private bool TryTarget(
        ExpertKnowledgeTarget target,
        string scope,
        Guid artist,
        out ShadowRepertoireSong? song
    )
    {
        song = null;
        if (
            target.Domain != "songs"
            || target.Namespace != "repertoire"
            || target.CanonicalUuid != null
        )
        {
            return false;
        }
        string[]? key;
        try
        {
            key = JsonSerializer.Deserialize<string[]>(target.StableKey);
        }
        catch (JsonException)
        {
            return false;
        }
        return key is { Length: 2 }
            && key[0] == scope
            && songs.TryGetValue((scope, artist, key[1]), out song);
    }
}
