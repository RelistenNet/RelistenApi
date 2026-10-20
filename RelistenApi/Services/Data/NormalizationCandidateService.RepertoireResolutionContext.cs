using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    public async Task<RepertoireResolutionPageContext> ReadRepertoireResolutionContextAsync(
        Guid registrationUuid,
        RepertoireResolutionSourceSnapshot source,
        int pageIndex,
        int pageSize,
        RepertoireResolutionPolicyState pinnedPolicy,
        CancellationToken ct = default
    )
    {
        if (registrationUuid == Guid.Empty || pageIndex < 0 || pageSize is < 1 or > 100)
        {
            throw new ArgumentException(
                "Use a registration UUID and a bounded nonnegative claim page."
            );
        }
        var offset = checked(pageIndex * pageSize);
        if (offset > source.Claims.Count)
        {
            throw new ArgumentException("The claim page is outside the complete source.");
        }
        var claims = source.Claims.Skip(offset).Take(pageSize).ToArray();
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
        var factsContext = new CandidateFactsContext();
        var issues = source.Issues.ToList();
        var owners = await ReadRepertoireOwners(
            c,
            t,
            registrationUuid,
            source.ArtistUuid,
            factsContext,
            ct
        );
        var exclusions = await ReadRepertoireExclusions(
            c,
            t,
            owners.Artist?.Membership.Owner?.SubjectUuid,
            ct
        );
        var titleIndex =
            RepertoireResolutionPolicyEvaluator.SupportsTitleReuse(pinnedPolicy.Active)
            && source.ArtistUuid is { } titleArtist
                ? await ReadRepertoireTitleIndex(c, t, titleArtist, ct)
                : null;
        var contexts = new List<RepertoireResolutionClaimContext>();
        var decisionReads = new List<CandidateFactsReadSet>();
        var freshnessComplete = true;
        foreach (var claim in claims)
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
                decisionReads.Add(snapshot.ReadSet);
                freshnessComplete &= RepertoireFreshnessComplete(snapshot.Issues);
            }
        }
        var finalUniverses = await ReadRepertoireContextUniverses(c, t, source.ArtistUuid, ct);
        var finalRead = await factsContext.ReadReadSetAsync(c, t, finalUniverses, ct);
        if (factsContext.Issues.Any(issue => issue.PreventsAutomaticAcceptance))
        {
            issues.AddRange(
                factsContext
                    .Issues.Where(issue => issue.PreventsAutomaticAcceptance)
                    .Select(issue => "candidate_context:" + issue.Kind + ":" + issue.FactKind)
            );
        }
        var merged = MergeRepertoireContextReadSet(
            source.ReadSet,
            decisionReads.Append(finalRead),
            pinnedPolicy.OwnerObservation
        );
        await t.CommitAsync(ct);
        return new(
            source.ArtistUuid,
            contexts,
            merged,
            owners.Observation,
            owners.Artist,
            pinnedPolicy,
            issues.Distinct().ToArray(),
            freshnessComplete && RepertoireFreshnessComplete(factsContext.Issues, source)
        );
    }

    private static NormalizationCandidateQuery RepertoireClaimQuery(
        Guid artist,
        RepertoireResolutionClaim claim,
        CandidateFactsContext context
    )
    {
        var names = claim
            .Aliases.Select(alias => alias.Value)
            .Concat(claim.Title is { } title ? [title.Value] : [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var validNames = names
            .Where(name =>
                !string.IsNullOrWhiteSpace(name)
                && NormalizedPeopleService.NameKey(name.Trim()).EnumerateRunes().Count()
                    is >= 1
                        and <= 200
                && name.Any(char.IsLetterOrDigit)
            )
            .ToArray();
        var identities = claim
            .Identities.Concat(claim.AliasOf is { } aliasOf ? [aliasOf] : [])
            .Select(identity => new MatchingExternalIdentity(
                identity.Identity.Provider,
                identity.Identity.ResourceType,
                identity.Identity.ProviderKey
            ))
            .Distinct()
            .ToArray();
        if (names.Length != validNames.Length || names.Length > 16 || identities.Length > 16)
        {
            context.AddIssue(
                CandidateFactsIssueKind.InvalidStoredFact,
                factKind: "claim_query_limit"
            );
        }
        return new(
            IdentityMatchingDomain.Repertoire,
            validNames.Take(16).ToArray(),
            identities.Take(16).ToArray(),
            artist
        );
    }

    private static async Task<NormalizationCandidateSet> IncludeRepertoireTargets(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid artist,
        NormalizationCandidateSet selection,
        Guid[] targets,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        if (targets.Length == 0)
        {
            return selection;
        }
        var raw = targets
            .Select(uuid => new RawHit { OriginUuid = uuid, Reason = "retained_correspondence" })
            .ToArray();
        var rows = await ResolveTargets(
            c,
            t,
            DomainTables.For(IdentityMatchingDomain.Repertoire),
            raw,
            ct
        );
        var valid = (
            await c.QueryAsync<Guid>(
                new CommandDefinition(
                    "SELECT uuid FROM normalization.repertoire_songs WHERE uuid=ANY(@ids) AND artist_uuid=@artist AND NOT retracted AND redirect_uuid IS NULL",
                    new { ids = rows.Select(row => row.Uuid).Distinct().ToArray(), artist },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToHashSet();
        foreach (
            var target in targets.Where(uuid =>
                !rows.Any(row => row.OriginUuid == uuid && valid.Contains(row.Uuid))
            )
        )
        {
            context.AddIssue(
                CandidateFactsIssueKind.MissingRelatedIdentity,
                target,
                "retained_repertoire_target"
            );
        }
        var extra = rows.Where(row => valid.Contains(row.Uuid))
            .Select(row => new NormalizationCandidateHit(
                row.Uuid,
                row.Revision,
                row.DisplayText,
                row.OwnerSubjectUuid,
                0,
                ["retained_correspondence"]
            ));
        return selection with
        {
            Items = selection.Items.Concat(extra).DistinctBy(hit => hit.Uuid).ToArray(),
        };
    }

    private static Task<NormalizationCandidateUniverseDependency[]> ReadRepertoireContextUniverses(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid? artist,
        CancellationToken ct
    ) =>
        ReadFactsUniversesAsync(
            c,
            t,
            artist is { } uuid
                ? new(IdentityMatchingDomain.Repertoire, [], [], uuid)
                : new(IdentityMatchingDomain.Compositions, [], []),
            ct
        );
}
