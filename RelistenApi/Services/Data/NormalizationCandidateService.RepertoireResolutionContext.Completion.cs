using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    public async Task<RepertoireResolutionPageContext> ReadRepertoireResolutionCompletionContextAsync(
        Guid registrationUuid,
        Guid attemptUuid,
        RepertoireResolutionPreparation preparation,
        IReadOnlyList<RepertoireResolutionClaimDecision> decisions,
        CancellationToken ct = default
    )
    {
        var source = preparation.Source;
        var pinnedPolicy = preparation.Policy;
        if (
            registrationUuid == Guid.Empty
            || attemptUuid == Guid.Empty
            || preparation.PageSize is < 1 or > 1000
            || decisions.Count > NormalizationImportedCatalogService.MaximumRepertoireSongs
        )
        {
            throw new ArgumentException(
                "Completion requires a registration and its bounded complete decisions."
            );
        }
        await using var c = db.CreateConnection(false, false);
        await c.OpenAsync(ct);
        await using var t = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await c.ExecuteAsync(
            new CommandDefinition(
                "SET TRANSACTION READ ONLY",
                transaction: t,
                cancellationToken: ct
            )
        );
        await RequireRepertoireSourceCurrent(c, t, source.ReadSet, ct);
        await RepertoireResolutionPolicyService.RequireStateAsync(c, t, pinnedPolicy, ct);
        var facts = new CandidateFactsContext();
        var owners = await ReadRepertoireOwners(
            c,
            t,
            registrationUuid,
            source.ArtistUuid,
            facts,
            ct
        );
        var issues = new List<string>();
        var accepted = decisions
            .Where(value =>
                value.Disposition
                    is RepertoireResolutionClaimDisposition.Bound
                        or RepertoireResolutionClaimDisposition.Provisional
            )
            .ToArray();
        var targets = accepted
            .Where(value => value.RepertoireSongUuid.HasValue)
            .Select(value => value.RepertoireSongUuid!.Value)
            .Distinct()
            .ToArray();
        if (
            accepted.Any(value => value.RepertoireSongUuid == null)
            || targets.Length > 0 && source.ArtistUuid == null
        )
        {
            issues.Add("accepted_correspondence_target_missing");
        }
        await ValidateRepertoireDecisionRevisions(c, t, attemptUuid, accepted, issues, ct);
        var exclusions = await ReadRepertoireExclusions(
            c,
            t,
            owners.Artist?.Membership.Owner?.SubjectUuid,
            ct
        );
        var sourceClaims = source.Claims.ToDictionary(value => value.ClaimUuid);
        var correspondenceKeys = accepted
            .Select(value => value.CorrespondenceKey)
            .Distinct()
            .ToArray();
        var latest = (
            await c.QueryAsync<RepertoireCompletionAssociation>(
                new CommandDefinition(
                    """
                    SELECT DISTINCT ON (correspondence_key) correspondence_key AS "CorrespondenceKey",
                        repertoire_song_uuid AS "RepertoireSongUuid",disposition AS "Disposition"
                    FROM normalization.repertoire_resolution_associations
                    WHERE registration_uuid=@registrationUuid AND correspondence_key=ANY(@correspondenceKeys)
                    ORDER BY correspondence_key,sequence DESC
                    """,
                    new { registrationUuid, correspondenceKeys },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToDictionary(value => value.CorrespondenceKey);
        var storedTargets = latest
            .Values.Where(value => value.Disposition is "Bound" or "Provisional")
            .Where(value => value.RepertoireSongUuid.HasValue)
            .Select(value => value.RepertoireSongUuid!.Value);
        var resolvedTargets = source.ArtistUuid is { } artist
            ? await ResolveRepertoireCompletionTargets(
                c,
                t,
                artist,
                targets.Concat(storedTargets).Distinct().ToArray(),
                issues,
                ct
            )
            : new Dictionary<Guid, Guid>();
        foreach (var target in targets)
        {
            if (!resolvedTargets.TryGetValue(target, out var resolved) || resolved != target)
            {
                issues.Add("accepted_correspondence_target_changed");
            }
        }
        foreach (var decision in accepted)
        {
            if (
                !sourceClaims.TryGetValue(decision.ClaimUuid, out var claim)
                || claim.CorrespondenceKey != decision.CorrespondenceKey
            )
            {
                issues.Add("accepted_correspondence_source_missing");
                continue;
            }
            if (
                latest.TryGetValue(decision.CorrespondenceKey, out var association)
                && (
                    association.Disposition is not ("Bound" or "Provisional")
                    || association.RepertoireSongUuid is not { } storedTarget
                    || !resolvedTargets.TryGetValue(storedTarget, out var resolvedTarget)
                    || resolvedTarget != decision.RepertoireSongUuid
                )
            )
            {
                issues.Add("accepted_correspondence_changed");
            }
            if (
                exclusions.Any(publication =>
                    RepertoireCompletionExcluded(publication, claim, decision.RepertoireSongUuid)
                )
            )
            {
                issues.Add("accepted_correspondence_excluded");
            }
        }
        var negativeReads = await RecheckRepertoireNegativeDecisions(
            c,
            t,
            registrationUuid,
            preparation,
            decisions,
            exclusions,
            ct
        );
        var universes = await ReadRepertoireContextUniverses(c, t, source.ArtistUuid, ct);
        var read = await facts.ReadReadSetAsync(c, t, universes, ct);
        issues.AddRange(
            facts
                .Issues.Where(value => value.PreventsAutomaticAcceptance)
                .Select(value => "candidate_context:" + value.Kind + ":" + value.FactKind)
        );
        var finalDecisionRead = MergeRepertoireContextReadSet(
            new([], [], [], [], null, [], [], [], []),
            negativeReads.Append(read),
            pinnedPolicy.OwnerObservation
        );
        read = new(
            finalDecisionRead.CandidateUniverses,
            finalDecisionRead.OwnerObservations,
            finalDecisionRead.CatalogInputs
        );
        read = await ReadRepertoireCompletionFreshness(
            c,
            t,
            attemptUuid,
            source.ArtistUuid,
            owners.Artist?.Membership.Owner,
            read,
            ct
        );
        var merged = MergeRepertoireContextReadSet(
            source.ReadSet,
            [read],
            pinnedPolicy.OwnerObservation
        );
        await t.CommitAsync(ct);
        return new(
            source.ArtistUuid,
            [],
            merged,
            owners.Observation,
            owners.Artist,
            pinnedPolicy,
            issues.Distinct().ToArray(),
            issues.Count == 0 && RepertoireFreshnessComplete(facts.Issues, source)
        );
    }

    private static async Task<CandidateFactsReadSet[]> RecheckRepertoireNegativeDecisions(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid registrationUuid,
        RepertoireResolutionPreparation preparation,
        IReadOnlyList<RepertoireResolutionClaimDecision> decisions,
        IReadOnlyList<MusicPublication> exclusions,
        CancellationToken ct
    )
    {
        // Later accepted pages can make an earlier unresolved alias bindable. The original
        // page remains audit evidence, while this final semantic check prevents settling a
        // negative that our own permitted pool changes have already made obsolete.
        var source = preparation.Source;
        var pinnedPolicy = preparation.Policy;
        var negative = decisions
            .Where(value =>
                value.Disposition
                    is RepertoireResolutionClaimDisposition.Review
                        or RepertoireResolutionClaimDisposition.Unresolved
            )
            .ToDictionary(value => value.ClaimUuid);
        if (negative.Count == 0)
        {
            return [];
        }

        var evaluator = new RepertoireResolutionPolicyEvaluator();
        var reads = new List<CandidateFactsReadSet>();
        var titleIndex =
            RepertoireResolutionPolicyEvaluator.SupportsTitleReuse(pinnedPolicy.Active)
            && source.ArtistUuid is { } titleArtist
                ? await ReadRepertoireTitleIndex(c, t, titleArtist, ct)
                : null;
        // Keep the original page boundaries: source aliases can connect rivals through another
        // claim on that page. Rechecking a negative alone can incorrectly make it bindable.
        foreach (var page in source.Claims.Chunk(preparation.PageSize))
        {
            if (!page.Any(claim => negative.ContainsKey(claim.ClaimUuid)))
            {
                continue;
            }
            var contexts = new List<RepertoireResolutionClaimContext>();
            foreach (var claim in page)
            {
                var context = await ReadRepertoireClaimContext(
                    c,
                    t,
                    registrationUuid,
                    source.ArtistUuid,
                    claim,
                    exclusions,
                    ct,
                    titleIndex
                );
                contexts.Add(context);
                if (context.Candidates is { } snapshot)
                {
                    if (!RepertoireFreshnessComplete(snapshot.Issues))
                    {
                        throw new NormalizationConflictException(
                            "The final negative repertoire context has missing freshness stamps."
                        );
                    }
                    reads.Add(snapshot.ReadSet);
                }
            }
            foreach (var current in evaluator.DecidePage(contexts, pinnedPolicy.Active))
            {
                if (!negative.Remove(current.ClaimUuid, out var saved))
                {
                    continue;
                }
                if (
                    current.CorrespondenceKey != saved.CorrespondenceKey
                    || current.Disposition != saved.Disposition
                    || current.Reason != saved.Reason
                    || current.RepertoireSongUuid != saved.RepertoireSongUuid
                    || current.Authority != saved.Authority
                )
                {
                    throw new NormalizationConflictException(
                        "A saved negative repertoire decision changed before source acknowledgment."
                    );
                }
            }
        }
        if (negative.Count != 0)
        {
            throw new NormalizationConflictException(
                "A saved negative repertoire decision has no exact source claim."
            );
        }
        return reads.ToArray();
    }

    /// <summary>
    /// Retirement changes contributor membership only. Source invalidation is precisely why
    /// cleanup can be necessary, so it reads current peer coordination without reauthorizing
    /// source claims or requiring their former input heads to remain current.
    /// </summary>
    public async Task<RepertoireResolutionPageContext> ReadRepertoireResolutionRetirementContextAsync(
        Guid registrationUuid,
        Guid artistUuid,
        RepertoireResolutionPolicyState pinnedPolicy,
        CancellationToken ct = default
    )
    {
        if (registrationUuid == Guid.Empty || artistUuid == Guid.Empty)
        {
            throw new ArgumentException(
                "Retirement requires exact registration and artist identities."
            );
        }
        await using var c = db.CreateConnection(false, false);
        await c.OpenAsync(ct);
        await using var t = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await c.ExecuteAsync(
            new CommandDefinition(
                "SET TRANSACTION READ ONLY",
                transaction: t,
                cancellationToken: ct
            )
        );
        var facts = new CandidateFactsContext();
        var owners = await ReadRepertoireOwners(c, t, registrationUuid, artistUuid, facts, ct);
        var issues = facts
            .Issues.Where(value => value.PreventsAutomaticAcceptance)
            .Select(value => "owner_context:" + value.Kind + ":" + value.FactKind)
            .ToArray();
        if (owners.Artist?.Membership.Owner == null)
        {
            throw new NormalizationConflictException("The repertoire retirement owner is missing.");
        }
        await t.CommitAsync(ct);
        return new(
            artistUuid,
            [],
            new([], [], [], [], null, [], [], [], []),
            owners.Observation,
            owners.Artist,
            pinnedPolicy,
            issues,
            RepertoireFreshnessComplete(facts.Issues)
        );
    }

    private static async Task ValidateRepertoireDecisionRevisions(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid attemptUuid,
        RepertoireResolutionClaimDecision[] accepted,
        List<string> issues,
        CancellationToken ct
    )
    {
        // The final snapshot may observe our own successful writes. It may not adopt another
        // writer's newer title revision merely because the same UUID still exists. Read compact
        // proof coordinates from immutable saved pages and successful Work revision receipts.
        var invalid = await c.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                WITH decisions AS (
                    SELECT * FROM jsonb_to_recordset(@decisions::jsonb)
                        AS d("ClaimUuid" uuid,"RepertoireSongUuid" uuid)
                ), pages AS (
                    SELECT s.ordinal,c.value AS context FROM normalization.repertoire_resolution_stages s
                    CROSS JOIN LATERAL jsonb_array_elements(s.stage->'Context'->'Claims') c
                    WHERE s.attempt_uuid=@attemptUuid AND (s.stage->>'Kind')::integer=3 AND NOT s.superseded
                ), original AS (
                    SELECT (p.context->'Claim'->>'ClaimUuid')::uuid AS claim_uuid,
                        (f.value->'Header'->>'Uuid')::uuid AS target_uuid,
                        (f.value->'Header'->>'Revision')::bigint AS revision,p.ordinal
                    FROM pages p CROSS JOIN LATERAL jsonb_array_elements(p.context->'Candidates'->'Facts') f
                ), own_writes AS (
                    SELECT r.entity_uuid,r.revision,s.ordinal FROM normalization.repertoire_resolution_stages s
                    JOIN normalization.music_revisions r ON r.decision_uuid=s.work_uuid AND r.entity_kind='repertoire_songs'
                    WHERE s.attempt_uuid=@attemptUuid AND (s.stage->>'Kind')::integer=0 AND s.receipt IS NOT NULL
                )
                SELECT EXISTS(SELECT 1 FROM decisions d
                    LEFT JOIN normalization.repertoire_songs song ON song.uuid=d."RepertoireSongUuid"
                    LEFT JOIN normalization.music_revisions current_revision ON current_revision.uuid=song.revision_uuid
                    WHERE current_revision.revision IS DISTINCT FROM COALESCE(
                        (SELECT w.revision FROM own_writes w WHERE w.entity_uuid=d."RepertoireSongUuid" ORDER BY w.ordinal DESC LIMIT 1),
                        (SELECT o.revision FROM original o WHERE o.claim_uuid=d."ClaimUuid" AND o.target_uuid=d."RepertoireSongUuid"
                            ORDER BY o.ordinal DESC LIMIT 1)))
                """,
                new
                {
                    attemptUuid,
                    decisions = System.Text.Json.JsonSerializer.Serialize(
                        accepted.Select(value => new { value.ClaimUuid, value.RepertoireSongUuid })
                    ),
                },
                t,
                cancellationToken: ct
            )
        );
        if (invalid)
        {
            issues.Add("accepted_correspondence_revision_changed");
        }
    }

    private static async Task<Dictionary<Guid, Guid>> ResolveRepertoireCompletionTargets(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid artist,
        Guid[] targets,
        List<string> issues,
        CancellationToken ct
    )
    {
        // Historical associations keep their stored ancestor UUID and immutable proof. Resolve
        // that coordinate through current same-artist headers, without rewriting the association.
        // The terminal revision, artist owner and both candidate epochs are already fenced by
        // this completion snapshot; foreign paths cannot supply new owner authority.
        var known = new Dictionary<Guid, RepertoireCompletionHeader>();
        var pending = targets;
        while (pending.Length > 0)
        {
            if (known.Count + pending.Length > 100000)
            {
                throw new InvalidOperationException(
                    "The repertoire completion identity closure exceeds its bound."
                );
            }
            var rows = (
                await c.QueryAsync<RepertoireCompletionHeader>(
                    new CommandDefinition(
                        """
                        SELECT uuid AS "Uuid",artist_uuid AS "ArtistUuid",retracted AS "Retracted",redirect_uuid AS "RedirectUuid"
                        FROM normalization.repertoire_songs WHERE uuid=ANY(@pending) ORDER BY uuid
                        """,
                        new { pending },
                        t,
                        cancellationToken: ct
                    )
                )
            ).ToArray();
            if (rows.Length != pending.Length)
            {
                issues.Add("accepted_repertoire_target_missing");
            }
            foreach (var row in rows)
            {
                known[row.Uuid] = row;
            }
            pending = rows.Where(row =>
                    row.RedirectUuid.HasValue && !known.ContainsKey(row.RedirectUuid.Value)
                )
                .Select(row => row.RedirectUuid!.Value)
                .Distinct()
                .ToArray();
        }
        var resolved = new Dictionary<Guid, Guid>();
        foreach (var target in targets)
        {
            var visited = new HashSet<Guid>();
            var current = target;
            while (known.TryGetValue(current, out var row))
            {
                if (!visited.Add(current) || row.Retracted || row.ArtistUuid != artist)
                {
                    issues.Add("accepted_repertoire_target_invalid");
                    break;
                }
                if (row.RedirectUuid is not { } next)
                {
                    resolved[target] = current;
                    break;
                }
                current = next;
            }
        }
        return resolved;
    }

    private static bool RepertoireCompletionExcluded(
        MusicPublication publication,
        RepertoireResolutionClaim claim,
        Guid? target
    ) =>
        publication.RepertoireSongs.Any(song =>
            song.Uuid == target
            || song.DisplayTitle == claim.Title?.Value
                && song.Evidence.Any(citation => citation == claim.Title?.Evidence)
        )
        || publication.ImportedSongs.Any(value => value.ImportedSongUuid == claim.ImportedSongUuid)
        || publication.ExternalIdentities.Any(value =>
            claim
                .Identities.Concat(claim.AliasOf is { } alias ? [alias] : [])
                .Any(identity =>
                    identity.Identity
                    == new NormalizationResourceKey(
                        value.Provider,
                        value.ResourceType,
                        value.ProviderKey
                    )
                )
        );

    private sealed record RepertoireCompletionAssociation(
        string CorrespondenceKey,
        Guid? RepertoireSongUuid,
        string Disposition
    );

    private sealed record RepertoireCompletionHeader(
        Guid Uuid,
        Guid ArtistUuid,
        bool Retracted,
        Guid? RedirectUuid
    );
}
