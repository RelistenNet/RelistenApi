using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Owns publication freshness and the append-only review ledger. Domain services own typed
/// accepted rows and write them through transaction callbacks. Callbacks must use only the
/// supplied transaction, make no network requests, and never lock input resources themselves.
/// Catalog bindings are checked before publication. Callbacks may read catalog rows but must
/// neither lock nor change them: catalog clocks are locked before resources and work owners.
/// A fresh run does not authorize replacing reviewed data: typed domain writers retain reviewed
/// assignments/exclusions and propose conflicts according to their own domain contracts.
/// </summary>
public sealed partial class NormalizationWorkService(DbService db)
{
    private const string SubjectColumns = """
        uuid AS "Uuid",domain AS "Domain",subject_kind AS "Kind",subject_uuid AS "EntityUuid",
        desired_generation AS "DesiredGeneration",editorial_revision AS "EditorialRevision",
        policy_version AS "PolicyVersion",configuration_hash AS "ConfigurationHash",
        published_generation AS "PublishedGeneration",published_work_uuid AS "PublishedWorkUuid"
        """;

    /// <summary>
    /// Captures a durable read set. Resources must already have retained current inputs; dependency
    /// UUIDs identify existing work subjects. Reuse the work UUID only for an exact request replay.
    /// Changed policy or bindings require ExpectedConfigurationGeneration matching the current
    /// desired generation, preventing obsolete workers from reinstalling an old configuration.
    /// Equal configuration and exact work replays need no configuration CAS.
    /// expectedInputRevisions pins inputs consumed before admission, such as a catalog binding's
    /// provider snapshot. Each expected key must be in resourceKeys; changed heads reject the run
    /// before its configuration commits. Omitted or empty expectations capture current heads.
    /// Changing input bytes needs no fan-out write:
    /// pending queries and publication compare the durable resource generations directly.
    /// </summary>
    public Task<NormalizationWorkTicket> BeginWorkAsync(
        Guid workUuid,
        NormalizationSubjectKey subject,
        string policyVersion,
        IReadOnlyList<NormalizationResourceKey> resourceKeys,
        IReadOnlyList<Guid> dependencySubjectUuids,
        long? expectedConfigurationGeneration = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<NormalizationCatalogDependency>? catalogInputs = null,
        IReadOnlyList<NormalizationSourceIdentityDependency>? sourceIdentities = null,
        NormalizationMappingRegistryDependency? mappingRegistry = null,
        IReadOnlyList<NormalizationBindingInputReference>? expectedInputRevisions = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);
        var expectedCatalog = NormalizeCatalogInputs(catalogInputs);
        var expectedSources = NormalizeSourceInputs(sourceIdentities);
        var bindings = WorkBindingSet.Create(
            resourceKeys,
            dependencySubjectUuids,
            expectedCatalog.Select(input => input.ArtistUuid),
            expectedSources.Select(input => input.UpstreamIdentifier),
            mappingRegistry != null
        );
        var configurationHash = bindings.ConfigurationHash(policyVersion);
        var expectedInputs = NormalizeExpectedInputRevisions(
            expectedInputRevisions,
            bindings.Resources
        );
        // Keep both pre-catalog request shapes for durable work UUID replays.
        var requestPayload =
            expectedCatalog.Length == 0 && expectedSources.Length == 0
                ? JsonSerializer.SerializeToElement(
                    new
                    {
                        subject,
                        configurationHash,
                        expectedConfigurationGeneration,
                    }
                )
                : JsonSerializer.SerializeToElement(
                    new
                    {
                        subject,
                        configurationHash,
                        expectedConfigurationGeneration,
                        catalogInputs = expectedCatalog,
                        sourceIdentities = expectedSources,
                    }
                );
        var requestHash = HashWorkRequest(requestPayload, mappingRegistry, expectedInputs);
        var request = new WorkStartRequest(
            workUuid,
            subject,
            policyVersion,
            bindings,
            expectedConfigurationGeneration,
            requestHash,
            expectedCatalog,
            expectedSources,
            mappingRegistry,
            expectedInputs
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

    private static async Task<NormalizationWorkTicket?> ReadWorkReplay(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workUuid,
        string requestHash,
        CancellationToken ct
    )
    {
        await LockOperation(connection, transaction, "work", workUuid, ct);
        var replay = await ReadRun(connection, transaction, workUuid, ct);
        if (replay == null)
        {
            return null;
        }
        if (replay.RequestHash != requestHash)
        {
            throw new NormalizationConflictException(
                "Work UUID is already bound to a different request."
            );
        }
        return replay.ToTicket();
    }

    private static Task LockConfiguration(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NormalizationSubjectKey subject,
        CancellationToken ct
    ) =>
        connection.ExecuteAsync(
            new CommandDefinition(
                "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",
                new
                {
                    key = $"normalization:configuration:{subject.Domain}:{subject.Kind}:{subject.Uuid}",
                },
                transaction,
                cancellationToken: ct
            )
        );

    private static async Task<NormalizationWorkTicket> StartConfiguredWork(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorkStartRequest request,
        CancellationToken ct
    )
    {
        // Configuration edits serialize before graph, catalog, resource and subject locks.
        // Ordinary publication never needs this configuration lock.
        var bindings = request.Bindings;
        var graphLocked = await LockDependencyChanges(
            connection,
            transaction,
            request.Subject,
            bindings.Dependencies,
            ct
        );
        var catalog = await LockCatalogReadSet(
            connection,
            transaction,
            bindings.CatalogArtists,
            bindings.SourceIdentifiers,
            bindings.UsesMappingRegistry,
            ct
        );
        RequireCatalogReadSet(
            catalog,
            request.CatalogInputs,
            request.SourceIdentities,
            request.MappingRegistry
        );
        var inputs = await LockInputs(connection, transaction, bindings.Resources, ct);
        await RequireExpectedInputRevisions(
            connection,
            transaction,
            request.ExpectedInputRevisions,
            ct
        );
        var configurationHash = bindings.ConfigurationHash(request.PolicyVersion);
        var subjectUuid = await FindOrCreateSubject(
            connection,
            transaction,
            request.Subject,
            request.PolicyVersion,
            configurationHash,
            ct
        );
        if (bindings.Dependencies.Contains(subjectUuid))
        {
            throw new ArgumentException("A subject cannot depend on itself.");
        }
        var subjects = await LockSubjects(
            connection,
            transaction,
            bindings.Dependencies.Append(subjectUuid),
            ct
        );
        var owner = subjects.Single(subject => subject.Uuid == subjectUuid);
        var expectedGeneration = request.ExpectedConfigurationGeneration;
        if (request.Contribution != null)
        {
            if (owner.PolicyVersion == request.PolicyVersion)
            {
                // The held owner-configuration lock makes this a current union, rather than
                // letting one recording reinstall the owner's earlier partial configuration.
                expectedGeneration = owner.DesiredGeneration;
            }
            await ApplyContribution(connection, transaction, owner.Uuid, request.Contribution, ct);
        }
        if (request.Contribution == null)
        {
            await RequireContributionUnion(connection, transaction, owner.Uuid, bindings, ct);
        }
        await ConfigureBindings(
            connection,
            transaction,
            owner,
            request.PolicyVersion,
            configurationHash,
            inputs,
            bindings.Dependencies,
            expectedGeneration,
            graphLocked,
            ct
        );
        await ConfigureCatalogBindings(connection, transaction, owner.Uuid, catalog, ct);
        var ticket = new NormalizationWorkTicket(
            request.WorkUuid,
            subjectUuid,
            owner.DesiredGeneration,
            owner.EditorialRevision,
            request.PolicyVersion,
            owner.PublishedWorkUuid,
            inputs,
            subjects
                .Where(subject => subject.Uuid != subjectUuid)
                .Select(subject => new NormalizationSubjectDependency(
                    subject.Uuid,
                    subject.DesiredGeneration,
                    subject.EditorialRevision,
                    subject.PublishedWorkUuid
                ))
                .ToArray(),
            catalog.Artists.Count == 0 ? null : catalog.Artists,
            catalog.SourceIdentities.Count == 0 ? null : catalog.SourceIdentities,
            catalog.MappingRegistry
        );
        await connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO normalization.work_runs(uuid,subject_uuid,request_hash,ticket) VALUES(@workUuid,@subjectUuid,@requestHash,@ticket::jsonb)",
                new
                {
                    workUuid = request.WorkUuid,
                    subjectUuid,
                    requestHash = request.RequestHash,
                    ticket = JsonSerializer.Serialize(ticket),
                },
                transaction,
                cancellationToken: ct
            )
        );
        return ticket;
    }

    private sealed record WorkStartRequest(
        Guid WorkUuid,
        NormalizationSubjectKey Subject,
        string PolicyVersion,
        WorkBindingSet Bindings,
        long? ExpectedConfigurationGeneration,
        string RequestHash,
        NormalizationCatalogDependency[] CatalogInputs,
        NormalizationSourceIdentityDependency[] SourceIdentities,
        NormalizationMappingRegistryDependency? MappingRegistry,
        NormalizationBindingInputReference[] ExpectedInputRevisions,
        ContributionChange? Contribution = null
    );

    private sealed record WorkBindingSet(
        NormalizationResourceKey[] Resources,
        Guid[] Dependencies,
        Guid[] CatalogArtists,
        string[] SourceIdentifiers,
        // Omit false in contributed request JSON so pre-registry work hashes still replay.
        [property: JsonIgnore(
            Condition = JsonIgnoreCondition.WhenWritingDefault
        )] bool UsesMappingRegistry = false
    )
    {
        public static WorkBindingSet Create(
            IEnumerable<NormalizationResourceKey> resources,
            IEnumerable<Guid> dependencies,
            IEnumerable<Guid> catalogArtists,
            IEnumerable<string> sourceIdentifiers,
            bool usesMappingRegistry = false
        ) =>
            new(
                resources
                    .Distinct()
                    .OrderBy(key => key.Provider, StringComparer.Ordinal)
                    .ThenBy(key => key.ResourceType, StringComparer.Ordinal)
                    .ThenBy(key => key.ProviderKey, StringComparer.Ordinal)
                    .ToArray(),
                dependencies.Distinct().Order().ToArray(),
                catalogArtists.Distinct().Order().ToArray(),
                sourceIdentifiers
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                usesMappingRegistry
            );

        public string ConfigurationHash(string policyVersion) =>
            UsesMappingRegistry
                ? Hash(
                    JsonSerializer.SerializeToElement(
                        new
                        {
                            policyVersion,
                            resources = Resources,
                            dependencies = Dependencies,
                            catalogArtists = CatalogArtists,
                            sourceIdentifiers = SourceIdentifiers,
                            mappingRegistry = true,
                        }
                    )
                )
            : CatalogArtists.Length == 0 && SourceIdentifiers.Length == 0
                ? Hash(
                    JsonSerializer.SerializeToElement(
                        new
                        {
                            policyVersion,
                            resources = Resources,
                            dependencies = Dependencies,
                        }
                    )
                )
            : Hash(
                JsonSerializer.SerializeToElement(
                    new
                    {
                        policyVersion,
                        resources = Resources,
                        dependencies = Dependencies,
                        catalogArtists = CatalogArtists,
                        sourceIdentifiers = SourceIdentifiers,
                    }
                )
            );
    }

    private static NormalizationCatalogDependency[] NormalizeCatalogInputs(
        IReadOnlyList<NormalizationCatalogDependency>? inputs
    )
    {
        var result = (inputs ?? [])
            .Distinct()
            .OrderBy(input => input.ArtistUuid)
            .ThenBy(input => input.Generation)
            .ToArray();
        if (result.GroupBy(input => input.ArtistUuid).Any(group => group.Count() != 1))
        {
            throw new ArgumentException(
                "An artist snapshot cannot contain conflicting generations."
            );
        }
        return result;
    }

    private static NormalizationSourceIdentityDependency[] NormalizeSourceInputs(
        IReadOnlyList<NormalizationSourceIdentityDependency>? inputs
    )
    {
        var result = (inputs ?? [])
            .Distinct()
            .OrderBy(input => input.UpstreamIdentifier, StringComparer.Ordinal)
            .ThenBy(input => input.Generation)
            .ToArray();
        if (
            result
                .GroupBy(input => input.UpstreamIdentifier, StringComparer.Ordinal)
                .Any(group => group.Count() != 1)
            || result.Any(input => string.IsNullOrWhiteSpace(input.UpstreamIdentifier))
        )
        {
            throw new ArgumentException(
                "A source identity snapshot requires unique identifiers and consistent generations."
            );
        }
        return result;
    }

    /// <summary>
    /// Publishes typed domain changes exactly once per committed run. Stale/replayed runs never call
    /// publish. Callback failure or cancellation rolls back both domain writes and publication, so
    /// the same work UUID can retry. External side effects do not belong in this callback.
    /// </summary>
    public Task<NormalizationPublicationResult> TryPublishAsync(
        Guid workUuid,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> publish,
        CancellationToken cancellationToken = default
    ) =>
        InTransaction(
            async (connection, transaction) =>
            {
                await LockOperation(connection, transaction, "work", workUuid, cancellationToken);
                var run =
                    await ReadRun(connection, transaction, workUuid, cancellationToken)
                    ?? throw new ArgumentException("Unknown work UUID.", nameof(workUuid));
                if (run.State != "running")
                {
                    return new NormalizationPublicationResult(
                        workUuid,
                        ParseStatus(run.State),
                        true
                    );
                }
                var ticket = run.ToTicket();
                var catalog = await LockCatalogReadSet(
                    connection,
                    transaction,
                    (ticket.CatalogInputs ?? []).Select(input => input.ArtistUuid).ToArray(),
                    (ticket.SourceIdentities ?? [])
                        .Select(input => input.UpstreamIdentifier)
                        .ToArray(),
                    ticket.MappingRegistry != null,
                    cancellationToken
                );
                var inputs = await LockInputUuids(
                    connection,
                    transaction,
                    ticket.Inputs.Select(i => i.ResourceUuid).ToArray(),
                    cancellationToken
                );
                var subjects = await LockSubjects(
                    connection,
                    transaction,
                    ticket.Dependencies.Select(d => d.SubjectUuid).Append(ticket.SubjectUuid),
                    cancellationToken
                );
                var owner = subjects.Single(s => s.Uuid == ticket.SubjectUuid);
                var current =
                    CatalogIsCurrent(ticket, catalog) && IsCurrent(ticket, inputs, subjects, owner);
                if (current)
                {
                    await publish(connection, transaction, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    await RecordPublication(connection, transaction, ticket, cancellationToken);
                    await RecordCatalogPublication(
                        connection,
                        transaction,
                        ticket,
                        cancellationToken
                    );
                }
                var state = current ? "published" : "stale";
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE normalization.work_runs SET state=@state,completed_at=clock_timestamp() WHERE uuid=@workUuid
                        """,
                        new { state, workUuid },
                        transaction,
                        cancellationToken: cancellationToken
                    )
                );
                return new NormalizationPublicationResult(workUuid, ParseStatus(state), false);
            },
            cancellationToken
        );

    /// <summary>
    /// Applies a trusted domain correction and immutable audit receipt in one transaction. Operator
    /// must come from the authenticated command context. Payload is audit data, never a generic
    /// accepted graph; the callback must validate and write the domain's typed records.
    /// Declare all additional identity owners before calling: the store locks and increments each
    /// owner once. Callbacks must not acquire additional subject locks after domain writes begin.
    /// </summary>
    public Task<NormalizationReviewResult> ApplyReviewAsync(
        NormalizationReviewCommand command,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> apply,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Operator);
        var payload = command.Payload.Clone();
        var additionalSubjects = (command.AdditionalSubjectUuids ?? [])
            .Where(uuid => uuid != command.SubjectUuid)
            .Distinct()
            .Order()
            .ToArray();
        command = command with { Payload = payload, AdditionalSubjectUuids = additionalSubjects };
        var affectedSubjects = additionalSubjects.Append(command.SubjectUuid).ToArray();
        var hash = Hash(JsonSerializer.SerializeToElement(command));
        return InTransaction(
            async (connection, transaction) =>
            {
                await LockOperation(
                    connection,
                    transaction,
                    "review",
                    command.OperationUuid,
                    cancellationToken
                );
                var replay = await connection.QuerySingleOrDefaultAsync<ReviewRow>(
                    new CommandDefinition(
                        """
                        SELECT command_hash AS "CommandHash",editorial_revision AS "EditorialRevision"
                        FROM normalization.review_operations WHERE uuid=@OperationUuid
                        """,
                        command,
                        transaction,
                        cancellationToken: cancellationToken
                    )
                );
                if (replay != null)
                {
                    if (replay.CommandHash != hash)
                    {
                        throw new NormalizationConflictException(
                            "Review UUID is already bound to a different command."
                        );
                    }
                    return new NormalizationReviewResult(
                        command.OperationUuid,
                        replay.EditorialRevision,
                        true
                    );
                }
                var affectedOwners = await LockSubjects(
                    connection,
                    transaction,
                    affectedSubjects,
                    cancellationToken
                );
                var owner = affectedOwners.Single(subject => subject.Uuid == command.SubjectUuid);
                if (owner.EditorialRevision != command.ExpectedEditorialRevision)
                {
                    throw new NormalizationConflictException(
                        "The subject changed after this review was prepared."
                    );
                }
                await apply(connection, transaction, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var revision = owner.EditorialRevision + 1;
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO normalization.review_operations(uuid,subject_uuid,command_hash,additional_subject_uuids,action,payload,reason,operator_name,editorial_revision)
                        VALUES(@OperationUuid,@SubjectUuid,@hash,@additionalSubjects,@Action,@payload::jsonb,@Reason,@Operator,@revision);
                        UPDATE normalization.work_subjects SET editorial_revision=editorial_revision+1,desired_generation=desired_generation+1,
                            updated_at=clock_timestamp() WHERE uuid=ANY(@affectedSubjects)
                        """,
                        new
                        {
                            command.OperationUuid,
                            command.SubjectUuid,
                            hash,
                            command.Action,
                            payload = payload.GetRawText(),
                            command.Reason,
                            command.Operator,
                            additionalSubjects,
                            affectedSubjects,
                            revision,
                        },
                        transaction,
                        cancellationToken: cancellationToken
                    )
                );
                return new NormalizationReviewResult(command.OperationUuid, revision, false);
            },
            cancellationToken
        );
    }

    /// <summary>Durable keyset page of subjects needing refresh; no Redis or outbox delivery is required.</summary>
    public Task<IReadOnlyList<NormalizationSubjectState>> GetPendingSubjectsAsync(
        Guid? afterUuid = null,
        int limit = 100,
        CancellationToken cancellationToken = default
    )
    {
        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
        return db.WithWriteConnection<IReadOnlyList<NormalizationSubjectState>>(async connection =>
            (
                await connection.QueryAsync<SubjectRow>(
                    new CommandDefinition(
                        $"""
                        SELECT {SubjectColumns} FROM normalization.work_subjects s
                        WHERE (@afterUuid::uuid IS NULL OR s.uuid>@afterUuid) AND (
                            s.desired_generation<>s.published_generation
                            OR EXISTS(SELECT 1 FROM normalization.work_inputs i JOIN normalization.input_resources r ON r.uuid=i.resource_uuid
                                WHERE i.subject_uuid=s.uuid AND i.published_generation IS DISTINCT FROM r.generation)
                            OR EXISTS(SELECT 1 FROM normalization.work_dependencies d JOIN normalization.work_subjects ds ON ds.uuid=d.dependency_uuid
                                WHERE d.subject_uuid=s.uuid AND (d.published_generation IS DISTINCT FROM ds.desired_generation
                                    OR d.published_editorial_revision IS DISTINCT FROM ds.editorial_revision
                                    OR d.published_work_uuid IS DISTINCT FROM ds.published_work_uuid))
                            OR EXISTS(SELECT 1 FROM normalization.work_catalog_inputs i JOIN normalization.catalog_artist_revisions r ON r.artist_uuid=i.artist_uuid
                                WHERE i.subject_uuid=s.uuid AND i.published_generation IS DISTINCT FROM r.generation)
                            OR EXISTS(SELECT 1 FROM normalization.work_source_identity_inputs i JOIN normalization.catalog_source_identity_revisions r ON r.upstream_identifier=i.upstream_identifier
                                WHERE i.subject_uuid=s.uuid AND i.published_generation IS DISTINCT FROM r.generation)
                            OR EXISTS(SELECT 1 FROM normalization.work_mapping_registry_inputs i CROSS JOIN normalization.catalog_mapping_registry_revision r
                                WHERE i.subject_uuid=s.uuid AND i.published_generation IS DISTINCT FROM r.generation))
                        ORDER BY s.uuid LIMIT @limit
                        """,
                        new { afterUuid, limit },
                        cancellationToken: cancellationToken
                    )
                )
            ).Select(s => s.ToState()).ToArray()
        );
    }

    /// <summary>Finds durable owner revisions by domain identity on the primary; an absent owner returns null.</summary>
    public Task<NormalizationSubjectState?> GetSubjectAsync(
        NormalizationSubjectKey subject,
        CancellationToken cancellationToken = default
    ) =>
        db.WithWriteConnection(async connection =>
            (
                await connection.QuerySingleOrDefaultAsync<SubjectRow>(
                    new CommandDefinition(
                        $"SELECT {SubjectColumns} FROM normalization.work_subjects WHERE domain=@Domain AND subject_kind=@Kind AND subject_uuid=@Uuid",
                        subject,
                        cancellationToken: cancellationToken
                    )
                )
            )?.ToState()
        );

    /// <summary>Reads durable owner revisions from the primary database; absent subjects return null.</summary>
    public Task<NormalizationSubjectState?> GetSubjectAsync(
        Guid subjectUuid,
        CancellationToken cancellationToken = default
    ) =>
        db.WithWriteConnection(async connection =>
            (
                await connection.QuerySingleOrDefaultAsync<SubjectRow>(
                    new CommandDefinition(
                        $"SELECT {SubjectColumns} FROM normalization.work_subjects WHERE uuid=@subjectUuid",
                        new { subjectUuid },
                        cancellationToken: cancellationToken
                    )
                )
            )?.ToState()
        );

    private Task<T> InTransaction<T>(
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> action,
        CancellationToken cancellationToken
    ) =>
        db.WithWriteConnection(async connection =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var transaction = await (
                (NpgsqlConnection)connection
            ).BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var result = await action((NpgsqlConnection)connection, transaction);
            await transaction.CommitAsync(cancellationToken);
            return result;
        });

    private static NormalizationPublicationStatus ParseStatus(string state) =>
        state == "published"
            ? NormalizationPublicationStatus.Published
            : NormalizationPublicationStatus.Stale;
}
