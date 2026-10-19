using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Maps retained batch facts to the core selector without making core depend on
/// tool contracts. The caller owns the registered job's spend, time and storage
/// limits. The returned inference never edits the supplied provider assertions.
/// </summary>
public sealed class CachedCompositionHypothesisSelector : ICompositionHypothesisSelector
{
    private readonly LunaCompositionHypothesisSelector selector;
    private readonly Guid jobUuid;
    private readonly bool retryFailed;

    public CachedCompositionHypothesisSelector(
        LunaCompositionHypothesisSelector selector,
        Guid jobUuid,
        bool retryFailed = false
    )
    {
        if (jobUuid == Guid.Empty)
        {
            throw new ArgumentException("A registered capped job is required.", nameof(jobUuid));
        }
        this.selector = selector;
        this.jobUuid = jobUuid;
        this.retryFailed = retryFailed;
    }

    public async Task<CompositionHypothesisSelection> SelectAsync(
        CompositionHypothesisSong song,
        IReadOnlyList<RetainedCompositionWork> suppliedCandidates,
        CancellationToken ct = default
    )
    {
        var input = ToInput(song, suppliedCandidates);
        var result = retryFailed
            ? await selector.RetryFailedAsync(jobUuid, input, ct)
            : await selector.SelectAsync(jobUuid, input, ct);
        return new(
            result.SelectedWorkMbid,
            result.AlternativeWorkMbids,
            result.State == "proposed" ? result.Outcome : result.State,
            result.Method,
            result.Rationale,
            result
        );
    }

    internal static CompositionHypothesisSelectionInput ToInput(
        CompositionHypothesisSong song,
        IReadOnlyList<RetainedCompositionWork> candidates
    )
    {
        if (song.ArtistUuid != song.Song.ArtistUuid)
        {
            throw new ArgumentException(
                "The song and performer must retain the same artist scope."
            );
        }
        return new(ToSong(song), candidates.Select(ToWork).ToArray());
    }

    private static CompositionSelectionSong ToSong(CompositionHypothesisSong song) =>
        new(
            song.Scope,
            song.ArtistUuid,
            song.ArtistName,
            song.Song.EntityKey,
            song.Song.Name,
            song.Song.Basis,
            song.Song.LegacySongUuid,
            song.DictionaryNames.Select(name => new CompositionSelectionDictionaryName(
                    name.InputKey,
                    name.JsonPointer,
                    name.Quote,
                    name.RetainedWitness is null ? null : Witness(name.RetainedWitness)
                ))
                .ToArray(),
            song.Relations.Select(relation => relation.ToString()).ToArray(),
            Witness(song.SourceWitness),
            song.OriginalMemberships,
            ArtistContext(song)
        );

    private static CompositionSelectionArtistContext? ArtistContext(CompositionHypothesisSong song)
    {
        if (song.ArtistContext is not { } context)
        {
            return null;
        }
        if (
            context.Scope != song.Scope
            || context.ArtistUuid != song.ArtistUuid
            || context.Name != song.ArtistName
            || song.ArtistContextManifestSha256 is null
            || context.Origin.Key.Scope != song.Scope
            || context.Origin.Key.Uuid != song.ArtistUuid
        )
        {
            throw new ArgumentException(
                "Artist display context must retain its exact scoped cell and manifest."
            );
        }
        return new(
            song.ArtistContextManifestSha256,
            context.Origin.CatalogueSha256,
            context.Origin.Kind,
            context.Origin.Table,
            context.Origin.Field,
            context.Scope,
            context.ArtistUuid,
            context.Name,
            context.MusicBrainzId
        );
    }

    private static CompositionSelectionWork ToWork(RetainedCompositionWork work) =>
        new(
            WorkMbid(work),
            work.EntityKey,
            work.Identity,
            work.Titles,
            work.Aliases,
            work.Iswcs,
            work.WriterRoles.Select(writer => new CompositionSelectionWriterAssertion(
                    writer.Role,
                    writer.Direction,
                    writer.TargetType,
                    writer.Artist,
                    writer.Evidence,
                    writer.SourceForm
                ))
                .ToArray(),
            work.LookupAssertions,
            work.SearchAssertions.Select(assertion => new CompositionSelectionSearchAssertion(
                    assertion.Page,
                    assertion.Hit
                ))
                .ToArray(),
            work.RecordingWorkRelationships.Select(
                    relationship => new CompositionSelectionRecordingRelationship(
                        relationship.Recording,
                        relationship.Work,
                        relationship.Relationship
                    )
                )
                .ToArray(),
            work.RecordingAssertions
        );

    private static Guid WorkMbid(RetainedCompositionWork work)
    {
        if (
            work.Identity.Identity
                is not { Provider: "musicbrainz.org", ResourceType: "work" } resource
            || !Guid.TryParseExact(resource.ProviderKey, "D", out var mbid)
            || mbid == Guid.Empty
        )
        {
            throw new ArgumentException(
                "Only supplied qualified MusicBrainz works can be selected."
            );
        }
        return mbid;
    }

    private static CompositionSelectionWitness Witness(MusicTitleWitness witness) =>
        new(
            witness.Run,
            witness.Chunk,
            witness.Line,
            witness.RecordId,
            witness.ValuePointer,
            witness.ChunkSha256,
            witness.DocumentKind
        );
}
