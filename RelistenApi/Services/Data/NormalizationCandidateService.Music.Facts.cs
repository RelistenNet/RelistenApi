using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    private static async Task<RepertoireCandidateFacts[]> ReadRepertoireFactsAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationCandidateSet selection,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        var result = new List<RepertoireCandidateFacts>();
        foreach (var hit in selection.Items)
        {
            var identities = await ReadMusicAncestors(c, t, hit, true, limits, context, ct);
            var header = identities.SingleOrDefault(row => row.Uuid == hit.Uuid);
            if (header?.ArtistUuid is not { } artist)
            {
                context.AddIssue(
                    CandidateFactsIssueKind.InvalidStoredFact,
                    hit.Uuid,
                    "repertoire_identity"
                );
                continue;
            }
            context.AddArtist(artist);
            var titles = await ReadMusicTitles(
                c,
                t,
                identities,
                true,
                hit.Uuid,
                limits,
                context,
                ct
            );
            var identifiers = await ReadMusicIdentifiers(
                c,
                t,
                identities,
                true,
                hit.Uuid,
                limits,
                context,
                ct
            );
            var links = await ReadRepertoireLinks(c, t, identities, hit.Uuid, limits, context, ct);
            result.Add(
                new RepertoireCandidateFacts
                {
                    Hit = hit,
                    Header = header.Header(),
                    ArtistUuid = artist,
                    IdentityClosure = identities.Select(row => row.Header()).ToArray(),
                    Redirects = MusicRedirects(identities),
                    Titles = titles,
                    ExternalIdentities = identifiers,
                    CompositionLinks = links.Links,
                    CompositionIdentities = links.Identities,
                    CompositionRedirects = links.Redirects,
                }
            );
        }
        return result.ToArray();
    }

    private static async Task<CompositionCandidateFacts[]> ReadCompositionFactsAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationCandidateSet selection,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        var result = new List<CompositionCandidateFacts>();
        foreach (var hit in selection.Items)
        {
            var identities = await ReadMusicAncestors(c, t, hit, false, limits, context, ct);
            var header = identities.SingleOrDefault(row => row.Uuid == hit.Uuid);
            if (header == null)
            {
                context.AddIssue(
                    CandidateFactsIssueKind.MissingRelatedIdentity,
                    hit.Uuid,
                    "composition"
                );
                continue;
            }
            var titles = await ReadMusicTitles(
                c,
                t,
                identities,
                false,
                hit.Uuid,
                limits,
                context,
                ct
            );
            var identifiers = await ReadMusicIdentifiers(
                c,
                t,
                identities,
                false,
                hit.Uuid,
                limits,
                context,
                ct
            );
            var authorship = await ReadCompositionAuthorshipRows(
                c,
                t,
                identities,
                hit.Uuid,
                limits,
                context,
                ct
            );
            var credits = await ReadCompositionCredits(
                c,
                t,
                identities,
                authorship
                    .SelectMany(row => row.Authors)
                    .Where(author => author.PersonUuid.HasValue)
                    .Select(author => author.PersonUuid!.Value)
                    .ToArray(),
                hit.Uuid,
                limits,
                context,
                ct
            );
            var authorFacts = AuthorshipFacts(
                authorship,
                identities,
                credits.People,
                hit.Uuid,
                limits,
                context
            );
            // An omitted member or conflicting account cannot expose a convenient complete
            // subset. Source completeness remains on each raw assertion for inspection.
            var completeAuthors = context.Issues.Any(issue =>
                issue.PreventsAutomaticAcceptance
                && (issue.CandidateUuid == hit.Uuid || issue.CandidateUuid == null)
            )
                ? null
                : CompositionAuthorshipAgreement.CompleteAuthors(
                    authorFacts.Select(assertion =>
                        (
                            assertion.CompleteAuthorship,
                            (IReadOnlyList<Guid?>)
                                assertion
                                    .Authors.Select(author => author.ResolvedPersonUuid)
                                    .ToArray()
                        )
                    ),
                    credits
                        .Credits.Where(credit =>
                            CompositionAuthorshipAgreement.IsAuthorRole(credit.Role)
                        )
                        .Select(credit => credit.ResolvedPersonUuid)
                );
            result.Add(
                new CompositionCandidateFacts
                {
                    Hit = hit,
                    Header = header.Header(),
                    IdentityClosure = identities.Select(row => row.Header()).ToArray(),
                    Redirects = MusicRedirects(identities),
                    Titles = titles,
                    ExternalIdentities = identifiers,
                    WorkCredits = credits.Credits,
                    PersonCreditTargets = credits.People,
                    ArtistCreditTargets = credits.Artists,
                    AuthorshipAssertions = authorFacts,
                    AuthorUuids = completeAuthors ?? new HashSet<Guid>(),
                }
            );
        }
        return result.ToArray();
    }

    private static async Task<MusicFactIdentity[]> ReadMusicAncestors(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationCandidateHit hit,
        bool repertoire,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        var table = repertoire ? "repertoire_songs" : "compositions";
        var artistScope = repertoire
            ? "AND x.artist_uuid=(SELECT artist_uuid FROM normalization.repertoire_songs WHERE uuid=@uuid)"
            : "";
        // Keep the recursive projection unsorted and unjoined until its LIMIT. PostgreSQL can
        // stop enumeration at the bound instead of materializing every ancestor before sorting.
        var ids = (
            await c.QueryAsync<Guid>(
                new CommandDefinition(
                    $"""
                    WITH RECURSIVE ancestors(uuid) AS (
                        SELECT uuid FROM normalization.{table} WHERE uuid=@uuid AND NOT retracted
                        UNION
                        SELECT x.uuid FROM normalization.{table} x JOIN ancestors a ON x.redirect_uuid=a.uuid
                        WHERE NOT x.retracted {artistScope}
                    ) SELECT uuid FROM ancestors LIMIT @take
                    """,
                    new { uuid = hit.Uuid, take = limits.MaximumIdentityClosure + 1 },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        ids = BoundMusicRows(
            ids,
            limits.MaximumIdentityClosure,
            context,
            hit.Uuid,
            "identity_closure",
            CandidateFactsIssueKind.IdentityClosureLimit
        );
        return await ReadMusicHeaders(c, t, ids, repertoire, hit.Uuid, context, ct);
    }

    private static async Task<MusicFactIdentity[]> ReadMusicHeaders(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid[] ids,
        bool repertoire,
        Guid candidate,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        if (ids.Length == 0)
        {
            return [];
        }
        var table = repertoire ? "repertoire_songs" : "compositions";
        var artist = repertoire ? "x.artist_uuid" : "NULL::uuid";
        var owner = repertoire ? "x.artist_uuid" : "x.uuid";
        var rows = (
            await c.QueryAsync<MusicFactIdentity>(
                new CommandDefinition(
                    $"""
                    SELECT x.uuid AS "Uuid",x.display_title AS "DisplayText",r.revision AS "Revision",
                        x.redirect_uuid AS "RedirectUuid",x.retracted AS "Retracted",{artist} AS "ArtistUuid",
                        x.revision_uuid AS "MusicRevisionUuid",r.decision_uuid AS "DecisionUuid",owner.uuid AS "OwnerSubjectUuid"
                    FROM normalization.{table} x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                    LEFT JOIN normalization.work_subjects owner ON owner.domain=@domain AND owner.subject_kind=@kind AND owner.subject_uuid={owner}
                    WHERE x.uuid=ANY(@ids) ORDER BY x.uuid
                    """,
                    new
                    {
                        ids,
                        domain = repertoire ? "songs" : "compositions",
                        kind = repertoire ? "artist_repertoire" : "composition",
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        foreach (var row in rows)
        {
            context.AddOwner(row.OwnerSubjectUuid, candidate);
        }
        return rows;
    }

    private static async Task<CandidateMusicTitleFact[]> ReadMusicTitles(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        MusicFactIdentity[] identities,
        bool repertoire,
        Guid candidate,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        var table = repertoire ? "repertoire_songs" : "compositions";
        var rows = (
            await c.QueryAsync<MusicTitleRow>(
                new CommandDefinition(
                    $"""
                    SELECT x.uuid AS "Uuid",x.uuid AS "StoredIdentityUuid",r.revision AS "Revision",r.basis AS "Basis",
                        r.uuid AS "MusicRevisionUuid",x.display_title AS "DisplayTitle",
                        ARRAY(SELECT alias FROM unnest(x.aliases) WITH ORDINALITY a(alias,ordinal) ORDER BY ordinal LIMIT @take) AS "Aliases",
                        {MusicEvidenceProjection}
                    FROM normalization.{table} x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                    WHERE x.uuid=ANY(@ids) AND NOT x.retracted ORDER BY x.uuid LIMIT @take
                    """,
                    new
                    {
                        ids = identities.Select(row => row.Uuid).ToArray(),
                        take = limits.MaximumAssertionsPerKind + 1,
                        evidenceTake = limits.MaximumEvidencePerAssertion + 1,
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        rows = BoundMusicRows(rows, limits.MaximumAssertionsPerKind, context, candidate, "titles");
        return rows.Select(row => new CandidateMusicTitleFact(
                MusicProvenance(
                    row,
                    identities
                        .Single(identity => identity.Uuid == row.StoredIdentityUuid)
                        .OwnerSubjectUuid,
                    candidate,
                    limits,
                    context,
                    "title"
                ),
                row.DisplayTitle,
                BoundMusicRows(
                    row.Aliases,
                    limits.MaximumAssertionsPerKind,
                    context,
                    candidate,
                    "aliases"
                )
            ))
            .ToArray();
    }

    private static async Task<CandidateExternalIdentityFact[]> ReadMusicIdentifiers(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        MusicFactIdentity[] identities,
        bool repertoire,
        Guid candidate,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        var column = repertoire ? "repertoire_song_uuid" : "composition_uuid";
        var artistCondition = repertoire ? "AND x.artist_uuid=@artist" : "";
        var rows = (
            await c.QueryAsync<MusicIdentifierRow>(
                new CommandDefinition(
                    $"""
                    SELECT x.uuid AS "Uuid",x.{column} AS "StoredIdentityUuid",r.revision AS "Revision",r.basis AS "Basis",
                        r.uuid AS "MusicRevisionUuid",x.provider AS "Provider",x.resource_type AS "ResourceType",x.provider_key AS "ProviderKey",
                        {MusicEvidenceProjection}
                    FROM normalization.music_external_identities x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                    WHERE x.{column}=ANY(@ids) AND NOT x.retracted {artistCondition} ORDER BY x.uuid LIMIT @take
                    """,
                    new
                    {
                        ids = identities.Select(row => row.Uuid).ToArray(),
                        artist = identities.FirstOrDefault()?.ArtistUuid,
                        take = limits.MaximumAssertionsPerKind + 1,
                        evidenceTake = limits.MaximumEvidencePerAssertion + 1,
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        rows = BoundMusicRows(
            rows,
            limits.MaximumAssertionsPerKind,
            context,
            candidate,
            "external_identities"
        );
        if (rows.Length > 0)
        {
            context.AddIssue(
                CandidateFactsIssueKind.UnknownAuthority,
                candidate,
                "music_external_identity"
            );
        }
        return rows.Select(row => new CandidateExternalIdentityFact(
                MusicProvenance(
                    row,
                    identities
                        .Single(identity => identity.Uuid == row.StoredIdentityUuid)
                        .OwnerSubjectUuid,
                    candidate,
                    limits,
                    context,
                    "external_identity"
                ),
                row.Provider,
                row.ResourceType,
                row.ProviderKey
            ))
            .ToArray();
    }

    private static async Task<MusicLinkFacts> ReadRepertoireLinks(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        MusicFactIdentity[] identities,
        Guid candidate,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        var rows = (
            await c.QueryAsync<MusicLinkRow>(
                new CommandDefinition(
                    $"""
                    SELECT x.uuid AS "Uuid",x.repertoire_song_uuid AS "StoredIdentityUuid",x.composition_uuid AS "CompositionUuid",
                        r.revision AS "Revision",r.basis AS "Basis",r.uuid AS "MusicRevisionUuid",
                        r.payload->>'MatchReason' AS "MatchReason",r.payload->'MatchedWorkIdentity' AS "MatchedWorkIdentity",
                        ARRAY(SELECT author::text::uuid FROM jsonb_array_elements_text(COALESCE(r.payload->'MatchedAuthorUuids','[]'::jsonb)) author LIMIT @take) AS "MatchedAuthorUuids",
                        COALESCE((r.payload->>'CompleteAuthorship')::boolean,false) AS "CompleteAuthorship",
                        (r.payload->>'EvaluatedCorrespondenceReceiptUuid')::uuid AS "EvaluatedCorrespondenceReceiptUuid",{MusicEvidenceProjection}
                    FROM normalization.repertoire_compositions x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                    WHERE x.repertoire_song_uuid=ANY(@ids) AND NOT x.retracted ORDER BY x.uuid LIMIT @take
                    """,
                    new
                    {
                        ids = identities.Select(row => row.Uuid).ToArray(),
                        take = limits.MaximumAssertionsPerKind + 1,
                        evidenceTake = limits.MaximumEvidencePerAssertion + 1,
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        rows = BoundMusicRows(
            rows,
            limits.MaximumAssertionsPerKind,
            context,
            candidate,
            "composition_links"
        );
        var chains = new Dictionary<Guid, MusicForwardChain>();
        var relatedIdentities = new HashSet<Guid>();
        foreach (var uuid in rows.Select(row => row.CompositionUuid).Distinct().Order())
        {
            var known = chains.Values.FirstOrDefault(chain =>
                chain.Identities.Any(identity => identity.Uuid == uuid)
            );
            if (known != null)
            {
                chains[uuid] = known;
                continue;
            }
            var remaining = limits.MaximumIdentityClosure - relatedIdentities.Count;
            if (remaining == 0)
            {
                context.AddIssue(
                    CandidateFactsIssueKind.IdentityClosureLimit,
                    candidate,
                    "linked_composition_closure"
                );
                chains[uuid] = new(null, []);
                continue;
            }
            var chain = await ReadForwardComposition(
                c,
                t,
                uuid,
                candidate,
                limits with
                {
                    MaximumIdentityClosure = remaining,
                },
                context,
                ct
            );
            chains[uuid] = chain;
            relatedIdentities.UnionWith(chain.Identities.Select(identity => identity.Uuid));
        }
        var links = rows.Select(row => new RepertoireCompositionFact(
                MusicProvenance(
                    row,
                    identities
                        .Single(identity => identity.Uuid == row.StoredIdentityUuid)
                        .OwnerSubjectUuid,
                    candidate,
                    limits,
                    context,
                    "composition_link"
                ),
                row.StoredIdentityUuid,
                row.CompositionUuid,
                chains[row.CompositionUuid].ResolvedUuid,
                row.MatchReason ?? "",
                ReadMatchedWorkIdentity(row.MatchedWorkIdentity, candidate, context),
                BoundMusicRows(
                    row.MatchedAuthorUuids,
                    limits.MaximumAssertionsPerKind,
                    context,
                    candidate,
                    "matched_authors"
                ),
                row.CompleteAuthorship,
                row.EvaluatedCorrespondenceReceiptUuid
            ))
            .ToArray();
        return new(
            links,
            chains
                .Values.SelectMany(chain => chain.Identities)
                .DistinctBy(row => row.Uuid)
                .OrderBy(row => row.Uuid)
                .Select(row => row.Header())
                .ToArray(),
            chains
                .Values.SelectMany(chain => MusicRedirects(chain.Identities))
                .DistinctBy(row => row.OriginalUuid)
                .OrderBy(row => row.OriginalUuid)
                .ToArray()
        );
    }

    private static async Task<MusicForwardChain> ReadForwardComposition(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid uuid,
        Guid candidate,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        var paths = (
            await c.QueryAsync<Guid>(
                new CommandDefinition(
                    """
                    WITH RECURSIVE path(uuid,depth,visited) AS (
                        SELECT uuid,0,ARRAY[uuid] FROM normalization.compositions WHERE uuid=@uuid
                        UNION ALL
                        SELECT next.uuid,path.depth+1,path.visited||next.uuid FROM path
                        JOIN normalization.compositions current ON current.uuid=path.uuid
                        JOIN normalization.compositions next ON next.uuid=current.redirect_uuid
                        WHERE NOT current.retracted AND NOT next.uuid=ANY(path.visited) AND path.depth<@maximum
                    ) SELECT uuid FROM path ORDER BY depth LIMIT @take
                    """,
                    new
                    {
                        uuid,
                        maximum = limits.MaximumIdentityClosure,
                        take = limits.MaximumIdentityClosure + 1,
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var bounded = BoundMusicRows(
            paths,
            limits.MaximumIdentityClosure,
            context,
            candidate,
            "linked_composition_closure",
            CandidateFactsIssueKind.IdentityClosureLimit
        );
        var headers = await ReadMusicHeaders(c, t, bounded, false, candidate, context, ct);
        var last = bounded.Length == 0 ? null : headers.Single(row => row.Uuid == bounded[^1]);
        var resolved =
            paths.Length <= limits.MaximumIdentityClosure
            && last is { Retracted: false, RedirectUuid: null }
                ? last.Uuid
                : (Guid?)null;
        if (resolved == null)
        {
            context.AddIssue(
                CandidateFactsIssueKind.MissingRelatedIdentity,
                candidate,
                "linked_composition"
            );
        }
        return new(resolved, headers);
    }

    private static async Task<MusicCreditFacts> ReadCompositionCredits(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        MusicFactIdentity[] identities,
        Guid[] authorPeople,
        Guid candidate,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        var rows = (
            await c.QueryAsync<MusicCreditRow>(
                new CommandDefinition(
                    $"""
                    SELECT x.uuid AS "Uuid",x.composition_uuid AS "StoredIdentityUuid",r.revision AS "Revision",r.basis AS "Basis",
                        r.uuid AS "MusicRevisionUuid",x.role AS "Role",x.original_text AS "OriginalText",
                        x.person_uuid AS "PersonUuid",x.artist_uuid AS "ArtistUuid",{MusicEvidenceProjection}
                    FROM normalization.work_credits x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                    WHERE x.composition_uuid=ANY(@ids) AND NOT x.retracted ORDER BY x.uuid LIMIT @take
                    """,
                    new
                    {
                        ids = identities.Select(row => row.Uuid).ToArray(),
                        take = limits.MaximumAssertionsPerKind + 1,
                        evidenceTake = limits.MaximumEvidencePerAssertion + 1,
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        rows = BoundMusicRows(
            rows,
            limits.MaximumAssertionsPerKind,
            context,
            candidate,
            "work_credits"
        );
        var people = new Dictionary<Guid, CandidatePersonCreditTarget>();
        var personHeaderCount = 0;
        foreach (
            var uuid in rows.Where(row => row.PersonUuid.HasValue)
                .Select(row => row.PersonUuid!.Value)
                .Concat(authorPeople)
                .Distinct()
                .Order()
        )
        {
            var remaining = limits.MaximumIdentityClosure - personHeaderCount;
            var known = people.Values.FirstOrDefault(resolution =>
                resolution.IdentityClosure.Any(identity => identity.Uuid == uuid)
            );
            if (known != null)
            {
                var suffix = known
                    .IdentityClosure.SkipWhile(identity => identity.Uuid != uuid)
                    .ToArray();
                var bounded = BoundMusicRows(
                    suffix,
                    remaining,
                    context,
                    candidate,
                    "person_credit_target_closure",
                    CandidateFactsIssueKind.IdentityClosureLimit
                );
                var ids = bounded.Select(identity => identity.Uuid).ToHashSet();
                people[uuid] = new(
                    uuid,
                    known.ResolvedPersonUuid,
                    bounded,
                    known.Redirects.Where(redirect => ids.Contains(redirect.OriginalUuid)).ToArray()
                );
                personHeaderCount += bounded.Length;
                continue;
            }
            if (remaining == 0)
            {
                context.AddIssue(
                    CandidateFactsIssueKind.IdentityClosureLimit,
                    candidate,
                    "person_credit_target_closure"
                );
                people[uuid] = new(uuid, null, [], []);
                continue;
            }
            var resolution = await ReadMusicPersonResolution(
                c,
                t,
                uuid,
                candidate,
                limits with
                {
                    MaximumIdentityClosure = remaining,
                },
                context,
                ct
            );
            people[uuid] = resolution;
            personHeaderCount += resolution.IdentityClosure.Count;
        }
        var artists = await ReadMusicArtistCreditTargets(
            c,
            t,
            rows.Where(row => row.ArtistUuid.HasValue)
                .Select(row => row.ArtistUuid!.Value)
                .Distinct()
                .Order()
                .ToArray(),
            candidate,
            limits,
            context,
            ct
        );
        var credits = rows.Select(row => new CompositionWorkCreditFact(
                MusicProvenance(
                    row,
                    identities
                        .Single(identity => identity.Uuid == row.StoredIdentityUuid)
                        .OwnerSubjectUuid,
                    candidate,
                    limits,
                    context,
                    "work_credit"
                ),
                row.StoredIdentityUuid,
                row.Role,
                row.OriginalText,
                row.PersonUuid,
                row.PersonUuid is { } person ? people[person].ResolvedPersonUuid : null,
                row.ArtistUuid
            ))
            .ToArray();
        return new(credits, people.Values.ToArray(), artists);
    }

    private static async Task<CandidatePersonCreditTarget> ReadMusicPersonResolution(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid uuid,
        Guid candidate,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        var rows = (
            await c.QueryAsync<MusicPersonRow>(
                new CommandDefinition(
                    """
                    WITH RECURSIVE path(uuid,depth,visited) AS (
                        SELECT uuid,0,ARRAY[uuid] FROM normalization.people WHERE uuid=@uuid
                        UNION ALL
                        SELECT next.uuid,path.depth+1,path.visited||next.uuid FROM path
                        JOIN normalization.person_redirects redirect ON redirect.retired_uuid=path.uuid
                        JOIN normalization.people next ON next.uuid=redirect.survivor_uuid
                        WHERE NOT next.uuid=ANY(path.visited) AND path.depth<@maximum
                    )
                    SELECT p.uuid AS "Uuid",p.display_name AS "DisplayText",p.revision AS "Revision",p.kind AS "Kind",
                        p.metadata_reviewed AS "MetadataReviewed",owner.uuid AS "OwnerSubjectUuid",
                        redirect.survivor_uuid AS "RedirectUuid",redirect.decision_uuid AS "DecisionUuid"
                    FROM path JOIN normalization.people p ON p.uuid=path.uuid
                    LEFT JOIN normalization.person_redirects redirect ON redirect.retired_uuid=p.uuid
                    LEFT JOIN normalization.work_subjects owner ON owner.domain='people' AND owner.subject_kind='person' AND owner.subject_uuid=p.uuid
                    ORDER BY path.depth LIMIT @take
                    """,
                    new
                    {
                        uuid,
                        maximum = limits.MaximumIdentityClosure,
                        take = limits.MaximumIdentityClosure + 1,
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var bounded = BoundMusicRows(
            rows,
            limits.MaximumIdentityClosure,
            context,
            candidate,
            "person_credit_target_closure",
            CandidateFactsIssueKind.IdentityClosureLimit
        );
        foreach (var row in bounded)
        {
            context.AddOwner(row.OwnerSubjectUuid, candidate);
        }
        var resolved =
            rows.Length <= limits.MaximumIdentityClosure
            && bounded.LastOrDefault() is { RedirectUuid: null } terminal
                ? terminal.Uuid
                : (Guid?)null;
        if (resolved == null)
        {
            context.AddIssue(
                CandidateFactsIssueKind.MissingRelatedIdentity,
                candidate,
                "person_credit_target"
            );
        }
        return new(
            uuid,
            resolved,
            bounded
                .Select(row => new CandidateIdentityHeader(
                    row.Uuid,
                    row.DisplayText,
                    row.Revision,
                    row.OwnerSubjectUuid,
                    row.Kind,
                    row.MetadataReviewed
                ))
                .ToArray(),
            bounded
                .Where(row => row.RedirectUuid.HasValue)
                .Select(row => new CandidateRedirectFact(
                    row.Uuid,
                    row.RedirectUuid!.Value,
                    row.DecisionUuid
                ))
                .ToArray()
        );
    }

    private static async Task<CandidateArtistCreditTarget[]> ReadMusicArtistCreditTargets(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid[] uuids,
        Guid candidate,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        uuids = BoundMusicRows(
            uuids,
            limits.MaximumArtistContexts,
            context,
            candidate,
            "artist_credit_targets",
            CandidateFactsIssueKind.ArtistContextLimit
        );
        if (uuids.Length == 0)
        {
            return [];
        }
        var artists = (
            await c.QueryAsync<MusicArtistRow>(
                new CommandDefinition(
                    "SELECT uuid AS \"Uuid\",name AS \"Name\",slug AS \"Slug\" FROM artists WHERE uuid=ANY(@uuids)",
                    new { uuids },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToDictionary(row => row.Uuid);
        return uuids
            .Select(uuid =>
            {
                context.AddArtist(uuid);
                if (!artists.TryGetValue(uuid, out var artist))
                {
                    context.AddIssue(
                        CandidateFactsIssueKind.MissingCatalogContext,
                        candidate,
                        "artist_credit_target"
                    );
                    return new CandidateArtistCreditTarget(uuid, false, null, null);
                }
                return new CandidateArtistCreditTarget(uuid, true, artist.Name, artist.Slug);
            })
            .ToArray();
    }

    // Citation JSON is immutable accepted revision data. Limit expansion on the server; the
    // appendable music_evidence_references search index cannot add support to current facts.
    private const string MusicEvidenceProjection = """
        CASE WHEN jsonb_typeof(r.evidence)='array' THEN
            (SELECT COALESCE(jsonb_agg(e.value ORDER BY e.ordinal),'[]'::jsonb)
                FROM (SELECT value,ordinality AS ordinal FROM jsonb_array_elements(r.evidence) WITH ORDINALITY LIMIT @evidenceTake) e)
            ELSE '[]'::jsonb END::text AS "EvidenceJson",
        jsonb_typeof(r.evidence) IS DISTINCT FROM 'array' AS "InvalidEvidence"
        """;

    private static CandidateAssertionProvenance MusicProvenance(
        MusicAssertionRow row,
        Guid? owner,
        Guid candidate,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        string kind
    )
    {
        context.AddOwner(owner, candidate);
        NormalizedEvidenceReference[] evidence;
        try
        {
            evidence =
                JsonSerializer.Deserialize<NormalizedEvidenceReference[]>(row.EvidenceJson) ?? [];
        }
        catch (JsonException)
        {
            evidence = [];
            context.AddIssue(CandidateFactsIssueKind.InvalidStoredFact, candidate, kind);
        }
        if (
            row.InvalidEvidence
            || evidence.Length == 0
            || evidence.Any(item =>
                item == null || item.InputRevisionUuid == Guid.Empty || item.JsonPointer == null
            )
        )
        {
            context.AddIssue(CandidateFactsIssueKind.InvalidStoredFact, candidate, kind);
            evidence = evidence
                .Where(item =>
                    item != null && item.InputRevisionUuid != Guid.Empty && item.JsonPointer != null
                )
                .ToArray();
        }
        evidence = BoundMusicRows(
            evidence,
            limits.MaximumEvidencePerAssertion,
            context,
            candidate,
            kind,
            CandidateFactsIssueKind.EvidenceLimit
        );
        return new(
            row.Uuid,
            row.StoredIdentityUuid,
            owner,
            row.Revision,
            row.Basis,
            evidence,
            row.MusicRevisionUuid
        );
    }

    private static NormalizationResourceKey? ReadMatchedWorkIdentity(
        string? json,
        Guid candidate,
        CandidateFactsContext context
    )
    {
        if (json == null)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<NormalizationResourceKey>(json);
        }
        catch (JsonException)
        {
            context.AddIssue(
                CandidateFactsIssueKind.InvalidStoredFact,
                candidate,
                "matched_work_identity"
            );
            return null;
        }
    }

    private static T[] BoundMusicRows<T>(
        T[] rows,
        int maximum,
        CandidateFactsContext context,
        Guid candidate,
        string kind,
        CandidateFactsIssueKind issue = CandidateFactsIssueKind.AssertionLimit
    )
    {
        if (rows.Length > maximum)
        {
            context.AddIssue(issue, candidate, kind);
            return rows.Take(maximum).ToArray();
        }
        return rows;
    }

    private static CandidateRedirectFact[] MusicRedirects(IEnumerable<MusicFactIdentity> rows) =>
        rows.Where(row => row.RedirectUuid.HasValue)
            .Select(row => new CandidateRedirectFact(
                row.Uuid,
                row.RedirectUuid!.Value,
                row.DecisionUuid,
                row.MusicRevisionUuid
            ))
            .ToArray();

    private sealed record MusicForwardChain(Guid? ResolvedUuid, MusicFactIdentity[] Identities);

    private sealed record MusicLinkFacts(
        RepertoireCompositionFact[] Links,
        CandidateIdentityHeader[] Identities,
        CandidateRedirectFact[] Redirects
    );

    private sealed record MusicCreditFacts(
        CompositionWorkCreditFact[] Credits,
        CandidatePersonCreditTarget[] People,
        CandidateArtistCreditTarget[] Artists
    );

    private sealed record MusicArtistRow(Guid Uuid, string? Name, string? Slug);

    private sealed class MusicFactIdentity
    {
        public Guid Uuid { get; init; }
        public string DisplayText { get; init; } = null!;
        public long Revision { get; init; }
        public Guid? OwnerSubjectUuid { get; init; }
        public Guid? ArtistUuid { get; init; }
        public Guid? RedirectUuid { get; init; }
        public bool Retracted { get; init; }
        public Guid MusicRevisionUuid { get; init; }
        public Guid DecisionUuid { get; init; }

        public CandidateIdentityHeader Header() =>
            new(Uuid, DisplayText, Revision, OwnerSubjectUuid, ArtistUuid: ArtistUuid);
    }

    private class MusicAssertionRow
    {
        public Guid Uuid { get; init; }
        public Guid StoredIdentityUuid { get; init; }
        public long Revision { get; init; }
        public string Basis { get; init; } = null!;
        public Guid MusicRevisionUuid { get; init; }
        public string EvidenceJson { get; init; } = null!;
        public bool InvalidEvidence { get; init; }
    }

    private sealed class MusicTitleRow : MusicAssertionRow
    {
        public string DisplayTitle { get; init; } = null!;
        public string[] Aliases { get; init; } = [];
    }

    private sealed class MusicIdentifierRow : MusicAssertionRow
    {
        public string Provider { get; init; } = null!;
        public string ResourceType { get; init; } = null!;
        public string ProviderKey { get; init; } = null!;
    }

    private sealed class MusicLinkRow : MusicAssertionRow
    {
        public Guid CompositionUuid { get; init; }
        public string? MatchReason { get; init; }
        public string? MatchedWorkIdentity { get; init; }
        public Guid[] MatchedAuthorUuids { get; init; } = [];
        public bool CompleteAuthorship { get; init; }
        public Guid? EvaluatedCorrespondenceReceiptUuid { get; init; }
    }

    private sealed class MusicCreditRow : MusicAssertionRow
    {
        public string Role { get; init; } = null!;
        public string OriginalText { get; init; } = null!;
        public Guid? PersonUuid { get; init; }
        public Guid? ArtistUuid { get; init; }
    }

    private sealed record MusicPersonRow(
        Guid Uuid,
        string DisplayText,
        long Revision,
        string Kind,
        bool MetadataReviewed,
        Guid? OwnerSubjectUuid,
        Guid? RedirectUuid,
        Guid? DecisionUuid
    );
}
