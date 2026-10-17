using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationWorkService
{
    private static Task LockOperation(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string kind,
        Guid uuid,
        CancellationToken cancellationToken
    ) =>
        connection.ExecuteAsync(
            new CommandDefinition(
                "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",
                new { key = $"normalization:{kind}:{uuid}" },
                transaction,
                cancellationToken: cancellationToken
            )
        );

    private static Task<RunRow?> ReadRun(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workUuid,
        CancellationToken cancellationToken
    ) =>
        connection.QuerySingleOrDefaultAsync<RunRow>(
            new CommandDefinition(
                """
                SELECT request_hash AS "RequestHash",ticket::text AS "Ticket",state AS "State"
                FROM normalization.work_runs WHERE uuid=@workUuid
                """,
                new { workUuid },
                transaction,
                cancellationToken: cancellationToken
            )
        );

    private static async Task<IReadOnlyList<NormalizationInputDependency>> LockInputs(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NormalizationResourceKey[] keys,
        CancellationToken cancellationToken
    )
    {
        var rows = (
            await connection.QueryAsync<InputRow>(
                new CommandDefinition(
                    """
                    SELECT r.uuid AS "ResourceUuid",r.current_revision_uuid AS "RevisionUuid",r.generation AS "Generation"
                    FROM normalization.input_resources r
                    JOIN jsonb_to_recordset(@keys::jsonb) AS k("Provider" text,"ResourceType" text,"ProviderKey" text)
                        ON r.provider=k."Provider" AND r.resource_type=k."ResourceType" AND r.provider_key=k."ProviderKey"
                    ORDER BY r.uuid FOR UPDATE OF r
                    """,
                    new { keys = JsonSerializer.Serialize(keys) },
                    transaction,
                    cancellationToken: cancellationToken
                )
            )
        ).ToArray();
        if (rows.Length != keys.Length || rows.Any(r => r.RevisionUuid == null))
        {
            throw new ArgumentException(
                "Every input resource must have retained current evidence.",
                nameof(keys)
            );
        }
        return rows.Select(r => r.ToInput()).ToArray();
    }

    private static async Task<IReadOnlyList<NormalizationInputDependency>> LockInputUuids(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid[] uuids,
        CancellationToken cancellationToken
    ) => (
            await connection.QueryAsync<InputRow>(
                new CommandDefinition(
                    """
                    SELECT uuid AS "ResourceUuid",current_revision_uuid AS "RevisionUuid",generation AS "Generation"
                    FROM normalization.input_resources WHERE uuid=ANY(@uuids) ORDER BY uuid FOR UPDATE
                    """,
                    new { uuids },
                    transaction,
                    cancellationToken: cancellationToken
                )
            )
        ).Select(r => r.ToInput()).ToArray();

    private static async Task<Guid> FindOrCreateSubject(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NormalizationSubjectKey subject,
        string policyVersion,
        string configurationHash,
        CancellationToken cancellationToken
    )
    {
        return await connection.QuerySingleAsync<Guid>(
            new CommandDefinition(
                """
                INSERT INTO normalization.work_subjects(uuid,domain,subject_kind,subject_uuid,policy_version,configuration_hash)
                VALUES(@newSubjectUuid,@Domain,@Kind,@Uuid,@policyVersion,@configurationHash) ON CONFLICT DO NOTHING;
                SELECT uuid FROM normalization.work_subjects WHERE domain=@Domain AND subject_kind=@Kind AND subject_uuid=@Uuid
                """,
                new
                {
                    newSubjectUuid = Guid.NewGuid(),
                    subject.Domain,
                    subject.Kind,
                    subject.Uuid,
                    policyVersion,
                    configurationHash,
                },
                transaction,
                cancellationToken: cancellationToken
            )
        );
    }

    private static async Task<SubjectRow[]> LockSubjects(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IEnumerable<Guid> subjectUuids,
        CancellationToken cancellationToken
    )
    {
        var uuids = subjectUuids.Distinct().ToArray();
        var subjects = (
            await connection.QueryAsync<SubjectRow>(
                new CommandDefinition(
                    $"""
                    SELECT {SubjectColumns} FROM normalization.work_subjects WHERE uuid=ANY(@uuids) ORDER BY uuid FOR UPDATE
                    """,
                    new { uuids },
                    transaction,
                    cancellationToken: cancellationToken
                )
            )
        ).ToArray();
        if (subjects.Length != uuids.Length)
        {
            throw new ArgumentException("Unknown work subject dependency.", nameof(subjectUuids));
        }
        return subjects;
    }

    private static async Task<bool> LockDependencyChanges(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NormalizationSubjectKey subject,
        Guid[] dependencies,
        CancellationToken cancellationToken
    )
    {
        var previous = (
            await connection.QueryAsync<Guid>(
                new CommandDefinition(
                    """
                    SELECT d.dependency_uuid FROM normalization.work_dependencies d
                    JOIN normalization.work_subjects s ON s.uuid=d.subject_uuid
                    WHERE s.domain=@Domain AND s.subject_kind=@Kind AND s.subject_uuid=@Uuid
                    """,
                    subject,
                    transaction,
                    cancellationToken: cancellationToken
                )
            )
        ).Order().ToArray();
        if (previous.SequenceEqual(dependencies))
        {
            return false;
        }

        await connection.ExecuteAsync(
            new CommandDefinition(
                "SELECT pg_advisory_xact_lock(hashtextextended('normalization:dependency-registry',0))",
                transaction: transaction,
                cancellationToken: cancellationToken
            )
        );
        return true;
    }

    private static async Task ValidateDependencyChange(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid subjectUuid,
        Guid[] dependencies,
        bool registryLocked,
        CancellationToken cancellationToken
    )
    {
        var previous = (
            await connection.QueryAsync<Guid>(
                new CommandDefinition(
                    "SELECT dependency_uuid FROM normalization.work_dependencies WHERE subject_uuid=@subjectUuid",
                    new { subjectUuid },
                    transaction,
                    cancellationToken: cancellationToken
                )
            )
        )
            .Order()
            .ToArray();
        if (previous.SequenceEqual(dependencies))
        {
            return;
        }
        if (!registryLocked)
        {
            // A concurrent reconfiguration changed the graph after the unlocked preview. Restart
            // instead of taking a graph lock after subject locks and introducing a lock inversion.
            throw new NormalizationConflictException(
                "Dependency bindings changed; reload the subject and retry configuration."
            );
        }
        var cycles = await connection.QuerySingleAsync<bool>(
            new CommandDefinition(
                """
                WITH RECURSIVE reachable(uuid) AS (
                    SELECT unnest(@dependencies::uuid[])
                    UNION
                    SELECT d.dependency_uuid FROM normalization.work_dependencies d JOIN reachable r ON d.subject_uuid=r.uuid
                ) SELECT EXISTS(SELECT 1 FROM reachable WHERE uuid=@subjectUuid)
                """,
                new { subjectUuid, dependencies },
                transaction,
                cancellationToken: cancellationToken
            )
        );
        if (cycles)
        {
            throw new ArgumentException(
                "Normalization subject dependencies must form an acyclic graph.",
                nameof(dependencies)
            );
        }
    }

    private static async Task ConfigureBindings(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SubjectRow owner,
        string policyVersion,
        string configurationHash,
        IReadOnlyList<NormalizationInputDependency> inputs,
        Guid[] dependencies,
        long? expectedConfigurationGeneration,
        bool dependencyRegistryLocked,
        CancellationToken cancellationToken
    )
    {
        var subjectUuid = owner.Uuid;
        var configurationChanged = owner.ConfigurationHash != configurationHash;
        if (configurationChanged && expectedConfigurationGeneration != owner.DesiredGeneration)
        {
            throw new NormalizationConflictException(
                "Changing policy or bindings requires the current desired generation."
            );
        }
        await ValidateDependencyChange(
            connection,
            transaction,
            subjectUuid,
            dependencies,
            dependencyRegistryLocked,
            cancellationToken
        );
        if (configurationChanged)
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE normalization.work_subjects SET configuration_hash=@configurationHash,
                        policy_version=@policyVersion,desired_generation=desired_generation+1,updated_at=clock_timestamp()
                    WHERE uuid=@subjectUuid;
                    DELETE FROM normalization.work_inputs WHERE subject_uuid=@subjectUuid;
                    DELETE FROM normalization.work_dependencies WHERE subject_uuid=@subjectUuid;
                    """,
                    new
                    {
                        configurationHash,
                        policyVersion,
                        subjectUuid,
                    },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
            owner.DesiredGeneration++;
        }
        foreach (var input in inputs)
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO normalization.work_inputs(subject_uuid,resource_uuid) VALUES(@subjectUuid,@ResourceUuid)
                    ON CONFLICT DO NOTHING
                    """,
                    new { subjectUuid, input.ResourceUuid },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
        }
        foreach (var dependency in dependencies)
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO normalization.work_dependencies(subject_uuid,dependency_uuid) VALUES(@subjectUuid,@dependency)
                    ON CONFLICT DO NOTHING
                    """,
                    new { subjectUuid, dependency },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
        }
    }

    private static bool IsCurrent(
        NormalizationWorkTicket ticket,
        IReadOnlyList<NormalizationInputDependency> inputs,
        SubjectRow[] subjects,
        SubjectRow owner
    ) =>
        owner.DesiredGeneration == ticket.DesiredGeneration
        && owner.EditorialRevision == ticket.EditorialRevision
        && owner.PolicyVersion == ticket.PolicyVersion
        && owner.PublishedWorkUuid == ticket.PreviousPublishedWorkUuid
        && inputs
            .OrderBy(i => i.ResourceUuid)
            .SequenceEqual(ticket.Inputs.OrderBy(i => i.ResourceUuid))
        && ticket.Dependencies.All(d =>
            subjects.Any(s =>
                s.Uuid == d.SubjectUuid
                && s.DesiredGeneration == d.DesiredGeneration
                && s.EditorialRevision == d.EditorialRevision
                && s.PublishedWorkUuid == d.PublishedWorkUuid
            )
        );

    private static async Task RecordPublication(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NormalizationWorkTicket ticket,
        CancellationToken cancellationToken
    )
    {
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE normalization.work_subjects SET published_generation=@DesiredGeneration,
                    published_work_uuid=@WorkUuid,updated_at=clock_timestamp() WHERE uuid=@SubjectUuid
                """,
                ticket,
                transaction,
                cancellationToken: cancellationToken
            )
        );
        foreach (var input in ticket.Inputs)
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE normalization.work_inputs SET published_generation=@Generation WHERE subject_uuid=@SubjectUuid AND resource_uuid=@ResourceUuid
                    """,
                    new
                    {
                        ticket.SubjectUuid,
                        input.Generation,
                        input.ResourceUuid,
                    },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
        }
        foreach (var dependency in ticket.Dependencies)
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE normalization.work_dependencies SET published_generation=@DesiredGeneration,
                        published_editorial_revision=@EditorialRevision,published_work_uuid=@PublishedWorkUuid
                    WHERE subject_uuid=@subjectUuid AND dependency_uuid=@dependencyUuid
                    """,
                    new
                    {
                        subjectUuid = ticket.SubjectUuid,
                        dependencyUuid = dependency.SubjectUuid,
                        dependency.DesiredGeneration,
                        dependency.EditorialRevision,
                        dependency.PublishedWorkUuid,
                    },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
        }
    }

    // Canonical object ordering makes a JSON review replay independent of property order.
    // Array order remains significant because domain commands can contain ordered moves/occurrences.
    private static string Hash(JsonElement value)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, value);
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (
                var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            )
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray())
            {
                WriteCanonical(writer, item);
            }
            writer.WriteEndArray();
        }
        else
        {
            value.WriteTo(writer);
        }
    }

    private sealed class RunRow
    {
        public string RequestHash { get; init; } = null!;
        public string Ticket { get; init; } = null!;
        public string State { get; init; } = null!;

        public NormalizationWorkTicket ToTicket() =>
            JsonSerializer.Deserialize<NormalizationWorkTicket>(Ticket)!;
    }

    private sealed class InputRow
    {
        public Guid ResourceUuid { get; init; }
        public Guid? RevisionUuid { get; init; }
        public long Generation { get; init; }

        public NormalizationInputDependency ToInput() =>
            new(ResourceUuid, RevisionUuid!.Value, Generation);
    }

    private sealed class SubjectRow
    {
        public Guid Uuid { get; init; }
        public string Domain { get; init; } = null!;
        public string Kind { get; init; } = null!;
        public Guid EntityUuid { get; init; }
        public long DesiredGeneration { get; set; }
        public long EditorialRevision { get; init; }
        public string PolicyVersion { get; init; } = null!;
        public string ConfigurationHash { get; init; } = null!;
        public long PublishedGeneration { get; init; }
        public Guid? PublishedWorkUuid { get; init; }

        public NormalizationSubjectState ToState() =>
            new(
                Uuid,
                new(Domain, Kind, EntityUuid),
                DesiredGeneration,
                EditorialRevision,
                PolicyVersion,
                PublishedGeneration,
                PublishedWorkUuid
            );
    }

    private sealed class ReviewRow
    {
        public string CommandHash { get; init; } = null!;
        public long EditorialRevision { get; init; }
    }
}
