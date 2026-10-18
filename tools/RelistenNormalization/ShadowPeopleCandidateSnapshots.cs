using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace Relisten.Normalization.Batch;

/// <summary>
/// A byte-pinned, offline collection of public people candidate reads. Each entry retains its
/// original query, limits, facts, issues and read set. Reads may come from separate database
/// transactions; this artifact makes no claim about current freshness or acceptance authority.
/// </summary>
public sealed class ShadowPeopleCandidateSnapshots
{
    private const string Version = "shadow-people-candidate-snapshots-v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
    private readonly Dictionary<
        string,
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts>
    > snapshots;

    /// <summary>Lowercase SHA-256 of the exact loaded bytes, for retained comparison locators.</summary>
    public string Identity { get; }

    /// <summary>
    /// Loads &lt;path&gt;#sha256=&lt;64 hexadecimal digits&gt;. The external digest pins the complete
    /// artifact; the reader's opaque QueryHash is preserved rather than recomputed here.
    /// </summary>
    public ShadowPeopleCandidateSnapshots(string pinnedPath)
    {
        var separator = pinnedPath.LastIndexOf("#sha256=", StringComparison.Ordinal);
        if (
            separator <= 0
            || pinnedPath.Length - separator != 72
            || !pinnedPath[(separator + 8)..].All(Uri.IsHexDigit)
        )
        {
            throw new ArgumentException(
                "People candidate snapshots require <path>#sha256=<64 hexadecimal digits>.",
                nameof(pinnedPath)
            );
        }

        var bytes = File.ReadAllBytes(Path.GetFullPath(pinnedPath[..separator]));
        Identity = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!Identity.Equals(pinnedPath[(separator + 8)..], StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "People candidate snapshots differ from the supplied digest."
            );
        }

        var document =
            JsonSerializer.Deserialize<SnapshotDocument>(bytes, Json)
            ?? throw new InvalidDataException("People candidate snapshots require a document.");
        if (
            document.Version != Version
            || document.Domain != IdentityMatchingDomain.People
            || document.ProjectionVersion != PeopleActorProjection.Version
            || document.Entries == null
        )
        {
            throw new InvalidDataException("Unsupported people candidate snapshot contract.");
        }

