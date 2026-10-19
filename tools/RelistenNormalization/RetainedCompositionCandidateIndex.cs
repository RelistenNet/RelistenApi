using System.Collections.ObjectModel;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Indexes the complete supplied MusicBrainz context without a performer boundary.
/// Every candidate remains an external assertion, not a catalog work assignment or
/// evidence that the retained search pages exhaust the external work universe.
/// </summary>
public sealed class RetainedCompositionCandidateIndex
{
    public const string Version = "retained-composition-candidates-v1";
    public const int MaximumInputs = 10_000;
    public const int MaximumWorks = 100_000;
    public const int MaximumPayloadBytes = 32 * 1024 * 1024;
    public const long MaximumAggregatePayloadBytes = 256L * 1024 * 1024;

    private readonly NormalizationProviderDecoder decoder = new();
    private readonly Dictionary<Guid, InputFingerprint> inputs = [];
    private readonly Dictionary<Guid, WorkBuilder> works = [];
    private readonly List<MusicBrainzSearchPageObservation> searchPages = [];
    private readonly Dictionary<string, IReadOnlyList<RetainedCompositionWork>> titles = new(
        StringComparer.Ordinal
    );
    private IReadOnlyList<RetainedCompositionWork>? completedWorks;
    private IReadOnlyList<MusicBrainzSearchPageObservation>? completedSearchPages;
    private long payloadBytes;
    private bool failed;

    public IReadOnlyList<RetainedCompositionWork> Works
    {
        get
        {
            RequireCompleted();
            return completedWorks!;
        }
    }

    public IReadOnlyList<MusicBrainzSearchPageObservation> SearchPages
    {
        get
        {
            RequireCompleted();
            return completedSearchPages!;
        }
    }

    public void Observe(NormalizationInputRevision input)
    {
        RequireObservation();
        try
        {
            if (input.Payload.Length > MaximumPayloadBytes)
            {
                throw new InvalidDataException(
                    "A retained composition input exceeds the byte bound."
                );
            }
            ShadowCompositionAcquisition.VerifyInput(input);
            var fingerprint = new InputFingerprint(
                input.Sha256.ToLowerInvariant(),
                input.Resource,
                input.MediaType,
                input.CaptureVersion,
                input.CapturedAt
            );
            if (inputs.TryGetValue(input.Uuid, out var previous))
            {
                if (previous != fingerprint)
                {
                    throw new InvalidDataException(
                        "A retained input UUID has conflicting revisions."
                    );
                }
                return;
            }
            if (
                inputs.Count >= MaximumInputs
                || input.Payload.Length > MaximumAggregatePayloadBytes - payloadBytes
            )
            {
                throw new InvalidDataException(
                    "The retained composition input population exceeds its bound."
                );
            }

            if (input.Resource.Provider == "musicbrainz.org")
            {
                var decoded = decoder.Decode(input);
                using var document = NormalizationRetainedDocument.Open(input);
                ObserveLookups(decoded, document);
                ObserveSearches(input, decoded, document);
                ObserveRecordings(decoded, document);
            }

            inputs.Add(input.Uuid, fingerprint);
            payloadBytes += input.Payload.Length;
        }
        catch
        {
            // A refused input may contain unseen rivals. Never freeze a partial index
            // after a caller catches the refusal; create a new index to retry the run.
            failed = true;
            throw;
        }
    }

    public void CompleteObservation()
    {
        if (failed)
        {
            throw new InvalidOperationException(
                "A refused retained input prevents index completion."
            );
        }
        if (completedWorks != null)
        {
            return;
        }

        completedWorks = Array.AsReadOnly(
            works
                .Values.Select(work => work.Complete())
                .OrderBy(work => work.EntityKey, StringComparer.Ordinal)
                .ToArray()
        );
        completedSearchPages = Array.AsReadOnly(
            searchPages.OrderBy(page => page.Origin.Evidence.InputRevisionUuid).ToArray()
        );
        var names = new Dictionary<string, List<RetainedCompositionWork>>(StringComparer.Ordinal);
        foreach (var work in completedWorks)
        {
            var keys = work
                .Titles.Select(title => title.Value)
                .Concat(
                    work.Aliases.Where(alias => alias.Name != null)
                        .Select(alias => alias.Name!.Value)
                )
                .Select(ShadowCompositionAdapter.NormalizeTitle)
                .Where(key => key.Length > 0)
                .Distinct(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                if (!names.TryGetValue(key, out var matches))
                {
                    matches = [];
                    names.Add(key, matches);
                }
                matches.Add(work);
            }
        }
        foreach (var (key, matches) in names)
        {
            titles.Add(key, matches.AsReadOnly());
        }
    }

    public IReadOnlyList<RetainedCompositionWork> Find(string exactTitleOrAlias)
    {
        RequireCompleted();
        return titles.GetValueOrDefault(ShadowCompositionAdapter.NormalizeTitle(exactTitleOrAlias))
            ?? [];
    }

