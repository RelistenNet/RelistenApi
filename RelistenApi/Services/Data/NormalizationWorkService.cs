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

/// <summary>
/// Owns publication freshness and the append-only review ledger. Domain services own typed
/// accepted rows and write them through transaction callbacks. Callbacks must use only the
/// supplied transaction, make no network requests, and never lock input resources themselves.
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
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);
        var resources = resourceKeys
            .Distinct()
            .OrderBy(k => k.Provider, StringComparer.Ordinal)
            .ThenBy(k => k.ResourceType, StringComparer.Ordinal)
            .ThenBy(k => k.ProviderKey, StringComparer.Ordinal)
            .ToArray();
        var dependencies = dependencySubjectUuids.Distinct().Order().ToArray();
        var configurationHash = Hash(
            JsonSerializer.SerializeToElement(
                new
                {
                    policyVersion,
                    resources,
                    dependencies,
                }
            )
        );
        var requestHash = Hash(
            JsonSerializer.SerializeToElement(
                new
                {
                    subject,
                    configurationHash,
                    expectedConfigurationGeneration,
                }
            )
        );
        return InTransaction(
            async (connection, transaction) =>
            {
                await LockOperation(connection, transaction, "work", workUuid, cancellationToken);
                var replay = await ReadRun(connection, transaction, workUuid, cancellationToken);
                if (replay != null)
                {
                    if (replay.RequestHash != requestHash)
                    {
                        throw new NormalizationConflictException(
                            "Work UUID is already bound to a different request."
                        );
                    }
                    return replay.ToTicket();
                }

                // Dependency graph edits serialize before resource/subject locks. Normal runs and
                // publication do not take this registry lock. Recheck bindings under owner locks.
                var dependencyRegistryLocked = await LockDependencyChanges(
                    connection,
                    transaction,
                    subject,
                    dependencies,
                    cancellationToken
                );

                // Input capture locks resources only. Always take resources before any subjects,
                // including dependency subjects, so capture and publication have one lock order.
                var inputs = await LockInputs(
                    connection,
                    transaction,
                    resources,
                    cancellationToken
                );
                var subjectUuid = await FindOrCreateSubject(
                    connection,
                    transaction,
                    subject,
                    policyVersion,
                    configurationHash,
                    cancellationToken
                );
                if (dependencies.Contains(subjectUuid))
                {
                    throw new ArgumentException(
                        "A subject cannot depend on itself.",
                        nameof(dependencySubjectUuids)
                    );
                }
                var subjects = await LockSubjects(
                    connection,
                    transaction,
                    dependencies.Append(subjectUuid),
                    cancellationToken
                );
                var owner = subjects.Single(s => s.Uuid == subjectUuid);
                await ConfigureBindings(
                    connection,
                    transaction,
                    owner,
                    policyVersion,
                    configurationHash,
                    inputs,
                    dependencies,
                    expectedConfigurationGeneration,
                    dependencyRegistryLocked,
                    cancellationToken
                );
                var ticket = new NormalizationWorkTicket(
                    workUuid,
                    subjectUuid,
                    owner.DesiredGeneration,
                    owner.EditorialRevision,
                    policyVersion,
                    owner.PublishedWorkUuid,
                    inputs,
                    subjects
                        .Where(s => s.Uuid != subjectUuid)
                        .Select(s => new NormalizationSubjectDependency(
                            s.Uuid,
                            s.DesiredGeneration,
                            s.EditorialRevision,
                            s.PublishedWorkUuid
                        ))
                        .ToArray()
                );
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO normalization.work_runs(uuid,subject_uuid,request_hash,ticket)
                        VALUES(@workUuid,@subjectUuid,@requestHash,@ticket::jsonb)
                        """,
                        new
                        {
                            workUuid,
                            subjectUuid,
                            requestHash,
                            ticket = JsonSerializer.Serialize(ticket),
                        },
                        transaction,
                        cancellationToken: cancellationToken
                    )
                );
                return ticket;
            },
            cancellationToken
        );
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
                var current = IsCurrent(ticket, inputs, subjects, owner);
                if (current)
                {
                    await publish(connection, transaction, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    await RecordPublication(connection, transaction, ticket, cancellationToken);
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
        command = command with { Payload = payload };
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
                var owner = (
                    await LockSubjects(
                        connection,
                        transaction,
                        [command.SubjectUuid],
                        cancellationToken
                    )
                ).Single();
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
                        INSERT INTO normalization.review_operations(uuid,subject_uuid,command_hash,action,payload,reason,operator_name,editorial_revision)
                        VALUES(@OperationUuid,@SubjectUuid,@hash,@Action,@payload::jsonb,@Reason,@Operator,@revision);
                        UPDATE normalization.work_subjects SET editorial_revision=@revision,desired_generation=desired_generation+1,
                            updated_at=clock_timestamp() WHERE uuid=@SubjectUuid
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
                                    OR d.published_work_uuid IS DISTINCT FROM ds.published_work_uuid)))
                        ORDER BY s.uuid LIMIT @limit
                        """,
                        new { afterUuid, limit },
                        cancellationToken: cancellationToken
                    )
                )
            ).Select(s => s.ToState()).ToArray()
        );
    }

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
