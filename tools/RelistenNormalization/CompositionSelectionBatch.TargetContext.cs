using System.Text;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

public sealed partial class CompositionSelectionBatch
{
    private const long MaximumContextBytes = 512L * 1024 * 1024;
    private const long MaximumContextManifestBytes = 16L * 1024 * 1024;

    /// <summary>
    /// The source exporter owns original-key membership joins and literal source
    /// extraction. This phase authenticates that frozen export, its source generation
    /// and exact target population before hydrating any model request. It never
    /// substitutes a newly read catalog head or a web adjudication for original facts.
    /// </summary>
    private static async Task<Dictionary<
        string,
        CompositionSelectionTargetContext
    >?> ReadTargetContextsAsync(
        CompositionSelectionPlan plan,
        CompositionHypothesisOutput hypotheses,
        Dictionary<string, string> pins,
        CancellationToken ct
    )
    {
        if (plan.TargetContextManifest is not { } artifact)
        {
            return null;
        }
        if (artifact.Bytes > MaximumContextManifestBytes)
        {
            throw new InvalidDataException("Target-context manifest exceeds its byte bound.");
        }
        await PinAsync(artifact, pins, ct);
        var manifest = JsonSerializer.Deserialize<CompositionSelectionTargetContextManifest>(
            await File.ReadAllTextAsync(artifact.Path, ct),
            Json
        )!;
        if (
            manifest.Version != "composition-target-context-v1"
            || manifest.State != "completed"
            || !manifest.Complete
            || manifest.TargetCount != plan.Songs.Count
            || manifest.HypothesisManifest != plan.HypothesisManifest
            || manifest.MusicInputManifest != hypotheses.Inputs.MusicInputManifest
            || manifest.MusicOutputManifest != hypotheses.Inputs.MusicOutputManifest
        )
        {
            throw new InvalidDataException(
                "Target context belongs to a different source generation or target population."
            );
        }

        await AuthenticateContextSourcesAsync(manifest, plan, pins, ct);
        var root = Path.GetDirectoryName(Path.GetFullPath(artifact.Path))!;
        var textsPath = ArtifactPath(root, manifest.Texts.Path);
        var targetsPath = ArtifactPath(root, manifest.Targets.Path);
        await PinAsync(manifest.Texts with { Path = textsPath }, pins, ct);
        await PinAsync(manifest.Targets with { Path = targetsPath }, pins, ct);
        if (manifest.Texts.Bytes + manifest.Targets.Bytes > MaximumContextBytes)
        {
            throw new InvalidDataException("Target-context export exceeds its byte bound.");
        }

        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        long decodedBytes = 0;
        long textRows = 0;
        await foreach (
            var line in ShadowJsonLines.ReadTextAsync(
                textsPath,
                MaximumLineCharacters,
                MaximumContextBytes,
                ct
            )
        )
        {
            ChargeContextBytes(line, ref decodedBytes);
            var text = JsonSerializer.Deserialize<TargetTextValue>(line, Json)!;
            if (
                Hash(Encoding.UTF8.GetBytes(text.Value)) != text.Sha256
                || !texts.TryAdd(text.Sha256, text.Value)
            )
            {
                throw new InvalidDataException(
                    "Target text is changed or duplicated in its literal pool."
                );
            }
            textRows++;
        }
        if (textRows != manifest.Texts.Rows)
        {
            throw new InvalidDataException("Target text rows differ from their manifest.");
        }

        var selected = plan.Songs.Select(Key).ToHashSet(StringComparer.Ordinal);
        var chunks = manifest.WitnessChunks.ToDictionary(
            chunk => Path.GetFullPath(chunk.Path),
            StringComparer.Ordinal
        );
        var contexts = new Dictionary<string, CompositionSelectionTargetContext>(
            StringComparer.Ordinal
        );
        await foreach (
            var line in ShadowJsonLines.ReadTextAsync(
                targetsPath,
                MaximumLineCharacters,
                MaximumContextBytes,
                ct
            )
        )
        {
            ChargeContextBytes(line, ref decodedBytes);
            var target = JsonSerializer.Deserialize<TargetContextRow>(line, Json)!;
            var key = Key(
                new CompositionSelectionSongKey(
                    target.Key.Scope,
                    target.Key.ArtistUuid,
                    target.Key.SongEntityKey
                )
            );
            if (!selected.Contains(key) || contexts.ContainsKey(key))
            {
                throw new InvalidDataException(
                    "Target context must cover each exact scoped song once."
                );
            }
            var witnesses = target
                .Witnesses.Select(witness => HydrateWitness(witness, texts))
                .ToArray();
            // Only this target's facts enter the request. The global export pin
            // remains in the plan/receipt so adding another target does not bust its cache.
            var context = new CompositionSelectionTargetContext(
                target.Key,
                target.Artist,
                target.Coverage,
                witnesses
            );
            foreach (var origin in ContextOrigins(context))
            {
                RequirePinnedContextOrigin(origin, chunks);
            }
            contexts.Add(key, context);
        }
        if (contexts.Count != manifest.Targets.Rows || !selected.SetEquals(contexts.Keys))
        {
            throw new InvalidDataException(
                "Target context is incomplete; missing rows cannot become empty context."
            );
        }
        return contexts;
    }