    private void ObserveLookups(
        ProviderDecodeResult decoded,
        NormalizationRetainedDocument document
    )
    {
        foreach (var assertion in decoded.MusicBrainzWorks ?? [])
        {
            document.ValidateEvidence(assertion.Origin.Evidence);
            var work = Work(assertion.Work, document);
            if (work == null)
            {
                continue;
            }
            work.Lookups.Add(assertion);
            AddNames(work, assertion.Work.Name, assertion.Aliases, assertion.Iswcs, document);
            foreach (var relation in assertion.Relationships ?? [])
            {
                document.ValidateEvidence(relation.Evidence);
                if (IsWriterRole(relation.Type))
                {
                    AddWriter(
                        work,
                        relation.Type,
                        relation.Direction,
                        relation.TargetType,
                        relation.Target?.Identity.Identity.ResourceType == "artist"
                            ? relation.Target
                            : null,
                        relation.Evidence,
                        "work_lookup",
                        document
                    );
                }
            }
        }
    }

    private void ObserveSearches(
        NormalizationInputRevision input,
        ProviderDecodeResult decoded,
        NormalizationRetainedDocument document
    )
    {
        if (input.Resource.ResourceType != "work-search")
        {
            return;
        }
        // The decoder can stop before creating a page for a malformed root or a
        // provider error. Preserve that retained response and its diagnostics too;
        // absent count/offset/hits remain unknown rather than becoming zero hits.
        IReadOnlyList<MusicBrainzSearchPageObservation> pages = decoded.MusicBrainzSearchPages
            is { Count: > 0 } observed
            ? observed
            :
            [
                new()
                {
                    Origin = new(
                        input.Resource,
                        new(input.Uuid, ""),
                        ProviderObservationScope.ResourceItem
                    ),
                    Issues = decoded.Issues,
                },
            ];
        foreach (var page in pages)
        {
            document.ValidateEvidence(page.Origin.Evidence);
            foreach (var issue in page.Issues)
            {
                document.ValidateEvidence(issue.Evidence);
            }
            searchPages.Add(page);
            foreach (var hit in page.Hits ?? [])
            {
                document.ValidateEvidence(hit.Evidence);
                var work = Work(hit.Entity, document);
                if (work == null)
                {
                    continue;
                }
                work.Searches.Add(new(page.Origin, hit));
                AddNames(work, hit.Entity.Name, hit.Aliases, hit.Iswcs, document);
                foreach (var relation in hit.WorkArtists ?? [])
                {
                    document.ValidateEvidence(relation.Evidence);
                    if (IsWriterRole(relation.Type))
                    {
                        AddWriter(
                            work,
                            relation.Type,
                            relation.Direction,
                            relation.TargetType,
                            relation.Artist,
                            relation.Evidence,
                            "work_search",
                            document
                        );
                    }
                }
            }
        }
    }

    private void ObserveRecordings(
        ProviderDecodeResult decoded,
        NormalizationRetainedDocument document
    )
    {
        foreach (var recording in decoded.MusicBrainzRecordings ?? [])
        {
            document.ValidateEvidence(recording.Origin.Evidence);
            ValidateEntity(recording.Recording, document);
            if (!Qualified(recording.Recording.Identity, "recording", out _))
            {
                continue;
            }
            foreach (var credit in recording.ArtistCredits ?? [])
            {
                document.ValidateEvidence(credit.Evidence);
                if (credit.Artist != null)
                {
                    ValidateEntity(credit.Artist, document);
                }
                if (credit.Name != null)
                {
                    document.ValidateEvidence(credit.Name.Evidence);
                }
                if (credit.JoinPhrase != null)
                {
                    document.ValidateEvidence(credit.JoinPhrase.Evidence);
                }
            }
            foreach (var relation in recording.Relationships ?? [])
            {
                document.ValidateEvidence(relation.Evidence);
                if (
                    relation.Type?.Value != "performance"
                    || relation.Direction?.Value != "forward"
                    || relation.TargetType?.Value != "work"
                    || relation.Target is not { } target
                )
                {
                    continue;
                }
                var work = Work(target, document);
                if (work == null)
                {
                    continue;
                }
                work.Recordings.Add(new(recording.Recording.Identity, target.Identity, relation));
                if (work.RecordingOrigins.Add(recording.Origin.Evidence))
                {
                    work.RecordingAssertions.Add(recording);
                }
                AddNames(work, target.Name, null, null, document);
            }
        }
    }

    private WorkBuilder? Work(
        MusicBrainzEntityReference entity,
        NormalizationRetainedDocument document
    )
    {
        ValidateEntity(entity, document);
        if (!Qualified(entity.Identity, "work", out var mbid))
        {
            return null;
        }
        if (!works.TryGetValue(mbid, out var work))
        {
            if (works.Count >= MaximumWorks)
            {
                throw new InvalidDataException("The retained work population exceeds its bound.");
            }
            work = new(mbid);
            works.Add(mbid, work);
        }
        work.Identities.Add(entity.Identity);
        return work;
    }

