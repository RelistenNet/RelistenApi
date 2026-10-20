namespace Relisten.Normalization.Batch;

/// <summary>
/// Reinterprets an existing file title through the complete current song policy.
/// The source result remains immutable. A replay adds a cited interpretation;
/// it cannot move audio, allocate a performance or turn a song mention into an
/// audio boundary. The caller authenticates the retained body and witness first.
/// </summary>
public sealed class RetainedFileTitleReplay(MusicRepertoireConsolidator resolver)
{
    public const string Version = "retained-file-title-replay-v2";

    /// <summary>
    /// Uses the original file's complete title, including titles whose old result
    /// had no song key. Replaying only previously proposed segments would retain
    /// the old dictionary's ambiguity after ordinary duplicate songs were joined.
    /// </summary>
    public ReplayedFileTitle Resolve(
        string scope,
        Guid artistUuid,
        Guid fileUuid,
        ShadowFileContents retained,
        MusicTitleWitness witness
    )
    {
        if (
            retained.Home.FileUuid != fileUuid
            || retained.Playback.FileUuid != fileUuid
            || retained.Home.SourceUuid != retained.Playback.SourceUuid
            || retained.Home.ShowUuid != retained.Playback.ShowUuid
            || retained.TitleInterpretation is { } title
                && (title.FileUuid != fileUuid || title.SourceUuid != retained.Home.SourceUuid)
        )
        {
            throw new InvalidDataException(
                "The retained title and playback have different file ownership."
            );
        }

        var original = retained.TitleInterpretation?.OriginalTitle;
        var correspondence =
            original == null
                ? null
                : ResolveTitle(
                    scope,
                    artistUuid,
                    original,
                    witness with
                    {
                        ValuePointer = "/proposal/titleInterpretation/originalTitle",
                    }
                );

        return new(
            scope,
            artistUuid,
            fileUuid,
            retained.Home.SourceUuid,
            retained.Home.ShowUuid,
            witness,
            correspondence,
            correspondence == null ? "missing_original_title" : null
        );
    }

    /// <summary>
    /// Resolves one authenticated literal title for an offline deduplicated replay.
    /// File membership stays in the export's separate ownership map. Title IDs used
    /// to deduplicate that export never become song or performance identities.
    /// </summary>
    public MusicRepertoireCorrespondence ResolveTitle(
        string scope,
        Guid artistUuid,
        string originalTitle,
        MusicTitleWitness witness
    ) =>
        resolver.Resolve(
            new(
                scope,
                artistUuid,
                ShadowRepertoireAdapter.ProvisionalEntityKey(artistUuid, originalTitle),
                originalTitle,
                "title_derived",
                "proposed",
                null,
                [],
                witness
            )
        );
}

/// <summary>
/// Compact file ownership and a new title interpretation. Playback URLs and the
/// original interpretation are read from OriginalWitness rather than copied into
/// a second catalog. Repeated target positions remain in Correspondence.Targets.
/// </summary>
public sealed record ReplayedFileTitle(
    string Scope,
    Guid ArtistUuid,
    Guid FileUuid,
    Guid? SourceUuid,
    Guid? ShowUuid,
    MusicTitleWitness OriginalWitness,
    MusicRepertoireCorrespondence? Correspondence,
    string? Reason
);