    private static async Task AuthenticateContextSourcesAsync(
        CompositionSelectionTargetContextManifest manifest,
        CompositionSelectionPlan plan,
        Dictionary<string, string> pins,
        CancellationToken ct
    )
    {
        // The context pins the original base plan, while the new plan pins this
        // context. Comparing target sets avoids a circular enriched-plan hash.
        if (manifest.SelectionPlan.Bytes > MaximumPlanBytes)
        {
            throw new InvalidDataException(
                "The original context selection plan exceeds its byte bound."
            );
        }
        foreach (
            var artifact in new[]
            {
                manifest.SelectionPlan,
                manifest.MusicInputManifest,
                manifest.MusicOutputManifest,
                manifest.Catalogue,
                manifest.Normalization,
            }
        )
        {
            await PinAsync(artifact, pins, ct);
        }
        var original = JsonSerializer.Deserialize<CompositionSelectionPlan>(
            await File.ReadAllTextAsync(manifest.SelectionPlan.Path, ct),
            Json
        )!;
        ValidatePlan(original);
        if (
            original.TargetContextManifest is not null
            || original.JobUuid == plan.JobUuid
            || original.HypothesisManifest != plan.HypothesisManifest
            || !original
                .Songs.Select(Key)
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(plan.Songs.Select(Key))
        )
        {
            throw new InvalidDataException(
                "Target context must retain the original plan's complete scoped targets."
            );
        }
        var music = JsonSerializer.Deserialize<MusicConsolidationInput>(
            await File.ReadAllTextAsync(manifest.MusicInputManifest.Path, ct),
            Json
        )!;
        if (
            manifest.Catalogue.Path != music.Catalogue.Path
            || manifest.Catalogue.Bytes != music.Catalogue.Bytes
            || manifest.Catalogue.Sha256 != music.Catalogue.Sha256
        )
        {
            throw new InvalidDataException(
                "Target context catalogue differs from the original music image."
            );
        }
        foreach (var artifact in manifest.SourceManifests.Concat(manifest.WitnessChunks))
        {
            await PinAsync(
                new(artifact.Path, artifact.Bytes, artifact.Sha256, artifact.Rows),
                pins,
                ct
            );
        }
    }

    private static CompositionSelectionTargetWitness HydrateWitness(
        TargetWitnessReference witness,
        IReadOnlyDictionary<string, string> texts
    ) =>
        new(
            witness.Kind,
            witness.Origin,
            witness.FileUuid,
            witness.SourceUuid,
            witness.ShowUuid,
            witness.NativeFileKey,
            witness.RecordingKey,
            witness.SetlistKey,
            witness.Ordered,
            witness.MatchingRepertoireKeys,
            witness
                .Texts.Select(text => new CompositionSelectionTargetText(
                    text.Field,
                    text.TextSha256,
                    texts.TryGetValue(text.TextSha256, out var value)
                        ? value
                        : throw new InvalidDataException(
                            "A target source text is absent from its literal pool."
                        ),
                    text.Origin,
                    text.Format
                ))
                .ToArray(),
            witness.Claims,
            witness.MissingFields
        );

    private static IEnumerable<CompositionSelectionTargetOrigin> ContextOrigins(
        CompositionSelectionTargetContext context
    )
    {
        foreach (var observation in context.Artist.Observations)
        {
            yield return observation.Origin;
        }
        foreach (var witness in context.Witnesses)
        {
            yield return witness.Origin;
            foreach (var text in witness.Texts)
            {
                yield return text.Origin;
            }
            foreach (var claim in witness.Claims)
            {
                yield return claim.Origin;
            }
        }
    }

    private static void RequirePinnedContextOrigin(
        CompositionSelectionTargetOrigin origin,
        IReadOnlyDictionary<string, CompositionSelectionContextSourceArtifact> chunks
    )
    {
        var root = Path.GetFullPath(origin.Run);
        var path = ArtifactPath(root, origin.Chunk);
        if (
            origin.Run != root
            || !chunks.TryGetValue(path, out var chunk)
            || chunk.Run != root
            || chunk.Sha256 != origin.Sha256
            || origin.Line < 1
            || origin.Line > chunk.Rows
        )
        {
            throw new InvalidDataException(
                "Target fact provenance is outside the pinned original source chunks."
            );
        }
    }

    private static void ChargeContextBytes(string line, ref long bytes)
    {
        bytes += Encoding.UTF8.GetByteCount(line) + 1L;
        if (bytes > MaximumContextBytes)
        {
            throw new InvalidDataException(
                "Complete target context exceeds its decoded byte bound; nothing was truncated."
            );
        }
    }

    private sealed record TargetTextValue(string Sha256, string Value);

    private sealed record TargetContextRow(
        CompositionSelectionTargetKey Key,
        CompositionSelectionTargetArtist Artist,
        CompositionSelectionTargetCoverage Coverage,
        IReadOnlyList<TargetWitnessReference> Witnesses
    );

    private sealed record TargetTextReference(
        string Field,
        string TextSha256,
        CompositionSelectionTargetOrigin Origin,
        string? Format
    );

    private sealed record TargetWitnessReference(
        string Kind,
        CompositionSelectionTargetOrigin Origin,
        Guid? FileUuid,
        Guid? SourceUuid,
        Guid? ShowUuid,
        string? NativeFileKey,
        string? RecordingKey,
        string? SetlistKey,
        bool? Ordered,
        IReadOnlyList<string> MatchingRepertoireKeys,
        IReadOnlyList<TargetTextReference> Texts,
        IReadOnlyList<CompositionSelectionTargetClaim> Claims,
        IReadOnlyList<string> MissingFields
    );
}