    private static void AddNames(
        WorkBuilder work,
        ObservedValue<string>? title,
        IReadOnlyList<MusicBrainzAliasObservation>? aliases,
        IReadOnlyList<MusicBrainzIswcObservation>? iswcs,
        NormalizationRetainedDocument document
    )
    {
        if (title != null)
        {
            document.ValidateEvidence(title.Evidence);
            work.Titles.Add(title);
        }
        foreach (var alias in aliases ?? [])
        {
            document.ValidateEvidence(alias.Evidence);
            if (alias.Name != null)
            {
                document.ValidateEvidence(alias.Name.Evidence);
            }
            work.Aliases.Add(alias);
        }
        foreach (var iswc in iswcs ?? [])
        {
            document.ValidateEvidence(iswc.Original.Evidence);
            document.ValidateEvidence(iswc.Identity.Evidence);
            work.Iswcs.Add(iswc);
        }
    }

    private static void AddWriter(
        WorkBuilder work,
        ObservedValue<string>? role,
        ObservedValue<string>? direction,
        ObservedValue<string>? targetType,
        MusicBrainzEntityReference? artist,
        NormalizedEvidenceReference evidence,
        string sourceForm,
        NormalizationRetainedDocument document
    )
    {
        foreach (var fact in new[] { role, direction, targetType })
        {
            if (fact != null)
            {
                document.ValidateEvidence(fact.Evidence);
            }
        }
        if (artist != null)
        {
            ValidateEntity(artist, document);
        }
        work.Writers.Add(new(role, direction, targetType, artist, evidence, sourceForm));
    }

    private static bool IsWriterRole(ObservedValue<string>? type) =>
        type?.Value is "writer" or "composer" or "lyricist";

    private static bool Qualified(ObservedProviderIdentity identity, string kind, out Guid mbid)
    {
        mbid = Guid.Empty;
        return identity.Identity is { Provider: "musicbrainz.org" } resource
            && resource.ResourceType == kind
            && Guid.TryParseExact(resource.ProviderKey, "D", out mbid)
            && mbid != Guid.Empty;
    }

    private static void ValidateEntity(
        MusicBrainzEntityReference entity,
        NormalizationRetainedDocument document
    )
    {
        document.ValidateEvidence(entity.Evidence);
        document.ValidateEvidence(entity.Identity.Evidence);
        foreach (
            var fact in new[] { entity.Name, entity.SortName, entity.Type, entity.Disambiguation }
        )
        {
            if (fact != null)
            {
                document.ValidateEvidence(fact.Evidence);
            }
        }
    }

    private void RequireObservation()
    {
        if (failed || completedWorks != null)
        {
            throw new InvalidOperationException("Retained composition observation is closed.");
        }
    }

    private void RequireCompleted()
    {
        if (failed || completedWorks == null)
        {
            throw new InvalidOperationException(
                "Complete the retained composition context before retrieval."
            );
        }
    }

    private sealed record InputFingerprint(
        string Sha256,
        NormalizationResourceKey Resource,
        string MediaType,
        string CaptureVersion,
        DateTime CapturedAt
    );

    private sealed class WorkBuilder(Guid mbid)
    {
        public readonly List<ObservedProviderIdentity> Identities = [];
        public readonly List<ObservedValue<string>> Titles = [];
        public readonly List<MusicBrainzAliasObservation> Aliases = [];
        public readonly List<MusicBrainzIswcObservation> Iswcs = [];
        public readonly List<RetainedCompositionWriterAssertion> Writers = [];
        public readonly List<MusicBrainzWorkObservation> Lookups = [];
        public readonly List<RetainedCompositionSearchAssertion> Searches = [];
        public readonly List<ShadowProviderCompositionRelationship> Recordings = [];
        public readonly HashSet<NormalizedEvidenceReference> RecordingOrigins = [];
        public readonly List<MusicBrainzRecordingObservation> RecordingAssertions = [];

        public RetainedCompositionWork Complete() =>
            new(
                ShadowCompositionAdapter.WorkKey(mbid),
                Identities
                    .OrderBy(identity => identity.Evidence.InputRevisionUuid)
                    .ThenBy(identity => identity.Evidence.JsonPointer, StringComparer.Ordinal)
                    .First(),
                Freeze(Titles),
                Freeze(Aliases),
                Freeze(Iswcs),
                Freeze(Writers),
                Freeze(Lookups),
                Freeze(Searches),
                Freeze(Recordings),
                Freeze(RecordingAssertions)
            );

        private static ReadOnlyCollection<T> Freeze<T>(List<T> values) =>
            Array.AsReadOnly(values.ToArray());
    }
}
