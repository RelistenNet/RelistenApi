using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Projects one incomplete retained work pool against a complete, frozen music
/// correspondence image. Original memberships stay in that image; this batch
/// emits hypotheses once per scoped target and never publishes identities.
/// </summary>
public sealed class CompositionHypothesisBatch
{
    public const string RequestVersion = "composition-hypothesis-input-v1";
    public const string Version = "composition-hypothesis-output-v1";
    private const long MaximumRows = 2_000_000;
    private const int MaximumMusicLine = 32 * 1024 * 1024;
    private const int MaximumProviderLine = 48 * 1024 * 1024;
    private const long MaximumDecodedCharacters = 64L * 1024 * 1024 * 1024;
    private const long MaximumOutputBytes = 4L * 1024 * 1024 * 1024;
    private const long MinimumFreeBytes = 11L * 1024 * 1024 * 1024 / 2;
    private const int CheckpointRows = 2000;
    private static readonly JsonSerializerOptions ProviderJson = new(JsonSerializerDefaults.Web);

    public async Task<CompositionHypothesisOutput> RunAsync(
        string requestPath,
        string outputDirectory,
        string requestSha256,
        bool resume = false,
        CancellationToken ct = default
    )
    {
        requestPath = Path.GetFullPath(requestPath);
        outputDirectory = Path.GetFullPath(outputDirectory);
        var prepared = await AuthenticateAsync(requestPath, requestSha256, outputDirectory, ct);
        var budget = prepared.Budget;
        Console.Error.WriteLine("Composition hypotheses: authenticated retained inputs.");
        // Reconciliation retains temporary descriptor state. Release it before
        // loading the external work graph; the two phases do not depend on each other.
        var songs = await ReadSongsAsync(prepared, ct);
        Console.Error.WriteLine(
            $"Composition hypotheses: reconciled {songs.Length} songs; managed bytes {GC.GetTotalMemory(false)}."
        );
        var index = await ObserveProvidersAsync(prepared.Request, ct);
        Console.Error.WriteLine(
            $"Composition hypotheses: indexed {index.Works.Count} works; managed bytes {GC.GetTotalMemory(false)}."
        );
        var projector = new CompositionHypothesisProjector(index);
        var methods = new Dictionary<string, string>
        {
            [nameof(CompositionHypothesisBatch)] = Version,
            [nameof(NormalizationProviderDecoder)] = NormalizationProviderDecoder.Version,
            [nameof(RetainedCompositionCandidateIndex)] = RetainedCompositionCandidateIndex.Version,
            [nameof(CompositionHypothesisProjector)] = CompositionHypothesisProjector.Version,
        };

        // Rebuild deterministic expectations before trusting a saved prefix. A
        // checkpoint alone cannot prove that its acknowledged gzip rows survived.
        var expected = new Dictionary<Sha256Digest, Sha256Digest>();
        foreach (var result in Results(index, songs, projector))
        {
            ct.ThrowIfCancellationRequested();
            if (
                !expected.TryAdd(ResultKey(result.Domain, result.InputKey), Measure(result, out _))
                || expected.Count > MaximumRows
            )
            {
                throw new InvalidDataException("A hypothesis output repeats a logical key.");
            }
        }
        var completion = Path.Combine(outputDirectory, "manifest.json");
        if (resume && File.Exists(completion))
        {
            var output = await ReadCompletedAsync(completion, requestSha256, prepared.Request, ct);
            await using var saved = new ShadowCatalogRun(
                outputDirectory,
                requestSha256,
                methods,
                true
            );
            if (saved.CompletedLine != expected.Count || saved.HasFailures)
            {
                throw new InvalidDataException(
                    "Completed output lacks its complete matching checkpoint."
                );
            }
            await RequireCoverageAsync(outputDirectory, expected, expected.Keys.ToHashSet(), ct);
            var actual = await DescribeOutputAsync(
                outputDirectory,
                requestSha256,
                prepared.Request,
                index,
                songs,
                projector,
                ct
            );
            if (
                JsonSerializer.Serialize(output, ShadowCatalogRun.Json)
                != JsonSerializer.Serialize(actual, ShadowCatalogRun.Json)
            )
            {
                throw new InvalidDataException(
                    "The completion manifest differs from its retained result image."
                );
            }
            return output;
        }

        var retainedBytes = budget.CheckResources();
        long pendingBytes = 0;
        await using var run = new ShadowCatalogRun(outputDirectory, requestSha256, methods, resume);
        var completed = run.CompletedLine;
        if (completed < 0 || completed > expected.Count)
        {
            throw new InvalidDataException("The checkpoint exceeds the frozen hypothesis image.");
        }
        if (resume)
        {
            var acknowledged = Results(index, songs, projector)
                .Take(checked((int)completed))
                .Select(result => ResultKey(result.Domain, result.InputKey))
                .ToHashSet();
            await RequireCoverageAsync(outputDirectory, expected, acknowledged, ct);
        }

        try
        {
            long line = 0;
            foreach (var result in Results(index, songs, projector))
            {
                ct.ThrowIfCancellationRequested();
                if (++line <= completed)
                {
                    continue;
                }
                Measure(result, out var bytes);
                budget.RequireFreeSpace(bytes);
                budget.RequireOutputBudget(retainedBytes, pendingBytes + bytes);
                await run.WriteInputAsync([result]);
                pendingBytes += bytes;
                completed = line;
                if (line % CheckpointRows == 0)
                {
                    await run.SaveAsync(line, "running", Population(index, songs), ct);
                    retainedBytes = budget.CheckResources();
                    pendingBytes = 0;
                }
            }
            await AuthenticateAsync(requestPath, requestSha256, outputDirectory, ct);
            await run.SaveAsync(completed, "completed", Population(index, songs), ct);
            retainedBytes = budget.CheckResources();
            await RequireCoverageAsync(outputDirectory, expected, expected.Keys.ToHashSet(), ct);
            var output = await DescribeOutputAsync(
                outputDirectory,
                requestSha256,
                prepared.Request,
                index,
                songs,
                projector,
                ct
            );
            // Completion is the last durable write. Interrupted runs retain their
            // proof and checkpoint, but never advertise a complete candidate image.
            using var manifestSize = new ResultSizeStream(16 * 1024 * 1024);
            JsonSerializer.Serialize(manifestSize, output, ShadowCatalogRun.Json);
            budget.RequireFreeSpace(manifestSize.Length);
            budget.RequireOutputBudget(retainedBytes, manifestSize.Length);
            await File.WriteAllBytesAsync(
                completion + ".writing",
                JsonSerializer.SerializeToUtf8Bytes(output, ShadowCatalogRun.Json),
                ct
            );
            File.Move(completion + ".writing", completion);
            return output;
        }
        catch
        {
            using var audit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await run.SaveAsync(
                    completed,
                    "interrupted",
                    Population(index, songs),
                    audit.Token
                );
            }
            catch { }
            throw;
        }
    }

    private static async Task<Prepared> AuthenticateAsync(
        string path,
        string sha,
        string outputDirectory,
        CancellationToken ct
    )
    {
        var bytes = await ReadMetadataAsync(path, ct);
        if (Digest(bytes) != sha)
        {
            throw new InvalidDataException("The hypothesis request differs from its external pin.");
        }
        var request =
            JsonSerializer.Deserialize<CompositionHypothesisBatchRequest>(
                bytes,
                ShadowCatalogRun.Json
            ) ?? throw new InvalidDataException("A hypothesis request is required.");
        if (
            request.Version != RequestVersion
            || request.RetainedProviderInputs.Count
                > RetainedCompositionCandidateIndex.MaximumInputs
        )
        {
            throw new InvalidDataException(
                "The hypothesis request version or population is invalid."
            );
        }
        // Refuse an impossible run before hashing or decoding the larger retained
        // music/provider images, and before creating an output directory.
        var budget = new ResourceBudget(request.Limits, outputDirectory);
        budget.CheckResources();
        var directory = Path.GetDirectoryName(path)!;
        request = request with
        {
            MusicInputManifest = Resolve(request.MusicInputManifest, directory),
            MusicOutputManifest = Resolve(request.MusicOutputManifest, directory),
            ArtistContextManifest = request.ArtistContextManifest is { } context
                ? Resolve(context, directory)
                : null,
            RetainedProviderInputs = request
                .RetainedProviderInputs.Select(input =>
                    input with
                    {
                        Path = Path.GetFullPath(input.Path, directory),
                    }
                )
                .ToArray(),
        };
        await RequireFileAsync(request.MusicInputManifest, ct);
        await RequireFileAsync(request.MusicOutputManifest, ct);
        var music =
            JsonSerializer.Deserialize<MusicConsolidationInput>(
                await ReadMetadataAsync(request.MusicInputManifest.Path, ct),
                ShadowCatalogRun.Json
            ) ?? throw new InvalidDataException("A completed music input is required.");
        var output =
            JsonSerializer.Deserialize<MusicConsolidationOutput>(
                await ReadMetadataAsync(request.MusicOutputManifest.Path, ct),
                ShadowCatalogRun.Json
            ) ?? throw new InvalidDataException("A completed music output is required.");
        if (
            music.Version != "music-consolidation-input-v1"
            || music.State != "completed"
            || !output.Complete
            || output.Version != MusicRepertoireConsolidationBatch.Version
            || output.InputManifestSha256 != request.MusicInputManifest.Sha256
            || output.ExtractorVersion != TrackTitleExtractor.Version
            || output.ResolverVersion != MusicRepertoireConsolidator.Version
            || music.Descriptors.Rows is < 0 or > MaximumRows
            || music.DictionaryNames.Rows is < 0 or > MaximumRows
            || output.LogicalRows != music.Descriptors.Rows
        )
        {
            throw new InvalidDataException(
                "Hypotheses require the complete current music correspondence image."
            );
        }
        var inputDirectory = Path.GetDirectoryName(request.MusicInputManifest.Path)!;
        music = music with
        {
            Descriptors = Resolve(music.Descriptors, inputDirectory),
            DictionaryNames = Resolve(music.DictionaryNames, inputDirectory),
        };
        await RequireFileAsync(music.Descriptors, ct);
        await RequireFileAsync(music.DictionaryNames, ct);
        // The externally pinned completed input manifest is the provenance link
        // to original witness/catalogue bytes. Their export already validated
        // them; rebuilding hypotheses does not require a live upstream catalogue.
        var musicOutputDirectory = Path.GetDirectoryName(request.MusicOutputManifest.Path)!;
        output = output with
        {
            Chunks = output.Chunks.Select(chunk => Resolve(chunk, musicOutputDirectory)).ToArray(),
            Progress = Resolve(output.Progress, musicOutputDirectory),
        };
        if (
            output.Chunks.Select(chunk => chunk.Path).Distinct(StringComparer.Ordinal).Count()
            != output.Chunks.Count
        )
        {
            throw new InvalidDataException("The music manifest repeats a chunk artifact.");
        }
        foreach (var artifact in output.Chunks.Append(output.Progress))
        {
            await RequireFileAsync(artifact, ct);
        }
        foreach (var artifact in request.RetainedProviderInputs)
        {
            if (
                artifact.Rows is < 0 or > MaximumRows
                || artifact.Format is not ("provider_wrappers_jsonl" or "input_revision_json")
                || artifact.Format == "input_revision_json" && artifact.Rows != 1
            )
            {
                throw new InvalidDataException(
                    "A retained provider artifact has an invalid format or row count."
                );
            }
            await RequireFileAsync(
                new(artifact.Path, artifact.Bytes, artifact.Sha256, artifact.Rows),
                ct
            );
        }
        var artists = await ReadArtistsAsync(request, music, ct);
        return new(request, music, output, artists, budget);
    }

    private static async Task<
        Dictionary<(string Scope, Guid Artist), CompositionArtistContext>
    > ReadArtistsAsync(
        CompositionHypothesisBatchRequest request,
        MusicConsolidationInput music,
        CancellationToken ct
    )
    {
        var artists = new Dictionary<(string Scope, Guid Artist), CompositionArtistContext>();
        if (request.ArtistContextManifest is not { } artifact)
        {
            return artists;
        }
        await RequireFileAsync(artifact, ct);
        var manifest =
            JsonSerializer.Deserialize<CompositionArtistContextManifest>(
                await ReadMetadataAsync(artifact.Path, ct),
                ShadowCatalogRun.Json
            ) ?? throw new InvalidDataException("An artist display context manifest is required.");
        var directory = Path.GetDirectoryName(artifact.Path)!;
        if (
            manifest.Version != "composition-artist-context-v1"
            || manifest.State != "completed"
            || manifest.NamesAreAuthorshipEvidence
            || !SameFile(
                Resolve(manifest.MusicInputManifest, directory),
                request.MusicInputManifest
            )
            || !SameFile(
                Resolve(manifest.MusicOutputManifest, directory),
                request.MusicOutputManifest
            )
            || manifest.Catalogue.Sha256 != music.Catalogue.Sha256
            || manifest.Artists.Bytes is < 0 or > 16 * 1024 * 1024
            || manifest.Artists.Rows is < 0 or > 100_000
        )
        {
            throw new InvalidDataException(
                "Artist names must retain the exact frozen music provenance."
            );
        }
        var names = Resolve(manifest.Artists, directory);
        await RequireFileAsync(names, ct);
        var scopes = music.Sources.Select(source => source.Scope).ToHashSet(StringComparer.Ordinal);
        long rows = 0;
        long decodedBytes = 0;
        await foreach (
            var line in ShadowJsonLines.ReadTextAsync(names.Path, 1024 * 1024, 16 * 1024 * 1024, ct)
        )
        {
            decodedBytes += Encoding.UTF8.GetByteCount(line) + 1;
            if (++rows > names.Rows || decodedBytes > 16 * 1024 * 1024)
            {
                throw new InvalidDataException(
                    "The artist display context exceeds its census or byte bound."
                );
            }
            var artist =
                JsonSerializer.Deserialize<CompositionArtistContext>(line, ShadowCatalogRun.Json)
                ?? throw new InvalidDataException("An artist display context row is null.");
            if (
                !scopes.Contains(artist.Scope)
                || artist.MusicBrainzId != null
                || artist.Origin
                    is not {
                        Kind: "derived_catalogue_cell",
                        Table: "artists",
                        Field: "name"
                    } origin
                || origin.Key != new CompositionArtistContextKey(artist.Scope, artist.ArtistUuid)
                || origin.CatalogueSha256 != music.Catalogue.Sha256
            )
            {
                throw new InvalidDataException(
                    "An artist name changes its original catalogue cell provenance."
                );
            }
            var key = (artist.Scope, artist.ArtistUuid);
            if (artists.TryGetValue(key, out var previous) && previous != artist)
            {
                throw new InvalidDataException("A scoped artist has conflicting display context.");
            }
            artists[key] = artist;
        }
        if (rows != names.Rows)
        {
            throw new InvalidDataException("The artist display context population is incomplete.");
        }
        return artists;
    }

    private static async Task<RetainedCompositionCandidateIndex> ObserveProvidersAsync(
        CompositionHypothesisBatchRequest request,
        CancellationToken ct
    )
    {
        var index = new RetainedCompositionCandidateIndex();
        long total = 0;
        long decodedCharacters = 0;
        foreach (var artifact in request.RetainedProviderInputs)
        {
            long rows = 0;
            if (artifact.Format == "input_revision_json")
            {
                decodedCharacters += artifact.Bytes;
                if (
                    artifact.Bytes > MaximumProviderLine
                    || decodedCharacters > MaximumDecodedCharacters
                )
                {
                    throw new InvalidDataException("A retained provider revision exceeds 48 MiB.");
                }
                var input =
                    JsonSerializer.Deserialize<NormalizationInputRevision>(
                        await ReadBoundedBytesAsync(artifact.Path, MaximumProviderLine, ct),
                        ProviderJson
                    ) ?? throw new InvalidDataException("A retained provider revision is null.");
                index.Observe(input);
                rows = 1;
            }
            else
            {
                await foreach (
                    var line in ShadowJsonLines.ReadTextAsync(
                        artifact.Path,
                        MaximumProviderLine,
                        MaximumDecodedCharacters,
                        ct
                    )
                )
                {
                    decodedCharacters += line.Length + 1;
                    if (
                        ++rows > artifact.Rows
                        || ++total > MaximumRows
                        || decodedCharacters > MaximumDecodedCharacters
                    )
                    {
                        throw new InvalidDataException(
                            "The retained provider population exceeds its census."
                        );
                    }
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var input =
                        root.GetProperty("input")
                            .Deserialize<NormalizationInputRevision>(ProviderJson)
                        ?? throw new InvalidDataException("A retained provider wrapper is null.");
                    if (
                        root.GetProperty("kind").GetString() != "provider_input"
                        || root.GetProperty("row").GetProperty("uuid").GetGuid() != input.Uuid
                    )
                    {
                        throw new InvalidDataException(
                            "A provider wrapper changes its retained input UUID."
                        );
                    }
                    index.Observe(input);
                }
            }
            if (artifact.Format == "input_revision_json")
            {
                total += rows;
            }
            if (rows != artifact.Rows || total > MaximumRows)
            {
                throw new InvalidDataException("A retained provider artifact is incomplete.");
            }
        }
        index.CompleteObservation();
        return index;
    }

    private static async Task<CompositionHypothesisSong[]> ReadSongsAsync(
        Prepared prepared,
        CancellationToken ct
    )
    {
        // The authenticated census bounds this allocation and avoids growth copies
        // while the complete original-key population is resident.
        var originals = new Dictionary<Sha256Digest, OriginalEntry>(
            checked((int)prepared.Music.Descriptors.Rows)
        );
        await foreach (var line in LinesAsync(prepared.Music.Descriptors.Path, ct))
        {
            var descriptor = JsonSerializer.Deserialize<MusicRepertoireDescriptor>(
                line,
                ShadowCatalogRun.Json
            )!;
            var key = Sha256Digest.Compute(
                Encoding.UTF8.GetBytes(OriginalKey(descriptor.Scope, descriptor.OriginalKey))
            );
            if (
                !originals.TryAdd(
                    key,
                    new OriginalEntry(
                        OriginalFingerprint(
                            descriptor.Scope,
                            descriptor.ArtistUuid,
                            descriptor.OriginalKey,
                            descriptor.Title,
                            descriptor.Witness
                        )
                    )
                )
                || originals.Count > prepared.Music.Descriptors.Rows
            )
            {
                throw new InvalidDataException(
                    "The music descriptor population repeats or exceeds its original keys."
                );
            }
        }
        if (originals.Count != prepared.Music.Descriptors.Rows)
        {
            throw new InvalidDataException("The music descriptor population is incomplete.");
        }
        Console.Error.WriteLine(
            $"Composition hypotheses: authenticated {originals.Count} descriptors; managed bytes {GC.GetTotalMemory(false)}."
        );
        long dictionaryRows = 0;
        await foreach (var line in LinesAsync(prepared.Music.DictionaryNames.Path, ct))
        {
            _ =
                JsonSerializer.Deserialize<MusicDictionaryName>(line, ShadowCatalogRun.Json)
                ?? throw new InvalidDataException("A retained dictionary name is null.");
            if (++dictionaryRows > prepared.Music.DictionaryNames.Rows)
            {
                throw new InvalidDataException("The dictionary population exceeds its census.");
            }
        }
        if (dictionaryRows != prepared.Music.DictionaryNames.Rows)
        {
            throw new InvalidDataException("The retained dictionary population is incomplete.");
        }

        var seenCount = 0;
        var metadata = new SourceMetadataPool();
        var songs = new Dictionary<(string Scope, Guid Artist, string Key), SongBuilder>();
        long physicalRows = 0;
        long decodedCharacters = 0;
        foreach (var chunk in prepared.Output.Chunks)
        {
            long rows = 0;
            await foreach (var line in LinesAsync(chunk.Path, ct))
            {
                decodedCharacters += line.Length + 1;
                if (
                    ++rows > chunk.Rows
                    || ++physicalRows > MaximumRows
                    || decodedCharacters > MaximumDecodedCharacters
                )
                {
                    throw new InvalidDataException(
                        "The correspondence chunks exceed their row census."
                    );
                }
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var correspondence =
                    root.GetProperty("proposal")
                        .Deserialize<MusicRepertoireCorrespondence>(ShadowCatalogRun.Json)
                    ?? throw new InvalidDataException("A correspondence proposal is null.");
                var key = OriginalKey(correspondence.Scope, correspondence.OriginalKey);
                var keyDigest = Sha256Digest.Compute(Encoding.UTF8.GetBytes(key));
                if (
                    root.GetProperty("domain").GetString() != "repertoire_correspondences"
                    || root.GetProperty("inputKey").GetString() != key
                    || root.GetProperty("artistUuid").GetGuid() != correspondence.ArtistUuid
                    || root.GetProperty("outcome").GetString() != correspondence.Disposition
                    || !originals.TryGetValue(keyDigest, out var original)
                    || original.DescriptorFingerprint
                        != OriginalFingerprint(
                            correspondence.Scope,
                            correspondence.ArtistUuid,
                            correspondence.OriginalKey,
                            correspondence.OriginalTitle,
                            correspondence.SourceWitness
                        )
                )
                {
                    throw new InvalidDataException(
                        "A correspondence changes its frozen original descriptor."
                    );
                }
                var fingerprint = Sha256Digest.Compute(Encoding.UTF8.GetBytes(line));
                if (original.CorrespondenceFingerprint is { } prior)
                {
                    if (prior != fingerprint)
                    {
                        throw new InvalidDataException(
                            "A repeated correspondence has conflicting retained content."
                        );
                    }
                    continue;
                }
                originals[keyDigest] = original with { CorrespondenceFingerprint = fingerprint };
                seenCount++;
                // Filter individual targets, not the enclosing file disposition:
                // a partially unresolved compound can retain proposed members.
                var memberships = new HashSet<(string Scope, Guid Artist, string Key)>();
                foreach (
                    var target in correspondence.Targets.Where(target =>
                        target.Disposition == "proposed"
                    )
                )
                {
                    var identity = (
                        Scope: metadata.Share(correspondence.Scope),
                        Artist: correspondence.ArtistUuid,
                        Key: target.Song.EntityKey
                    );
                    if (
                        target.Song.ArtistUuid != correspondence.ArtistUuid
                        || correspondence.ArtistUuid == Guid.Empty
                    )
                    {
                        throw new InvalidDataException(
                            "A proposed target changes its original artist scope."
                        );
                    }
                    if (!songs.TryGetValue(identity, out var song))
                    {
                        song = new(
                            identity.Scope,
                            target.Song with
                            {
                                Basis = metadata.Share(target.Song.Basis),
                            },
                            metadata.Share(correspondence.SourceWitness)
                        );
                        songs.Add(identity, song);
                        if (songs.Count > MaximumRows)
                        {
                            throw new InvalidDataException(
                                "The proposed target population exceeds its bound."
                            );
                        }
                    }
                    song.Observe(target, memberships.Add(identity));
                }
            }
            if (rows != chunk.Rows)
            {
                throw new InvalidDataException("A correspondence chunk is incomplete.");
            }
        }
        if (seenCount != originals.Count)
        {
            throw new InvalidDataException(
                "The correspondence image omits original scoped descriptors."
            );
        }

        // Descriptor reconciliation is complete. Release its keys and digests
        // before allocating the ordered, completed song population.
        originals.Clear();
        originals.TrimExcess();

        return songs
            .Values.Select(song =>
                song.Complete(prepared.Artists, prepared.Request.ArtistContextManifest?.Sha256)
            )
            .OrderBy(song => song.Scope, StringComparer.Ordinal)
            .ThenBy(song => song.ArtistUuid)
            .ThenBy(song => song.Song.EntityKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<ShadowCatalogResult> Results(
        RetainedCompositionCandidateIndex index,
        IReadOnlyList<CompositionHypothesisSong> songs,
        CompositionHypothesisProjector projector
    )
    {
        foreach (var work in index.Works)
        {
            yield return new(
                "retained_work_candidates",
                work.EntityKey,
                "retained_work",
                Guid.Empty,
                Guid.Empty,
                "",
                "musicbrainz.org",
                "retained",
                null,
                RetainedCompositionCandidateIndex.Version,
                [work.EntityKey],
                work,
                work.Identity.Evidence
            );
        }
        foreach (var page in index.SearchPages)
        {
            var evidence = page.Origin.Evidence;
            yield return new(
                "retained_work_search_pages",
                JsonSerializer.Serialize(
                    new object[] { evidence.InputRevisionUuid, evidence.JsonPointer }
                ),
                "retained_search_page",
                evidence.InputRevisionUuid,
                Guid.Empty,
                "",
                "musicbrainz.org",
                "retained",
                null,
                NormalizationProviderDecoder.Version,
                [],
                page,
                evidence
            );
        }
        foreach (var song in songs)
        {
            var hypothesis = projector.Project(song);
            yield return new(
                "composition_hypotheses",
                JsonSerializer.Serialize(
                    new object[] { song.Scope, song.ArtistUuid, song.Song.EntityKey }
                ),
                "repertoire_target",
                song.Song.LegacySongUuid ?? Guid.Empty,
                song.ArtistUuid,
                song.ArtistName ?? "",
                "retained_music_target",
                hypothesis.Outcome,
                hypothesis.Reason,
                CompositionHypothesisProjector.Version,
                hypothesis
                    .Candidates.Select(candidate => candidate.WorkEntityKey)
                    .Prepend(song.Song.EntityKey)
                    .ToArray(),
                hypothesis,
                song.SourceWitness
            );
        }
    }

    private static async Task RequireCoverageAsync(
        string directory,
        Dictionary<Sha256Digest, Sha256Digest> expected,
        HashSet<Sha256Digest> required,
        CancellationToken ct
    )
    {
        long rows = 0;
        long decodedCharacters = 0;
        foreach (
            var path in Directory.EnumerateFiles(
                directory,
                "*.jsonl.gz",
                SearchOption.AllDirectories
            )
        )
        {
            await foreach (var line in LinesAsync(path, ct))
            {
                decodedCharacters += line.Length + 1;
                if (++rows > MaximumRows || decodedCharacters > MaximumDecodedCharacters)
                {
                    throw new InvalidDataException(
                        "Retained hypothesis output exceeds its physical row bound."
                    );
                }
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var domain = root.GetProperty("domain").GetString()!;
                if (Path.GetFileName(Path.GetDirectoryName(path)) != domain)
                {
                    throw new InvalidDataException(
                        "A retained hypothesis result differs from its exact frozen projection."
                    );
                }
                var key = ResultKey(domain, root.GetProperty("inputKey").GetString()!);
                if (
                    !expected.TryGetValue(key, out var wanted)
                    || Sha256Digest.Compute(Encoding.UTF8.GetBytes(line)) != wanted
                )
                {
                    throw new InvalidDataException(
                        "A retained hypothesis result differs from its exact frozen projection."
                    );
                }
                required.Remove(key);
            }
        }
        if (required.Count != 0)
        {
            throw new InvalidDataException("Hypothesis output is missing acknowledged results.");
        }
    }

    private static async Task<CompositionHypothesisOutput> DescribeOutputAsync(
        string directory,
        string sha,
        CompositionHypothesisBatchRequest request,
        RetainedCompositionCandidateIndex index,
        CompositionHypothesisSong[] songs,
        CompositionHypothesisProjector projector,
        CancellationToken ct
    )
    {
        var chunks = new List<MusicConsolidationArtifact>();
        foreach (
            var path in Directory
                .EnumerateFiles(directory, "*.jsonl.gz", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
        )
        {
            long rows = 0;
            await foreach (var _ in LinesAsync(path, ct))
            {
                rows++;
            }
            chunks.Add(
                new(
                    Path.GetRelativePath(directory, path),
                    new FileInfo(path).Length,
                    await HashAsync(path, ct),
                    rows
                )
            );
        }
        var progress = Path.Combine(directory, "progress.json");
        return new(
            Version,
            true,
            sha,
            NormalizationProviderDecoder.Version,
            RetainedCompositionCandidateIndex.Version,
            CompositionHypothesisProjector.Version,
            songs.LongLength,
            index.Works.Count,
            songs
                .Select(projector.Project)
                .GroupBy(result => result.Outcome)
                .ToDictionary(group => group.Key, group => group.LongCount()),
            chunks,
            new("progress.json", new FileInfo(progress).Length, await HashAsync(progress, ct), 1),
            request
        );
    }

    private static async Task<CompositionHypothesisOutput> ReadCompletedAsync(
        string path,
        string sha,
        CompositionHypothesisBatchRequest request,
        CancellationToken ct
    )
    {
        var output =
            JsonSerializer.Deserialize<CompositionHypothesisOutput>(
                await ReadMetadataAsync(path, ct),
                ShadowCatalogRun.Json
            ) ?? throw new InvalidDataException("A completed hypothesis manifest is required.");
        if (
            !output.Complete
            || output.Version != Version
            || output.RequestSha256 != sha
            || output.DecoderVersion != NormalizationProviderDecoder.Version
            || output.IndexVersion != RetainedCompositionCandidateIndex.Version
            || output.ProjectorVersion != CompositionHypothesisProjector.Version
            || JsonSerializer.Serialize(output.Inputs, ShadowCatalogRun.Json)
                != JsonSerializer.Serialize(request, ShadowCatalogRun.Json)
        )
        {
            throw new InvalidDataException(
                "Completed replay requires the same full inputs and projection versions."
            );
        }
        var directory = Path.GetDirectoryName(path)!;
        foreach (var artifact in output.Chunks.Append(output.Progress))
        {
            await RequireFileAsync(Resolve(artifact, directory), ct);
        }
        return output;
    }

    private static MusicConsolidationArtifact Resolve(
        MusicConsolidationArtifact artifact,
        string directory
    ) => artifact with { Path = Path.GetFullPath(artifact.Path, directory) };

    private static bool SameFile(
        MusicConsolidationArtifact left,
        MusicConsolidationArtifact right
    ) => left.Path == right.Path && left.Bytes == right.Bytes && left.Sha256 == right.Sha256;

    private static string OriginalKey(string scope, string key) =>
        JsonSerializer.Serialize(new[] { scope, key });

    private static Sha256Digest OriginalFingerprint(
        string scope,
        Guid artist,
        string key,
        string? title,
        MusicTitleWitness witness
    ) =>
        Sha256Digest.Compute(
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    scope,
                    artist,
                    key,
                    title,
                    witness,
                },
                ShadowCatalogRun.Json
            )
        );

    private static async IAsyncEnumerable<string> LinesAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        await foreach (
            var line in ShadowJsonLines.ReadTextAsync(
                path,
                MaximumMusicLine,
                MaximumDecodedCharacters,
                ct
            )
        )
        {
            yield return line;
        }
    }

    private static async Task<byte[]> ReadMetadataAsync(string path, CancellationToken ct)
    {
        return await ReadBoundedBytesAsync(path, 16 * 1024 * 1024, ct);
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(
        string path,
        int maximumBytes,
        CancellationToken ct
    )
    {
        await using var input = File.OpenRead(path);
        using var bytes = new MemoryStream();
        var buffer = new byte[65536];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (bytes.Length + read > maximumBytes)
            {
                throw new InvalidDataException(
                    "A retained hypothesis metadata file exceeds its byte bound."
                );
            }
            bytes.Write(buffer, 0, read);
        }
        return bytes.ToArray();
    }

    private static async Task RequireFileAsync(
        MusicConsolidationArtifact artifact,
        CancellationToken ct
    )
    {
        if (
            artifact.Bytes < 0
            || artifact.Rows < 0
            || new FileInfo(artifact.Path).Length != artifact.Bytes
            || await HashAsync(artifact.Path, ct) != artifact.Sha256
        )
        {
            throw new InvalidDataException("A frozen hypothesis artifact changed.");
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static object Population(
        RetainedCompositionCandidateIndex index,
        CompositionHypothesisSong[] songs
    ) =>
        new
        {
            songs = songs.Length,
            works = index.Works.Count,
            searchPages = index.SearchPages.Count,
            externalUniverseComplete = false,
            publicationPermitted = false,
        };

    private sealed class ResourceBudget
    {
        private const long FramingReserve = 3 * 65536;
        private readonly string outputDirectory;
        private readonly long minimumFreeBytes;
        private readonly long maximumOutputBytes;

        internal ResourceBudget(CompositionHypothesisResourceLimits? limits, string outputDirectory)
        {
            minimumFreeBytes = limits?.MinimumFreeBytes ?? MinimumFreeBytes;
            maximumOutputBytes = limits?.MaximumOutputBytes ?? MaximumOutputBytes;
            if (
                minimumFreeBytes <= 0
                || maximumOutputBytes <= 0
                || maximumOutputBytes > MaximumOutputBytes
            )
            {
                throw new InvalidDataException(
                    "Hypothesis limits require a positive free-space floor and output ceiling of at most 4 GiB."
                );
            }

            this.outputDirectory = outputDirectory;
        }

        internal void RequireFreeSpace(long nextBytes)
        {
            var available = new DriveInfo(Path.GetPathRoot(outputDirectory)!).AvailableFreeSpace;
            if (
                available < minimumFreeBytes
                || available - minimumFreeBytes < FramingReserve
                || nextBytes > available - minimumFreeBytes - FramingReserve
            )
            {
                throw new IOException(
                    "Composition hypotheses paused at the request's free-space floor."
                );
            }
        }

        internal long CheckResources()
        {
            RequireFreeSpace(0);
            var bytes = Directory.Exists(outputDirectory)
                ? Directory
                    .EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length)
                : 0;
            RequireOutputBudget(bytes, 0);
            return bytes;
        }

        internal void RequireOutputBudget(long retainedBytes, long pendingBytes)
        {
            // Scan durable files only at checkpoints. Between scans, reserve
            // uncompressed envelopes, expansion allowance and gzip framing.
            if (
                retainedBytes + pendingBytes + pendingBytes / 100 + FramingReserve
                > maximumOutputBytes
            )
            {
                throw new IOException(
                    "Composition hypotheses reached the request's retained-output bound."
                );
            }
        }
    }

    private static Sha256Digest Measure(ShadowCatalogResult result, out long bytes)
    {
        using var stream = new ResultSizeStream();
        JsonSerializer.Serialize(stream, result, ShadowCatalogRun.Json);
        bytes = stream.Length;
        return stream.Hash();
    }

    private sealed record Prepared(
        CompositionHypothesisBatchRequest Request,
        MusicConsolidationInput Music,
        MusicConsolidationOutput Output,
        Dictionary<(string Scope, Guid Artist), CompositionArtistContext> Artists,
        ResourceBudget Budget
    );

    private static Sha256Digest ResultKey(string domain, string inputKey) =>
        Sha256Digest.Compute(JsonSerializer.SerializeToUtf8Bytes(new[] { domain, inputKey }));

    // Full SHA-256 keys replace retained JSON key strings, under the same
    // cryptographic collision assumption as the input and output content pins.
    // Four inline words preserve every digest bit without per-entry arrays.
    private readonly record struct Sha256Digest(
        ulong First,
        ulong Second,
        ulong Third,
        ulong Fourth
    )
    {
        public static Sha256Digest Compute(ReadOnlySpan<byte> value)
        {
            Span<byte> bytes = stackalloc byte[32];
            SHA256.HashData(value, bytes);
            return From(bytes);
        }

        public static Sha256Digest From(ReadOnlySpan<byte> bytes) =>
            new(
                BinaryPrimitives.ReadUInt64LittleEndian(bytes),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..])
            );
    }

    // One inline state per authenticated original; null means no correspondence
    // has been observed, and exact repeated rows never advance the seen census.
    private readonly record struct OriginalEntry(
        Sha256Digest DescriptorFingerprint,
        Sha256Digest? CorrespondenceFingerprint = null
    );

    // A source chunk supplies many songs. Share its literal coordinates within
    // this run instead of retaining another copy of each path and hash per song.
    private sealed class SourceMetadataPool
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

        internal string Share(string value)
        {
            if (values.TryGetValue(value, out var retained))
            {
                return retained;
            }

            values.Add(value, value);
            return value;
        }

        internal MusicTitleWitness Share(MusicTitleWitness witness) =>
            witness with
            {
                Run = Share(witness.Run),
                Chunk = Share(witness.Chunk),
                ChunkSha256 = Share(witness.ChunkSha256),
                ValuePointer = witness.ValuePointer is null ? null : Share(witness.ValuePointer),
                DocumentKind = Share(witness.DocumentKind),
            };
    }

    private sealed class SongBuilder(
        string scope,
        ShadowRepertoireSong song,
        MusicTitleWitness witness
    )
    {
        private HashSet<ShadowRepertoireDictionaryEvidence>? names;
        private MusicRepertoireRelation? firstRelation;
        private HashSet<MusicRepertoireRelation>? relations;
        private long memberships;

        public void Observe(MusicRepertoireTarget target, bool firstMembership)
        {
            if (target.Song != song)
            {
                throw new InvalidDataException(
                    "A scoped proposed target has conflicting song facts."
                );
            }
            // Most targets have one relation and no dictionary evidence. Allocate
            // sets only when an observation actually needs their uniqueness checks.
            if (firstRelation is null)
            {
                firstRelation = target.Relation;
            }
            else if (target.Relation != firstRelation.Value)
            {
                relations ??= [firstRelation.Value];
                relations.Add(target.Relation);
            }
            foreach (
                var diagnostic in target.ComparisonDiagnostics.Where(candidate =>
                    candidate.Song.EntityKey == song.EntityKey
                )
            )
            {
                foreach (var name in diagnostic.Evidence)
                {
                    names ??= [];
                    names.Add(name);
                }
            }
            if (firstMembership)
            {
                memberships++;
            }
        }

        public CompositionHypothesisSong Complete(
            Dictionary<(string Scope, Guid Artist), CompositionArtistContext> artists,
            string? contextSha
        )
        {
            artists.TryGetValue((scope, song.ArtistUuid), out var artist);
            return new(
                scope,
                song.ArtistUuid,
                song,
                names is null
                    ? []
                    : names
                        .OrderBy(name => name.InputKey, StringComparer.Ordinal)
                        .ThenBy(name => name.JsonPointer, StringComparer.Ordinal)
                        .ThenBy(name => name.Quote, StringComparer.Ordinal)
                        .ThenBy(
                            name =>
                                JsonSerializer.Serialize(
                                    name.RetainedWitness,
                                    ShadowCatalogRun.Json
                                ),
                            StringComparer.Ordinal
                        )
                        .ToArray(),
                relations is null ? [firstRelation!.Value] : relations.Order().ToArray(),
                witness,
                memberships,
                artist?.Name,
                artist,
                artist == null ? null : contextSha
            );
        }
    }

    private sealed class ResultSizeStream(long maximumBytes = MaximumMusicLine) : Stream
    {
        private long size;
        private readonly IncrementalHash digest = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );

        public Sha256Digest Hash()
        {
            Span<byte> bytes = stackalloc byte[32];
            digest.GetHashAndReset(bytes);
            return Sha256Digest.From(bytes);
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            size += buffer.Length;
            if (size > maximumBytes)
            {
                throw new InvalidDataException("A complete hypothesis result exceeds 32 MiB.");
            }
            digest.AppendData(buffer);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => size;
        public override long Position
        {
            get => size;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                digest.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
