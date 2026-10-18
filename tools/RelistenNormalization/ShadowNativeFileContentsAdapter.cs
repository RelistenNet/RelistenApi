using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Projects native files with the same title policy as catalog files and setlist passages.
/// Original file scope survives missing titles, ambiguous songs and missing catalog homes.
/// </summary>
public sealed class ShadowNativeFileContentsAdapter(
    ShadowProviderObservations observations,
    ShadowRepertoireAdapter? repertoire = null
) : IShadowCatalogAdapter
{
    private static readonly IReadOnlyList<string> Domains = ["file_contents"];
    private readonly ShadowRepertoireAdapter repertoire = repertoire ?? new();
    private readonly TrackTitleExtractor titleExtractor = new();

    public string Version =>
        "shadow-native-file-contents-v1/"
        + ShadowProviderObservations.Version
        + "/"
        + TrackTitleExtractor.Version
        + "/"
        + repertoire.Version;

    public IReadOnlyList<string> DomainsFor(string inputKind) =>
        inputKind == "provider_input" ? Domains : [];

    public void Observe(JsonElement record)
    {
        observations.Observe(record);
        repertoire.Observe(record);
    }

    public IEnumerable<ShadowCatalogResult> Process(ShadowCatalogInput input)
    {
        if (DomainsFor(input.Kind).Count == 0)
        {
            return [];
        }
        try
        {
            return [Project(input, observations.Read(input))];
        }
        catch (Exception error)
            when (error
                    is JsonException
                        or ArgumentException
                        or InvalidDataException
                        or NotSupportedException
                        or KeyNotFoundException
                        or InvalidOperationException
            )
        {
            return
            [
                ShadowCatalogResult.For(
                    input,
                    "file_contents",
                    "failed",
                    Version,
                    null,
                    new { input_revision_uuid = input.Uuid, error_type = error.GetType().Name },
                    error is NotSupportedException
                        ? "unsupported_provider_capture"
                        : "invalid_retained_provider_input"
                ),
            ];
        }
    }

    private ShadowCatalogResult Project(
        ShadowCatalogInput input,
        ShadowProviderObservationSet source
    )
    {
        // Share the retained document across all file field/span checks in this input.
        // Each citation still validates its original revision, pointer and exact quote.
        using var document = NormalizationRetainedDocument.Open(source.Input);
        var files = new List<ShadowNativeFileContents>();
        foreach (var recording in source.Recordings)
        {
            foreach (var file in recording.Recording.Files)
            {
                files.Add(ProjectFile(input, source, recording, file, files.Count, document));
            }
        }
        var incomplete =
            source.Decoded.Issues.Count > 0
            || source.Issues.Any(issue => issue.Code != "local_root_requires_directory_input");
        var outcome =
            files.Count == 0
                ? incomplete
                    ? "unresolved"
                    : "not_applicable"
                : files.Any(file => file.Outcome is "failed" or "unresolved")
                    ? "unresolved"
                    : files.All(file => file.Outcome == "not_applicable")
                        ? "not_applicable"
                        : "proposed";
        return ShadowCatalogResult.For(
            input,
            "file_contents",
            outcome,
            Version,
            new ShadowNativeFileContentsProposal(files, source.Decoded.Issues, source.Issues),
            new
            {
                source.Input.Uuid,
                source.Input.Resource,
                source.Input.Sha256,
                source.Input.CaptureVersion,
                source.Decoded.DecoderVersion,
                title_interpretation = "shared_title_and_repertoire_policy",
                audio_boundaries = "unknown",
            },
            files.Count == 0
                ? incomplete
                    ? "native_file_observations_incomplete"
                    : "provider_input_has_no_file_accounts"
                : outcome == "unresolved"
                    ? "one_or_more_native_files_need_review"
                    : null,
            files.SelectMany(file => file.EntityKeys).Distinct(StringComparer.Ordinal).ToArray()
        );
    }

    private ShadowNativeFileContents ProjectFile(
        ShadowCatalogInput input,
        ShadowProviderObservationSet source,
        ShadowProviderRecordingObservation recording,
        ProviderFileObservation file,
        int ordinal,
        NormalizationRetainedDocument document
    )
    {
        var key = ShadowCatalogIdentity.Key(
            "native_file",
            JsonSerializer.Serialize(
                new
                {
                    source.Input.Uuid,
                    recordingOrigin = recording.Recording.Origin,
                    fileOrigin = file.Origin,
                }
            )
        );
        var home = CatalogHome(recording, file);
        var title = InterpretTitle(input, document, recording.Recording.Origin, file);
        var issues = source
            .Decoded.Issues.Where(issue => WithinFile(file, issue.Evidence))
            .ToArray();
        var keys = new List<string> { key };
        keys.AddRange(
            title.Mentions.SelectMany(mention =>
                mention
                    .Interpretation.CandidateSongs.Select(song => song.EntityKey)
                    .Concat(
                        mention.Interpretation.Song == null
                            ? []
                            : new[] { mention.Interpretation.Song.EntityKey }
                    )
            )
        );
        if (home.SourceUuid is { } sourceId)
        {
            keys.Add("source:" + sourceId.ToString("D"));
        }
        if (home.ShowUuid is { } showId)
        {
            keys.Add("show:" + showId.ToString("D"));
        }
        if (home.File != null)
        {
            keys.Add("file:" + home.File.TrackUuid.ToString("D"));
            keys.Add("source_set:" + home.File.SourceSetUuid.ToString("D"));
        }
        return new(
            ordinal,
            key,
            source.Input.Uuid,
            recording.Recording.Origin,
            file,
            home.SourceUuid,
            home.ShowUuid,
            home.File?.TrackUuid,
            home.File?.SourceSetUuid,
            home.Reason,
            home.Playback,
            title.Extraction,
            title.Mentions,
            issues,
            title.Outcome,
            title.Reason,
            keys.Distinct(StringComparer.Ordinal).ToArray()
        );
    }

    private static FileCatalogHome CatalogHome(
        ShadowProviderRecordingObservation recording,
        ProviderFileObservation file
    )
    {
        var conflictingOwner =
            file.RecordingIdentity is { } owner
            && owner.Identity != recording.Recording.Origin.Item;
        var binding = conflictingOwner
            ? null
            : recording.CatalogFiles.SingleOrDefault(binding => binding.Origin == file.Origin);
        var sourceUuid = conflictingOwner ? null : recording.CatalogSourceUuid;
        var showUuid = conflictingOwner ? null : recording.CatalogShowUuid;
        var bindingReason = conflictingOwner
            ? "file_recording_identity_conflicts_with_enclosing_recording"
            : recording.CatalogBindingReason
                ?? (binding == null ? "native_file_has_no_exact_catalog_anchor" : null);
        var playback =
            binding == null
                ? null
                : new ShadowFilePlayback(
                    binding.TrackUuid,
                    binding.SourceUuid,
                    showUuid,
                    binding.MediaUrl
                );
        return new(sourceUuid, showUuid, binding, playback, bindingReason);
    }

    private TitleContent InterpretTitle(
        ShadowCatalogInput input,
        NormalizationRetainedDocument document,
        ProviderObservationOrigin recordingOrigin,
        ProviderFileObservation file
    )
    {
        TrackTitleExtraction? extraction = null;
        IReadOnlyList<ShadowNativeFileTitleMention> mentions = [];
        string outcome;
        string? reason;
        try
        {
            ValidateFile(document, recordingOrigin, file);
            if (file.Title == null)
            {
                outcome = "unresolved";
                reason = "missing_native_file_title";
            }
            else
            {
                ValidateTitleValue(document, file.Title);
                extraction = titleExtractor.Extract(input.Artist.Uuid, file.Title.Value);
                mentions = extraction
                    .Segments.Select(segment =>
                    {
                        var evidence = Remap(file.Title.Evidence, segment.Evidence);
                        document.ValidateEvidence(evidence);
                        return new ShadowNativeFileTitleMention(
                            repertoire.ResolveTitleMention(
                                input.Artist.Id,
                                input.Artist.Uuid,
                                segment
                            ),
                            evidence
                        );
                    })
                    .ToArray();
                outcome =
                    mentions.Count == 0
                    || !extraction.IsResolved
                    || mentions.Any(mention => mention.Interpretation.Outcome == "unresolved")
                        ? "unresolved"
                    : mentions.All(mention => mention.Interpretation.Outcome == "not_applicable")
                        ? "not_applicable"
                    : "proposed";
                reason =
                    outcome == "unresolved"
                        ? extraction.Reason ?? "one_or_more_title_mentions_need_review"
                        : null;
            }
        }
        catch (Exception error)
            when (error is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            // Keep the original file and its independently established playback association.
            // An uninterpretable title must not erase another file or the native inventory.
            extraction = null;
            mentions = [];
            outcome = "failed";
            reason = "invalid_native_file_evidence_or_title";
        }
        return new(extraction, mentions, outcome, reason);
    }

    private sealed record FileCatalogHome(
        Guid? SourceUuid,
        Guid? ShowUuid,
        ShadowProviderCatalogFile? File,
        ShadowFilePlayback? Playback,
        string? Reason
    );

    private sealed record TitleContent(
        TrackTitleExtraction? Extraction,
        IReadOnlyList<ShadowNativeFileTitleMention> Mentions,
        string Outcome,
        string? Reason
    );

    private static bool WithinFile(
        ProviderFileObservation file,
        NormalizedEvidenceReference evidence
    ) =>
        evidence.InputRevisionUuid == file.Origin.Evidence.InputRevisionUuid
        && (
            evidence.JsonPointer == file.Origin.Evidence.JsonPointer
            || evidence.JsonPointer.StartsWith(
                file.Origin.Evidence.JsonPointer + "/",
                StringComparison.Ordinal
            )
        );

    private static void ValidateTitleValue(
        NormalizationRetainedDocument document,
        ObservedValue<string> title
    )
    {
        var value = document.Resolve(title.Evidence.JsonPointer);
        var original =
            title.Evidence.Quote
            ?? (value.ValueKind == JsonValueKind.String ? value.GetString() : null);
        if (original != title.Value)
        {
            throw new InvalidDataException(
                "Native title must equal its exact cited original text."
            );
        }
    }

    private static NormalizedEvidenceReference Remap(
        NormalizedEvidenceReference title,
        NormalizationTextEvidence mention
    ) =>
        new(
            title.InputRevisionUuid,
            title.JsonPointer,
            (title.StartCodePoint ?? 0) + mention.StartCodePoint,
            (title.StartCodePoint ?? 0) + mention.EndCodePoint,
            mention.Quote
        );

    private static void ValidateFile(
        NormalizationRetainedDocument document,
        ProviderObservationOrigin recording,
        ProviderFileObservation file
    )
    {
        var evidence = new NormalizedEvidenceReference?[]
        {
            recording.Evidence,
            file.Origin.Evidence,
            file.RecordingIdentity?.Evidence,
            file.Filename?.Evidence,
            file.OriginalFilename?.Evidence,
            file.Title?.Evidence,
            file.PlaybackLocator?.Evidence,
            file.Position?.Evidence,
            file.SetLabel?.Evidence,
        }
            .OfType<NormalizedEvidenceReference>()
            .Concat(file.Identities.Select(identity => identity.Evidence))
            .Concat(file.SongIdentities.Select(identity => identity.Evidence))
            .Concat(file.Text.Select(text => text.Text.Evidence))
            .Concat(
                file.WorkCredits.SelectMany(credit =>
                    credit
                        .Identities.Select(identity => identity.Evidence)
                        .Prepend(credit.Name.Evidence)
                )
            );
        foreach (var reference in evidence)
        {
            document.ValidateEvidence(reference);
        }
    }
}
