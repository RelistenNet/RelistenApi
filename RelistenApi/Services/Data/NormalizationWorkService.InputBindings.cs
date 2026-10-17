using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

// Contributor ownership is part of the existing work configuration transaction. It must
// never commit separately from the complete read set that invalidates earlier workers.
public sealed partial class NormalizationWorkService
{
    /// <summary>
    /// Replaces one contributor's complete bindings and starts work over every contributor's
    /// union. Exact work UUID replay returns its original ticket. Another work UUID requires
    /// the contributor's current revision, even when the proposed bindings are unchanged.
    /// Returned input and dependency sets can exceed the incoming contribution: reload all
    /// facts required by this owner from that ticket before extracting or matching.
    /// The catalogInputs and sourceIdentities parameters replace this contributor's complete owned key sets;
    /// omitting either removes those bindings from this contributor. Other contributors retain theirs.
    /// Catalog generations supplied by the caller remain preconditions, never freshened values.
    /// An unchanged policy uses the serialized owner's current configuration revision; changing
    /// policy requires the caller's ExpectedConfigurationGeneration, as ordinary work does.
    /// </summary>
    public Task<NormalizationWorkTicket> BeginContributedWorkAsync(
        Guid workUuid,
        NormalizationSubjectKey subject,
        string policyVersion,
        NormalizationInputContribution contribution,
        long? expectedConfigurationGeneration = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<NormalizationCatalogDependency>? catalogInputs = null,
        IReadOnlyList<NormalizationSourceIdentityDependency>? sourceIdentities = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);
        ValidateContributor(contribution.Contributor);
        ArgumentOutOfRangeException.ThrowIfNegative(contribution.ExpectedRevision);
        var expectedCatalog = NormalizeCatalogInputs(catalogInputs);
        var expectedSources = NormalizeSourceInputs(sourceIdentities);
        var replacement = new ContributionReplacement(
            contribution.Contributor,
            contribution.ExpectedRevision,
            WorkBindingSet.Create(
                contribution.Resources,
                contribution.DependencySubjectUuids,
                expectedCatalog.Select(input => input.ArtistUuid),
                expectedSources.Select(input => input.UpstreamIdentifier)
            )
        );
        var requestHash = Hash(
            JsonSerializer.SerializeToElement(
                new
                {
                    mode = "contributed_inputs_v1",
                    subject,
                    policyVersion,
                    contribution = replacement,
                    expectedConfigurationGeneration,
                    catalogInputs = expectedCatalog,
                    sourceIdentities = expectedSources,
                }
            )
        );
        return InTransaction(
            async (connection, transaction) =>
            {
                var replay = await ReadWorkReplay(
                    connection,
                    transaction,
                    workUuid,
                    requestHash,
                    cancellationToken
                );
                if (replay != null)
                {
                    return replay;
                }
                await LockConfiguration(connection, transaction, subject, cancellationToken);
                var (union, change) = await PrepareContribution(
                    connection,
                    transaction,
                    subject,
                    replacement,
                    cancellationToken
                );
                var request = new WorkStartRequest(
                    workUuid,
                    subject,
                    policyVersion,
                    union,
                    expectedConfigurationGeneration,
                    requestHash,
                    expectedCatalog,
                    expectedSources,
                    change
                );
                return await StartConfiguredWork(
                    connection,
                    transaction,
                    request,
                    cancellationToken
                );
            },
            cancellationToken
        );
    }

    private static async Task<(
        WorkBindingSet Bindings,
        ContributionChange Change
    )> PrepareContribution(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NormalizationSubjectKey subject,
        ContributionReplacement replacement,
        CancellationToken ct
    )
    {
        var ownerUuid = await FindContributionOwner(connection, transaction, subject, ct);
        var states =
            ownerUuid == null
                ? []
                : await ReadContributionStates(connection, transaction, ownerUuid.Value, null, ct);
        ContributionReplacement? baseline = null;
        if (ownerUuid != null && states.Length == 0)
        {
            var previous = await ReadUnmanagedBindings(
                connection,
                transaction,
                ownerUuid.Value,
                ct
            );
            if (
                previous.Resources.Length
                    + previous.Dependencies.Length
                    + previous.CatalogArtists.Length
                    + previous.SourceIdentifiers.Length
                > 0
            )
            {
                // Adoption retains existing evidence ownership. Reconciliation may later
                // replace this explicit baseline using the same contributor CAS contract.
                baseline = new(
                    new("relisten", "preexisting_owner_bindings", ownerUuid.Value.ToString("D")),
                    0,
                    previous
                );
                states = [ToContributionState(ownerUuid.Value, baseline, 1)];
            }
        }
        var current = states.SingleOrDefault(state => state.Contributor == replacement.Contributor);
        if ((current?.Revision ?? 0) != replacement.ExpectedRevision)
        {
            throw new NormalizationConflictException(
                "Input contribution revision changed; reload its complete bindings."
            );
        }
        var retained = states
            .Where(state => state.Contributor != replacement.Contributor)
            .ToArray();
        var union = WorkBindingSet.Create(
            retained.SelectMany(state => state.Resources).Concat(replacement.Bindings.Resources),
            retained
                .SelectMany(state => state.DependencySubjectUuids)
                .Concat(replacement.Bindings.Dependencies),
            retained
                .SelectMany(state => state.CatalogArtistUuids)
                .Concat(replacement.Bindings.CatalogArtists),
            retained
                .SelectMany(state => state.SourceIdentifiers)
                .Concat(replacement.Bindings.SourceIdentifiers)
        );
        return (union, new(replacement, baseline));
    }

    /// <summary>Reads one contributor, including an empty tombstone, from a primary repeatable-read snapshot.</summary>
    public Task<NormalizationInputContributionState?> GetInputContributionAsync(
        NormalizationSubjectKey subject,
        NormalizationResourceKey contributor,
        CancellationToken cancellationToken = default
    )
    {
        ValidateContributor(contributor);
        return db.WithWriteConnection(async connection =>
        {
            await using var transaction = await (
                (NpgsqlConnection)connection
            ).BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            var ownerUuid = await FindContributionOwner(
                (NpgsqlConnection)connection,
                transaction,
                subject,
                cancellationToken
            );
            var state =
                ownerUuid == null
                    ? null
                    : (
                        await ReadContributionStates(
                            (NpgsqlConnection)connection,
                            transaction,
                            ownerUuid.Value,
                            contributor,
                            cancellationToken
                        )
                    ).SingleOrDefault();
            await transaction.CommitAsync(cancellationToken);
            return state;
        });
    }

    private static void ValidateContributor(NormalizationResourceKey key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.ResourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.ProviderKey);
    }

    private static Task<Guid?> FindContributionOwner(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NormalizationSubjectKey subject,
        CancellationToken ct
    ) =>
        connection.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                "SELECT uuid FROM normalization.work_subjects WHERE domain=@Domain AND subject_kind=@Kind AND subject_uuid=@Uuid",
                subject,
                transaction,
                cancellationToken: ct
            )
        );

    private static async Task ApplyContribution(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid subjectUuid,
        ContributionChange change,
        CancellationToken ct
    )
    {
        if (change.Baseline != null)
        {
            await ReplaceContribution(connection, transaction, subjectUuid, change.Baseline, ct);
        }
        await ReplaceContribution(connection, transaction, subjectUuid, change.Replacement, ct);
    }

    private static async Task ReplaceContribution(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid subjectUuid,
        ContributionReplacement replacement,
        CancellationToken ct
    )
    {
        var parameters = new
        {
            subjectUuid,
            provider = replacement.Contributor.Provider,
            type = replacement.Contributor.ResourceType,
            key = replacement.Contributor.ProviderKey,
            expected = replacement.ExpectedRevision,
        };
        var sql =
            replacement.ExpectedRevision == 0
                ? """
                    INSERT INTO normalization.work_input_contributions(uuid,subject_uuid,contributor_provider,contributor_type,contributor_key,revision)
                    VALUES(gen_random_uuid(),@subjectUuid,@provider,@type,@key,1) ON CONFLICT DO NOTHING RETURNING uuid
                    """
                : """
                    UPDATE normalization.work_input_contributions SET revision=revision+1,updated_at=clock_timestamp()
                    WHERE subject_uuid=@subjectUuid AND contributor_provider=@provider AND contributor_type=@type AND contributor_key=@key AND revision=@expected
                    RETURNING uuid
                    """;
        var contributionUuid = await connection.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(sql, parameters, transaction, cancellationToken: ct)
        );
        if (contributionUuid == null)
        {
            throw new NormalizationConflictException(
                "Input contribution revision changed; reload its complete bindings."
            );
        }
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                DELETE FROM normalization.work_contribution_inputs WHERE contribution_uuid=@contributionUuid;
                DELETE FROM normalization.work_contribution_dependencies WHERE contribution_uuid=@contributionUuid;
                DELETE FROM normalization.work_contribution_catalog_inputs WHERE contribution_uuid=@contributionUuid;
                DELETE FROM normalization.work_contribution_source_identities WHERE contribution_uuid=@contributionUuid;
                INSERT INTO normalization.work_contribution_inputs(contribution_uuid,resource_uuid)
                    SELECT @contributionUuid,r.uuid FROM normalization.input_resources r
                    JOIN jsonb_to_recordset(@resources::jsonb) AS k("Provider" text,"ResourceType" text,"ProviderKey" text)
                        ON r.provider=k."Provider" AND r.resource_type=k."ResourceType" AND r.provider_key=k."ProviderKey";
                INSERT INTO normalization.work_contribution_dependencies(contribution_uuid,dependency_uuid)
                    SELECT @contributionUuid,unnest(@dependencies::uuid[]);
                INSERT INTO normalization.work_contribution_catalog_inputs(contribution_uuid,artist_uuid)
                    SELECT @contributionUuid,unnest(@artists::uuid[]);
                INSERT INTO normalization.work_contribution_source_identities(contribution_uuid,upstream_identifier)
                    SELECT @contributionUuid,unnest(@identifiers::text[])
                """,
                new
                {
                    contributionUuid,
                    resources = JsonSerializer.Serialize(replacement.Bindings.Resources),
                    dependencies = replacement.Bindings.Dependencies,
                    artists = replacement.Bindings.CatalogArtists,
                    identifiers = replacement.Bindings.SourceIdentifiers,
                },
                transaction,
                cancellationToken: ct
            )
        );
    }

    private static async Task RequireContributionUnion(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid subjectUuid,
        WorkBindingSet requested,
        CancellationToken ct
    )
    {
        var states = await ReadContributionStates(connection, transaction, subjectUuid, null, ct);
        if (states.Length == 0)
        {
            return;
        }
        var union = WorkBindingSet.Create(
            states.SelectMany(state => state.Resources),
            states.SelectMany(state => state.DependencySubjectUuids),
            states.SelectMany(state => state.CatalogArtistUuids),
            states.SelectMany(state => state.SourceIdentifiers)
        );
        if (
            !union.Resources.SequenceEqual(requested.Resources)
            || !union.Dependencies.SequenceEqual(requested.Dependencies)
            || !union.CatalogArtists.SequenceEqual(requested.CatalogArtists)
            || !union.SourceIdentifiers.SequenceEqual(requested.SourceIdentifiers)
        )
        {
            throw new NormalizationConflictException(
                "Contributor-managed owners require their complete binding union; replace one contribution instead."
            );
        }
    }

    private static async Task<NormalizationInputContributionState[]> ReadContributionStates(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid subjectUuid,
        NormalizationResourceKey? contributor,
        CancellationToken ct
    )
    {
        const string filter =
            "h.subject_uuid=@subjectUuid AND (@provider::text IS NULL OR (h.contributor_provider=@provider AND h.contributor_type=@type AND h.contributor_key=@key))";
        using var rows = await connection.QueryMultipleAsync(
            new CommandDefinition(
                $"""
                SELECT h.uuid AS Uuid,h.contributor_provider AS Provider,h.contributor_type AS Type,h.contributor_key AS Key,h.revision AS Revision
                    FROM normalization.work_input_contributions h WHERE {filter};
                SELECT i.contribution_uuid AS Uuid,r.provider AS Provider,r.resource_type AS Type,r.provider_key AS Key
                    FROM normalization.work_contribution_inputs i JOIN normalization.work_input_contributions h ON h.uuid=i.contribution_uuid
                    JOIN normalization.input_resources r ON r.uuid=i.resource_uuid WHERE {filter};
                SELECT i.contribution_uuid AS Uuid,i.dependency_uuid AS Value FROM normalization.work_contribution_dependencies i
                    JOIN normalization.work_input_contributions h ON h.uuid=i.contribution_uuid WHERE {filter};
                SELECT i.contribution_uuid AS Uuid,i.artist_uuid AS Value FROM normalization.work_contribution_catalog_inputs i
                    JOIN normalization.work_input_contributions h ON h.uuid=i.contribution_uuid WHERE {filter};
                SELECT i.contribution_uuid AS Uuid,i.upstream_identifier AS Value FROM normalization.work_contribution_source_identities i
                    JOIN normalization.work_input_contributions h ON h.uuid=i.contribution_uuid WHERE {filter}
                """,
                new
                {
                    subjectUuid,
                    provider = contributor?.Provider,
                    type = contributor?.ResourceType,
                    key = contributor?.ProviderKey,
                },
                transaction,
                cancellationToken: ct
            )
        );
        var headers = (await rows.ReadAsync<ContributionHeader>()).ToArray();
        var resources = (await rows.ReadAsync<ContributionResource>()).ToLookup(row => row.Uuid);
        var dependencies = (await rows.ReadAsync<ContributionGuid>()).ToLookup(row => row.Uuid);
        var artists = (await rows.ReadAsync<ContributionGuid>()).ToLookup(row => row.Uuid);
        var identifiers = (await rows.ReadAsync<ContributionText>()).ToLookup(row => row.Uuid);
        return headers
            .Select(header =>
            {
                var bindings = WorkBindingSet.Create(
                    resources[header.Uuid]
                        .Select(row => new NormalizationResourceKey(
                            row.Provider,
                            row.Type,
                            row.Key
                        )),
                    dependencies[header.Uuid].Select(row => row.Value),
                    artists[header.Uuid].Select(row => row.Value),
                    identifiers[header.Uuid].Select(row => row.Value)
                );
                return ToContributionState(
                    subjectUuid,
                    new(new(header.Provider, header.Type, header.Key), 0, bindings),
                    header.Revision
                );
            })
            .ToArray();
    }

    private static async Task<WorkBindingSet> ReadUnmanagedBindings(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid subjectUuid,
        CancellationToken ct
    )
    {
        using var rows = await connection.QueryMultipleAsync(
            new CommandDefinition(
                """
                SELECT r.provider AS Provider,r.resource_type AS ResourceType,r.provider_key AS ProviderKey
                    FROM normalization.work_inputs i JOIN normalization.input_resources r ON r.uuid=i.resource_uuid WHERE i.subject_uuid=@subjectUuid;
                SELECT dependency_uuid FROM normalization.work_dependencies WHERE subject_uuid=@subjectUuid;
                SELECT artist_uuid FROM normalization.work_catalog_inputs WHERE subject_uuid=@subjectUuid;
                SELECT upstream_identifier FROM normalization.work_source_identity_inputs WHERE subject_uuid=@subjectUuid
                """,
                new { subjectUuid },
                transaction,
                cancellationToken: ct
            )
        );
        var resources = (await rows.ReadAsync<NormalizationResourceKey>()).ToArray();
        var dependencies = (await rows.ReadAsync<Guid>()).ToArray();
        var artists = (await rows.ReadAsync<Guid>()).ToArray();
        var identifiers = (await rows.ReadAsync<string>()).ToArray();
        return WorkBindingSet.Create(resources, dependencies, artists, identifiers);
    }

    private static NormalizationInputContributionState ToContributionState(
        Guid subjectUuid,
        ContributionReplacement replacement,
        long revision
    ) =>
        new(
            subjectUuid,
            replacement.Contributor,
            revision,
            replacement.Bindings.Resources,
            replacement.Bindings.Dependencies,
            replacement.Bindings.CatalogArtists,
            replacement.Bindings.SourceIdentifiers
        );

    private sealed record ContributionReplacement(
        NormalizationResourceKey Contributor,
        long ExpectedRevision,
        WorkBindingSet Bindings
    );

    private sealed record ContributionChange(
        ContributionReplacement Replacement,
        ContributionReplacement? Baseline
    );

    private sealed record ContributionHeader(
        Guid Uuid,
        string Provider,
        string Type,
        string Key,
        long Revision
    );

    private sealed record ContributionResource(Guid Uuid, string Provider, string Type, string Key);

    private sealed record ContributionGuid(Guid Uuid, Guid Value);

    private sealed record ContributionText(Guid Uuid, string Value);
}