        snapshots = Index(document.Entries);
    }

    /// <summary>
    /// Finds this exact projected query. A missing snapshot is unknown, including when another
    /// query selected the same names. A present empty snapshot retains its original completeness.
    /// Returned snapshot collections must be treated as read-only.
    /// </summary>
    public ShadowPeopleCandidateSnapshotLookup Find(NormalizationCandidateQuery query)
    {
        RequirePeopleQuery(query);
        var fingerprint = PeopleActorProjection.QueryFingerprint(query);
        return snapshots.TryGetValue(fingerprint, out var snapshot)
            ? new(fingerprint, snapshot, new(Identity, fingerprint, snapshot.QueryHash))
            : new(fingerprint, null, null);
    }

    /// <summary>
    /// Reads each distinct projected query through the public read-only candidate service and
    /// serializes the original snapshots. Each call has its own database snapshot. Callers own
    /// saving the returned bytes and retaining their SHA-256 separately from the file.
    /// </summary>
    public static async Task<byte[]> ExportAsync(
        NormalizationCandidateService service,
        IEnumerable<NormalizationCandidateQuery> queries,
        CandidateFactsLimits? limits = null,
        CancellationToken cancellationToken = default
    )
    {
        var results = new List<NormalizationCandidateFactsSnapshot<PersonCandidateFacts>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var query in queries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequirePeopleQuery(query);
            var fingerprint = PeopleActorProjection.QueryFingerprint(query);
            if (!seen.Add(fingerprint))
            {
                continue;
            }

            var snapshot = await service.FindPeopleFactsAsync(query, limits, cancellationToken);
            if (PeopleActorProjection.QueryFingerprint(snapshot.Query) != fingerprint)
            {
                throw new InvalidDataException(
                    "Candidate reader returned a different projected query."
                );
            }

            results.Add(snapshot);
        }

        return Serialize(results);
    }

    /// <summary>
    /// Serializes retained typed snapshots without altering their facts or read sets. Repeated
    /// identical queries collapse to one entry; conflicting snapshots for a query are rejected.
    /// Query entry order does not change the serialized bytes.
    /// </summary>
    public static byte[] Serialize(
        IEnumerable<NormalizationCandidateFactsSnapshot<PersonCandidateFacts>> snapshots
    )
    {
        var entries = snapshots
            .Select(snapshot => new SnapshotEntry(
                PeopleActorProjection.QueryFingerprint(snapshot.Query),
                snapshot
            ))
            .ToArray();
        var indexed = Index(entries);
        return JsonSerializer.SerializeToUtf8Bytes(
            new SnapshotDocument(
                Version,
                IdentityMatchingDomain.People,
                PeopleActorProjection.Version,
                indexed
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new SnapshotEntry(pair.Key, pair.Value))
                    .ToArray()
            ),
            Json
        );
    }

    private static Dictionary<
        string,
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts>
    > Index(IEnumerable<SnapshotEntry> entries)
    {
        var indexed = new Dictionary<
            string,
            NormalizationCandidateFactsSnapshot<PersonCandidateFacts>
        >(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            Validate(entry.Snapshot);
            if (
                PeopleActorProjection.QueryFingerprint(entry.Snapshot.Query)
                != entry.QueryFingerprint
            )
            {
                throw new InvalidDataException(
                    "Candidate snapshot query fingerprint does not match its query."
                );
            }

            if (indexed.TryGetValue(entry.QueryFingerprint, out var previous))
            {
                if (
                    !JsonElement.DeepEquals(
                        JsonSerializer.SerializeToElement(previous, Json),
                        JsonSerializer.SerializeToElement(entry.Snapshot, Json)
                    )
                )
                {
                    throw new InvalidDataException(
                        "Conflicting candidate snapshots have the same query fingerprint."
                    );
                }
            }
            else
            {
                indexed.Add(entry.QueryFingerprint, entry.Snapshot);
            }
        }

        return indexed;
    }

    private static void Validate(NormalizationCandidateFactsSnapshot<PersonCandidateFacts> snapshot)
    {
        if (
            snapshot == null
            || snapshot.Version != NormalizationCandidateService.PeopleFactsVersion
            || snapshot.Query == null
            || snapshot.Query.Domain != IdentityMatchingDomain.People
            || string.IsNullOrWhiteSpace(snapshot.QueryHash)
            || snapshot.Selection == null
            || snapshot.Selection.Domain != IdentityMatchingDomain.People
            || snapshot.Selection.Items == null
            || snapshot.Facts == null
            || snapshot.Limits == null
            || snapshot.ReadSet == null
            || snapshot.Issues == null
        )
        {
            throw new InvalidDataException("Unsupported or incomplete people candidate snapshot.");
        }

        var selected = new Dictionary<Guid, NormalizationCandidateHit>();
        foreach (var hit in snapshot.Selection.Items)
        {
            if (hit.Uuid == Guid.Empty || !selected.TryAdd(hit.Uuid, hit))
            {
                throw new InvalidDataException(
                    "Candidate selection requires distinct resolved person UUIDs."
                );
            }
        }

        var factsUuids = new HashSet<Guid>();
        foreach (var fact in snapshot.Facts)
        {
            if (
                !factsUuids.Add(fact.Hit.Uuid)
                || !selected.TryGetValue(fact.Hit.Uuid, out var hit)
                || fact.Header.Uuid != fact.Hit.Uuid
                || !JsonElement.DeepEquals(
                    JsonSerializer.SerializeToElement(hit, Json),
                    JsonSerializer.SerializeToElement(fact.Hit, Json)
                )
            )
            {
                throw new InvalidDataException(
                    "Candidate facts must correspond exactly to the selected person UUIDs."
                );
            }

            // The v5 reader separates complete matching aggregates from bounded witnesses.
            // Refuse an omitted aggregate rather than letting Candidate fall back to examples.
            if (
                fact.AcceptedAliasMatchingNames == null
                || fact.AliasMatchingNamesComplete == null
                || fact.ActorMatchingFacts == null
            )
            {
                throw new InvalidDataException(
                    "People v5 snapshots require alias and actor matching aggregates."
                );
            }
        }

        if (factsUuids.Count != selected.Count)
        {
            throw new InvalidDataException(
                "Every selected person requires its original candidate facts."
            );
        }
    }

    private static void RequirePeopleQuery(NormalizationCandidateQuery query)
    {
        if (query.Domain != IdentityMatchingDomain.People)
        {
            throw new ArgumentException(
                "Candidate snapshot lookup requires a people query.",
                nameof(query)
            );
        }
    }

    private sealed record SnapshotDocument(
        string Version,
        IdentityMatchingDomain Domain,
        string ProjectionVersion,
        IReadOnlyList<SnapshotEntry> Entries
    );

    private sealed record SnapshotEntry(
        string QueryFingerprint,
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts> Snapshot
    );
}

/// <summary>Locates one original snapshot without repeating its facts in every comparison.</summary>
public sealed record ShadowPeopleCandidateSnapshotLocator(
    string ArtifactSha256,
    string QueryFingerprint,
    string QueryHash
);

/// <summary>A null Snapshot means this query was not exported; it does not mean no candidates.</summary>
public sealed record ShadowPeopleCandidateSnapshotLookup(
    string QueryFingerprint,
    NormalizationCandidateFactsSnapshot<PersonCandidateFacts>? Snapshot,
    ShadowPeopleCandidateSnapshotLocator? Locator
)
{
    public bool Found => Snapshot != null;
}
