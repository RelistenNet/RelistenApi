using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    /// <summary>
    /// Reads the exact accepted claim, immutable source citations and complete bounded candidate
    /// facts in one primary snapshot. The current accepted owner/revision establishes claim
    /// authority; historical citations do not assert that their raw resource heads are current.
    /// </summary>
    public async Task<CompositionResolutionContext> ReadCompositionResolutionContextAsync(
        CatalogWorkClaimReference claim,
        IReadOnlyList<MatchingExternalIdentity>? externalWorks = null,
        CancellationToken ct = default
    )
    {
        if (claim.RepertoireSongUuid == Guid.Empty || claim.MusicRevisionUuid == Guid.Empty)
        {
            throw new ArgumentException(
                "A repertoire identity and exact accepted music revision are required."
            );
        }

        var requestedWorks = (externalWorks ?? []).Distinct().ToArray();
        if (
            requestedWorks.Length > 16
            || requestedWorks.Any(id =>
                id.Provider != "musicbrainz.org"
                || id.ResourceType != "work"
                || !Guid.TryParse(id.ProviderKey, out var mbid)
                || mbid == Guid.Empty
            )
        )
        {
            throw new ArgumentException("Supply at most 16 qualified MusicBrainz work identities.");
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

        var issues = new List<string>();
        var context = new CandidateFactsContext();
        var limits = ValidateFactsLimits(new());
        var source = await ReadCompositionClaim(c, t, claim, ct);
        context.AddOwner(source.OwnerSubjectUuid, claim.RepertoireSongUuid);
        context.AddArtist(source.ArtistUuid);
        var hit = new NormalizationCandidateHit(
            source.Uuid,
            source.Revision,
            source.DisplayTitle,
            source.OwnerSubjectUuid,
            0,
            ["exact_local_claim"]
        );
        var repertoire = (
            await ReadRepertoireFactsAsync(
                c,
                t,
                new(IdentityMatchingDomain.Repertoire, [hit], false),
                limits,
                context,
                ct
            )
        ).Single();
        var localTitle = await ReadCompositionClaimTitle(c, t, source, repertoire, issues, ct);
        var artist = await ReadCompositionArtist(c, t, source.ArtistUuid, issues, ct);
        var query = CompositionContextQuery(repertoire, requestedWorks, issues);
        var selection =
            query.Names.Count == 0 && query.ExternalIdentities.Count == 0
                ? new NormalizationCandidateSet(IdentityMatchingDomain.Compositions, [], false)
                : await FindInSnapshotAsync(c, t, query, ValidateQuery(query), ct);
        var selected = selection;
        if (selected.Items.Count > limits.MaximumCandidates)
        {
            context.AddIssue(CandidateFactsIssueKind.CandidateLimit);
            selected = selected with
            {
                Items = selected.Items.Take(limits.MaximumCandidates).ToArray(),
            };
        }
        var facts = await ReadCompositionFactsAsync(c, t, selected, limits, context, ct);
        var exclusions = await ReadCompositionExclusions(
            c,
            t,
            repertoire,
            facts,
            context,
            issues,
            ct
        );
        var universes = await ReadFactsUniversesAsync(
            c,
            t,
            new(IdentityMatchingDomain.Repertoire, [], [], source.ArtistUuid),
            ct
        );
        var readSet = await context.ReadReadSetAsync(c, t, universes, ct);
        var candidates = new NormalizationCandidateFactsSnapshot<CompositionCandidateFacts>(
            FactsVersion,
            FactsQueryHash(query, limits),
            query,
            limits,
            selection,
            facts,
            readSet,
            context.Issues
        );
        var completeReadSet = new CompositionResolutionReadSet(
            [],
            [],
            readSet.CatalogInputs,
            [],
            null,
            [],
            readSet.CandidateUniverses,
            readSet.OwnerObservations,
            []
        );
        var membership = await NormalizationWorkService.GetInputContributionsInSnapshotAsync(
            c,
            t,
            new("songs", "artist_repertoire", source.ArtistUuid),
            new(),
            ct
        );
        var ownerBindings = source.OwnerSubjectUuid is { } owner
            ? await ReadCompositionOwnerBindings(c, t, owner, issues, ct)
            : new CompositionOwnerBindings(completeReadSet, []);

        await t.CommitAsync(ct);
        return new(
            claim,
            source.ArtistUuid,
            artist,
            localTitle,
            repertoire,
            candidates,
            completeReadSet,
            issues.Distinct().ToArray(),
            exclusions,
            membership,
            ownerBindings.ReadSet,
            ownerBindings.InputReferences
        );
    }

    private static async Task<CompositionClaimRow> ReadCompositionClaim(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        CatalogWorkClaimReference claim,
        CancellationToken ct
    )
    {
        var row = await c.QuerySingleOrDefaultAsync<CompositionClaimRow>(
            new CommandDefinition(
                """
                SELECT x.uuid AS "Uuid",x.artist_uuid AS "ArtistUuid",x.display_title AS "DisplayTitle",
                    x.revision_uuid AS "MusicRevisionUuid",r.revision AS "Revision",owner.uuid AS "OwnerSubjectUuid"
                FROM normalization.repertoire_songs x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                LEFT JOIN normalization.work_subjects owner ON owner.domain='songs'
                    AND owner.subject_kind='artist_repertoire' AND owner.subject_uuid=x.artist_uuid
                WHERE x.uuid=@RepertoireSongUuid AND x.revision_uuid=@MusicRevisionUuid
                    AND NOT x.retracted AND x.redirect_uuid IS NULL
                """,
                claim,
                t,
                cancellationToken: ct
            )
        );
        return row
            ?? throw new NormalizationConflictException(
                "The repertoire claim is missing, redirected or no longer at the requested revision."
            );
    }

    private static NormalizationCandidateQuery CompositionContextQuery(
        RepertoireCandidateFacts repertoire,
        MatchingExternalIdentity[] externalWorks,
        List<string> issues
    )
    {
        var allNames = repertoire
            .Titles.SelectMany(title => title.Aliases.Prepend(title.DisplayTitle))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var names = allNames
            .Where(name =>
                !string.IsNullOrWhiteSpace(name)
                && name.Trim().EnumerateRunes().Count() <= 200
                && name.Any(char.IsLetterOrDigit)
            )
            .ToArray();
        if (names.Length != allNames.Length)
        {
            issues.Add("candidate_name_unrepresentable");
        }
        if (names.Length > 16)
        {
            issues.Add("candidate_name_limit");
        }
        var identifiers = repertoire
            .ExternalIdentities.Where(id =>
                id.Provider == "musicbrainz.org" && id.ResourceType == "work"
            )
            .Select(id => new MatchingExternalIdentity(
                id.Provider,
                id.ResourceType,
                id.ProviderKey
            ))
            .Concat(externalWorks)
            .Distinct()
            .ToArray();
        if (identifiers.Length > 16)
        {
            issues.Add("candidate_work_identity_limit");
        }
        if (names.Length == 0 && identifiers.Length == 0)
        {
            issues.Add("candidate_query_missing");
        }
        return new(
            IdentityMatchingDomain.Compositions,
            names.Take(16).ToArray(),
            identifiers.Take(16).ToArray()
        );
    }

    private static async Task<CatalogWorkArtistContext> ReadCompositionArtist(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid artistUuid,
        List<string> issues,
        CancellationToken ct
    )
    {
        var row = await c.QuerySingleOrDefaultAsync<CompositionArtistRow>(
            new CommandDefinition(
                "SELECT name AS \"Name\",musicbrainz_id AS \"MusicBrainzId\" FROM artists WHERE uuid=@artistUuid",
                new { artistUuid },
                t,
                cancellationToken: ct
            )
        );
        if (row == null)
        {
            issues.Add("catalog_artist_missing");
        }
        Guid? external =
            Guid.TryParse(row?.MusicBrainzId, out var mbid) && mbid != Guid.Empty ? mbid : null;
        return new(artistUuid, row?.Name, external);
    }

    private static async Task<ObservedValue<string>?> ReadCompositionClaimTitle(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        CompositionClaimRow source,
        RepertoireCandidateFacts repertoire,
        List<string> issues,
        CancellationToken ct
    )
    {
        var title = repertoire.Titles.SingleOrDefault(item =>
            item.Provenance.MusicRevisionUuid == source.MusicRevisionUuid
        );
        var citations = title?.Provenance.Evidence ?? [];
        if (citations.Count == 0)
        {
            issues.Add("local_title_evidence_missing");
            return null;
        }
        var heads = await ReadCompositionInputMetadata(
            c,
            t,
            citations.Select(item => item.InputRevisionUuid).Distinct().ToArray(),
            ct
        );
        if (heads.Length != citations.Select(item => item.InputRevisionUuid).Distinct().Count())
        {
            issues.Add("local_title_input_missing");
            return null;
        }
        try
        {
            var retained = await NormalizationEvidenceService.GetInputsAsync(
                c,
                t,
                heads
                    .Select(row => new NormalizationBindingInputReference(
                        row.RevisionUuid,
                        new(row.Provider, row.ResourceType, row.ProviderKey)
                    ))
                    .ToArray(),
                new(
                    MaximumInputs: 256,
                    MaximumInputBytes: 32 * 1024 * 1024,
                    MaximumTotalBytes: 128 * 1024 * 1024
                ),
                ct
            );
            foreach (var input in retained)
            {
                using var document = NormalizationRetainedDocument.Open(input, ct);
                foreach (
                    var citation in citations.Where(item => item.InputRevisionUuid == input.Uuid)
                )
                {
                    document.ValidateEvidence(citation);
                    var value = document.Resolve(citation.JsonPointer);
                    var text =
                        citation.Quote
                        ?? (value.ValueKind == JsonValueKind.String ? value.GetString() : null);
                    // Acceptance of a title does not let an unrelated valid citation become its
                    // literal search seed. A normalized title needs an independently retained span.
                    if (text == source.DisplayTitle)
                    {
                        return new(text, citation);
                    }
                }
            }
        }
        catch (ArgumentException)
        {
            issues.Add("local_title_evidence_invalid");
            return null;
        }
        catch (InvalidOperationException)
        {
            issues.Add("local_title_evidence_limit");
            return null;
        }
        issues.Add("local_title_literal_not_cited");
        return null;
    }

    private static async Task<CompositionInputHead[]> ReadCompositionInputMetadata(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid[] revisions,
        CancellationToken ct
    ) =>
        (
            await c.QueryAsync<CompositionInputHead>(
                new CommandDefinition(
                    """
                    SELECT r.uuid AS "RevisionUuid",p.provider AS "Provider",
                        p.resource_type AS "ResourceType",p.provider_key AS "ProviderKey"
                    FROM normalization.input_revisions r JOIN normalization.input_resources p ON p.uuid=r.resource_uuid
                    WHERE r.uuid=ANY(@revisions) ORDER BY p.uuid,r.uuid
                    """,
                    new { revisions },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();

    private static async Task<CompositionResolutionExclusion[]> ReadCompositionExclusions(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        RepertoireCandidateFacts repertoire,
        CompositionCandidateFacts[] candidates,
        CandidateFactsContext context,
        List<string> issues,
        CancellationToken ct
    )
    {
        var owners = candidates
            .SelectMany(fact => fact.IdentityClosure)
            .Append(repertoire.Header)
            .Select(header => header.OwnerSubjectUuid)
            .OfType<Guid>()
            .Distinct()
            .ToArray();
        foreach (var owner in owners)
        {
            context.AddOwner(owner);
        }
        var rows = (
            await c.QueryAsync<CompositionExclusionRow>(
                new CommandDefinition(
                    """
                    WITH bounded AS (
                        SELECT x.decision_uuid,x.claim_hash,r.payload,octet_length(r.payload::text)::bigint AS bytes
                        FROM normalization.music_exclusions x JOIN normalization.review_operations r ON r.uuid=x.decision_uuid
                        WHERE r.subject_uuid=ANY(@owners) OR r.additional_subject_uuids && @owners
                        ORDER BY x.decision_uuid,x.claim_hash LIMIT 5001
                    )
                    SELECT decision_uuid AS "DecisionUuid",claim_hash AS "ClaimHash",
                        CASE WHEN bytes<=1048576 AND sum(bytes) OVER()<=16777216
                            THEN payload::text ELSE NULL END AS "PublicationJson"
                    FROM bounded ORDER BY decision_uuid,claim_hash
                    """,
                    new { owners },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (rows.Length > 5000 || rows.Any(row => row.PublicationJson == null))
        {
            issues.Add("reviewed_exclusion_limit");
        }
        var result = new List<CompositionResolutionExclusion>();
        foreach (var row in rows.Take(5000).Where(row => row.PublicationJson != null))
        {
            try
            {
                var publication = JsonSerializer.Deserialize<MusicPublication>(
                    row.PublicationJson!
                );
                if (publication == null)
                {
                    issues.Add("reviewed_exclusion_invalid");
                    continue;
                }
                result.Add(new(row.DecisionUuid, row.ClaimHash, publication));
            }
            catch (JsonException)
            {
                issues.Add("reviewed_exclusion_invalid");
            }
        }
        return result.ToArray();
    }

    /// <summary>
    /// Current coordination state for preserving this owner's other contributors. These clocks
    /// do not reinterpret their source bytes or authorize the historical accepted title. The
    /// complete membership CAS and this read set must both survive contributed admission.
    /// </summary>
    private static async Task<CompositionOwnerBindings> ReadCompositionOwnerBindings(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid owner,
        List<string> issues,
        CancellationToken ct
    )
    {
        var bindings = await c.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                SELECT (SELECT count(*) FROM normalization.work_inputs WHERE subject_uuid=@owner)
                    +(SELECT count(*) FROM normalization.work_dependencies WHERE subject_uuid=@owner)
                    +(SELECT count(*) FROM normalization.work_catalog_inputs WHERE subject_uuid=@owner)
                    +(SELECT count(*) FROM normalization.work_source_identity_inputs WHERE subject_uuid=@owner)
                    +(SELECT count(*) FROM normalization.work_mapping_registry_inputs WHERE subject_uuid=@owner)
                    +(SELECT count(*) FROM normalization.work_venue_inputs WHERE subject_uuid=@owner)
                    +(SELECT count(*) FROM normalization.work_candidate_inputs WHERE subject_uuid=@owner)
                    +(SELECT count(*) FROM normalization.work_owner_observations WHERE subject_uuid=@owner)
                    +(SELECT count(*) FROM normalization.work_source_uuid_inputs WHERE subject_uuid=@owner)
                """,
                new { owner },
                t,
                cancellationToken: ct
            )
        );
        if (bindings > 10000)
        {
            throw new InvalidOperationException(
                "The complete repertoire owner binding snapshot exceeds its 10,000 binding limit."
            );
        }

        using var rows = await c.QueryMultipleAsync(
            new CommandDefinition(
                """
                SELECT p.uuid AS "ResourceUuid",p.current_revision_uuid AS "RevisionUuid",p.generation AS "Generation",
                    p.provider AS "Provider",p.resource_type AS "ResourceType",p.provider_key AS "ProviderKey"
                FROM normalization.work_inputs i JOIN normalization.input_resources p ON p.uuid=i.resource_uuid
                WHERE i.subject_uuid=@owner ORDER BY p.uuid;

                SELECT s.uuid AS "SubjectUuid",s.desired_generation AS "DesiredGeneration",
                    s.editorial_revision AS "EditorialRevision",s.published_work_uuid AS "PublishedWorkUuid"
                FROM normalization.work_dependencies i JOIN normalization.work_subjects s ON s.uuid=i.dependency_uuid
                WHERE i.subject_uuid=@owner ORDER BY s.uuid;

                SELECT r.artist_uuid AS "ArtistUuid",r.generation AS "Generation"
                FROM normalization.work_catalog_inputs i JOIN normalization.catalog_artist_revisions r ON r.artist_uuid=i.artist_uuid
                WHERE i.subject_uuid=@owner ORDER BY r.artist_uuid;

                SELECT r.upstream_identifier AS "UpstreamIdentifier",r.generation AS "Generation"
                FROM normalization.work_source_identity_inputs i JOIN normalization.catalog_source_identity_revisions r
                    ON r.upstream_identifier=i.upstream_identifier
                WHERE i.subject_uuid=@owner ORDER BY r.upstream_identifier COLLATE "C";

                SELECT r.generation AS "Generation"
                FROM normalization.work_mapping_registry_inputs i CROSS JOIN normalization.catalog_mapping_registry_revision r
                WHERE i.subject_uuid=@owner AND r.singleton;

                SELECT r.venue_uuid AS "VenueUuid",r.generation AS "Generation"
                FROM normalization.work_venue_inputs i JOIN normalization.catalog_venue_revisions r ON r.venue_uuid=i.venue_uuid
                WHERE i.subject_uuid=@owner ORDER BY r.venue_uuid;

                SELECT r.domain AS "Domain",r.scope_uuid AS "ScopeUuid",r.generation AS "Generation"
                FROM normalization.work_candidate_inputs i JOIN normalization.candidate_universe_revisions r
                    ON r.domain=i.domain AND r.scope_uuid=i.scope_uuid
                WHERE i.subject_uuid=@owner ORDER BY r.domain,r.scope_uuid;

                SELECT s.uuid AS "SubjectUuid",s.desired_generation AS "DesiredGeneration",
                    s.editorial_revision AS "EditorialRevision",s.published_work_uuid AS "PublishedWorkUuid"
                FROM normalization.work_owner_observations i JOIN normalization.work_subjects s ON s.uuid=i.observed_subject_uuid
                WHERE i.subject_uuid=@owner ORDER BY s.uuid;

                SELECT r.source_uuid AS "SourceUuid",r.generation AS "Generation"
                FROM normalization.work_source_uuid_inputs i JOIN normalization.catalog_source_uuid_revisions r ON r.source_uuid=i.source_uuid
                WHERE i.subject_uuid=@owner ORDER BY r.source_uuid;
                """,
                new { owner },
                t,
                cancellationToken: ct
            )
        );
        var resources = (await rows.ReadAsync<CompositionOwnerInput>()).ToArray();
        var dependencies = (await rows.ReadAsync<NormalizationSubjectDependency>()).ToArray();
        var catalog = (await rows.ReadAsync<NormalizationCatalogDependency>()).ToArray();
        var identities = (await rows.ReadAsync<NormalizationSourceIdentityDependency>()).ToArray();
        var registry =
            await rows.ReadSingleOrDefaultAsync<NormalizationMappingRegistryDependency>();
        var venues = (await rows.ReadAsync<NormalizationImportedVenueDependency>()).ToArray();
        var universes = (await rows.ReadAsync<CompositionOwnerUniverse>()).ToArray();
        var observations = (await rows.ReadAsync<NormalizationSubjectObservation>()).ToArray();
        var sources = (await rows.ReadAsync<NormalizationSourceUuidDependency>()).ToArray();
        if (resources.Any(resource => resource.RevisionUuid == null))
        {
            issues.Add("owner_input_head_missing");
        }
        var inputs = resources.Where(resource => resource.RevisionUuid.HasValue).ToArray();
        return new(
            new(
                inputs
                    .Select(input => new NormalizationInputDependency(
                        input.ResourceUuid,
                        input.RevisionUuid!.Value,
                        input.Generation
                    ))
                    .ToArray(),
                dependencies,
                catalog,
                identities,
                registry,
                venues,
                universes
                    .Select(universe => new NormalizationCandidateUniverseDependency(
                        new(universe.Domain, universe.ScopeUuid),
                        universe.Generation
                    ))
                    .ToArray(),
                observations,
                sources
            ),
            inputs
                .Select(input => new NormalizationBindingInputReference(
                    input.RevisionUuid!.Value,
                    new(input.Provider, input.ResourceType, input.ProviderKey)
                ))
                .ToArray()
        );
    }

    private sealed record CompositionClaimRow(
        Guid Uuid,
        Guid ArtistUuid,
        string DisplayTitle,
        Guid MusicRevisionUuid,
        long Revision,
        Guid? OwnerSubjectUuid
    );

    private sealed record CompositionArtistRow(string? Name, string? MusicBrainzId);

    private sealed record CompositionInputHead(
        Guid RevisionUuid,
        string Provider,
        string ResourceType,
        string ProviderKey
    );

    private sealed record CompositionExclusionRow(
        Guid DecisionUuid,
        string ClaimHash,
        string? PublicationJson
    );

    private sealed record CompositionOwnerInput(
        Guid ResourceUuid,
        Guid? RevisionUuid,
        long Generation,
        string Provider,
        string ResourceType,
        string ProviderKey
    );

    private sealed record CompositionOwnerUniverse(string Domain, Guid ScopeUuid, long Generation);

    private sealed record CompositionOwnerBindings(
        CompositionResolutionReadSet ReadSet,
        IReadOnlyList<NormalizationBindingInputReference> InputReferences
    );
}
