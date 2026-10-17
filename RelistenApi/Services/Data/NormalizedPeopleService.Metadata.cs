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

public sealed partial class NormalizedPeopleService
{
    /// <summary>Review identity-owned metadata without changing any recording credit. Replays use the operation UUID.</summary>
    public async Task<NormalizationReviewResult> ApplyMetadataDecisionAsync(
        PersonMetadataDecision command,
        string trustedOperator,
        CancellationToken cancellationToken = default
    )
    {
        var additionalOwners = await FindMetadataReviewOwners(
            db,
            "people",
            command.PersonUuid,
            command.OperationUuid,
            cancellationToken
        );
        var effectiveOwner =
            additionalOwners.Count == 0 ? command.SubjectUuid : additionalOwners[0];
        return await work.ApplyReviewAsync(
            new NormalizationReviewCommand(
                command.OperationUuid,
                command.SubjectUuid,
                command.ExpectedEditorialRevision,
                command.Action,
                JsonSerializer.SerializeToElement(command),
                command.Reason,
                trustedOperator,
                AdditionalSubjectUuids: additionalOwners
            ),
            async (c, t, ct) =>
            {
                await ValidateOwnerAsync(
                    c,
                    t,
                    command.SubjectUuid,
                    "people",
                    "person",
                    command.PersonUuid,
                    ct
                );
                await LockMetadataIdentity(
                    c,
                    t,
                    "people",
                    command.PersonUuid,
                    command.ExpectedPersonRevision,
                    effectiveOwner,
                    ct
                );
                await ValidateMetadataReviewInputs(
                    c,
                    t,
                    command.SubjectUuid,
                    command.Action,
                    new[]
                    {
                        command.Alias?.Evidence,
                        command.ExternalIdentity?.Evidence,
                        command.IdentityEvidence,
                    }
                        .OfType<NormalizedEvidenceReference>()
                        .ToArray(),
                    ct
                );
                await ApplyPersonMetadata(c, t, command, ct);
                await BumpMetadataIdentity(c, t, "people", command.PersonUuid, ct);
            },
            cancellationToken,
            candidateWriteScopes: IdentityCandidateWriteScopes
        );
    }

    private static async Task ApplyPersonMetadata(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        PersonMetadataDecision command,
        CancellationToken ct
    )
    {
        if (
            new object?[] { command.Alias, command.ExternalIdentity, command.Identity }.Count(x =>
                x != null
            ) != 1
        )
        {
            throw new ArgumentException("A metadata decision changes exactly one typed claim.");
        }
        if (command.Alias is { } alias)
        {
            await ReviewPersonAlias(c, t, command, alias, ct);
        }
        else if (command.ExternalIdentity is { } external)
        {
            await ReviewPersonExternalIdentity(c, t, command, external, ct);
        }
        else
        {
            await ReviewPersonDisplayIdentity(c, t, command, ct);
        }
    }

    private static async Task ReviewPersonAlias(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        PersonMetadataDecision command,
        PersonAlias alias,
        CancellationToken ct
    )
    {
        if (alias.PersonUuid != command.PersonUuid)
        {
            throw new ArgumentException("Alias belongs to another person.");
        }
        await ValidateEvidenceAsync(c, t, [alias.Evidence], ct);
        if (command.Action is not ("retract" or "reject"))
        {
            await ValidateNameEvidenceAsync(c, t, alias.Wording, alias.Evidence, ct);
        }
        var args = new DynamicParameters(
            new
            {
                alias.Uuid,
                alias.PersonUuid,
                alias.Wording,
                nameKey = NameKey(alias.Wording),
            }
        );
        await ReviewMetadataAssertion(
            c,
            t,
            "person_aliases",
            "person_uuid",
            command.PersonUuid,
            alias.Uuid,
            "person_uuid,wording,name_key",
            "@PersonUuid,@Wording,@nameKey",
            args,
            alias.Evidence,
            command.Action,
            command.ExpectedAssertionRevision,
            command.OperationUuid,
            ct
        );
    }

    private static async Task ReviewPersonExternalIdentity(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        PersonMetadataDecision command,
        PersonExternalIdentity external,
        CancellationToken ct
    )
    {
        if (external.PersonUuid != command.PersonUuid || external.Uuid == null)
        {
            throw new ArgumentException(
                "External identity requires its assertion UUID and original person owner."
            );
        }
        await ValidateEvidenceAsync(c, t, [external.Evidence], ct);
        var args = new DynamicParameters(external);
        await ReviewMetadataAssertion(
            c,
            t,
            "person_external_identities",
            "person_uuid",
            command.PersonUuid,
            external.Uuid.Value,
            "person_uuid,provider,resource_type,provider_key",
            "@PersonUuid,@Provider,@ResourceType,@ProviderKey",
            args,
            external.Evidence,
            command.Action,
            command.ExpectedAssertionRevision,
            command.OperationUuid,
            ct
        );
    }

    private static async Task ReviewPersonDisplayIdentity(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        PersonMetadataDecision command,
        CancellationToken ct
    )
    {
        var identity = command.Identity!;
        if (
            command.Action != "replace"
            || identity.Uuid != command.PersonUuid
            || command.IdentityEvidence == null
        )
        {
            throw new ArgumentException(
                "Identity display correction requires replace, the original UUID and name evidence."
            );
        }
        await ValidateEvidenceAsync(c, t, [command.IdentityEvidence], ct);
        await ValidateNameEvidenceAsync(c, t, identity.DisplayName, command.IdentityEvidence, ct);
        await c.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO normalization.people_metadata_history SELECT *,@OperationUuid,@InputRevisionUuid,@JsonPointer FROM normalization.people WHERE uuid=@Uuid;
                UPDATE normalization.people SET display_name=@DisplayName,name_key=@key,kind=@Kind,metadata_reviewed=true WHERE uuid=@Uuid
                """,
                new
                {
                    identity.Uuid,
                    identity.DisplayName,
                    identity.Kind,
                    key = NameKey(identity.DisplayName),
                    command.OperationUuid,
                    command.IdentityEvidence.InputRevisionUuid,
                    command.IdentityEvidence.JsonPointer,
                },
                t,
                cancellationToken: ct
            )
        );
    }

    internal static async Task<System.Collections.Generic.IReadOnlyList<Guid>> FindMetadataReviewOwners(
        DbService db,
        string domain,
        Guid uuid,
        Guid operationUuid,
        CancellationToken ct
    )
    {
        var prior = await ReadMetadataReviewOwners(db, operationUuid, ct);
        if (prior != null)
        {
            return prior.Owners;
        }
        var effectiveOwner = await FindMetadataEffectiveOwner(db, domain, uuid, ct);
        // A duplicate call can commit while redirect lookup waits for that call's owner lock.
        // Read its receipt again before deriving owners from the now-current topology.
        var committed = await ReadMetadataReviewOwners(db, operationUuid, ct);
        return committed?.Owners ?? [effectiveOwner];
    }

    private static Task<MetadataReviewOwners?> ReadMetadataReviewOwners(
        DbService db,
        Guid operationUuid,
        CancellationToken ct
    ) =>
        db.WithWriteConnection(c =>
            c.QuerySingleOrDefaultAsync<MetadataReviewOwners>(
                new CommandDefinition(
                    "SELECT additional_subject_uuids AS \"Owners\" FROM normalization.review_operations WHERE uuid=@operationUuid",
                    new { operationUuid },
                    cancellationToken: ct
                )
            )
        );

    private sealed class MetadataReviewOwners
    {
        public Guid[] Owners { get; init; } = [];
    }

    private static Task<Guid> FindMetadataEffectiveOwner(
        DbService db,
        string domain,
        Guid uuid,
        CancellationToken ct
    ) =>
        db.WithWriteConnection(async connection =>
        {
            var c = (NpgsqlConnection)connection;
            var table =
                domain == "people" ? "person_identity_resolution" : "venue_identity_resolution";
            var resolved = await c.QuerySingleOrDefaultAsync<Guid?>(
                new CommandDefinition(
                    $"SELECT resolved_uuid FROM normalization.{table} WHERE original_uuid=@uuid",
                    new { uuid },
                    cancellationToken: ct
                )
            );
            return await FindIdentityOwner(
                c,
                null,
                domain,
                domain == "people" ? "person" : "venue",
                resolved ?? uuid,
                ct
            );
        });

    internal static async Task LockMetadataIdentity(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        string domain,
        Guid uuid,
        long expectedRevision,
        Guid effectiveOwner,
        CancellationToken ct
    )
    {
        await c.ExecuteAsync(
            new CommandDefinition(
                domain == "venues"
                    ? "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))"
                    : "SELECT pg_advisory_xact_lock_shared(hashtextextended(@key,0))",
                new { key = $"normalization:{domain}:identity" },
                t,
                cancellationToken: ct
            )
        );
        var resolution =
            domain == "people" ? "person_identity_resolution" : "venue_identity_resolution";
        var table = domain == "people" ? "people" : "canonical_venues";
        var resolved = await c.QuerySingleAsync<Guid>(
            new CommandDefinition(
                $"SELECT resolved_uuid FROM normalization.{resolution} WHERE original_uuid=@uuid",
                new { uuid },
                t,
                cancellationToken: ct
            )
        );
        var actualOwner = await FindIdentityOwner(
            c,
            t,
            domain,
            domain == "people" ? "person" : "venue",
            resolved,
            ct
        );
        if (actualOwner != effectiveOwner)
        {
            throw new NormalizationConflictException(
                "Identity redirect changed while metadata review was prepared."
            );
        }
        var revision = await c.QuerySingleAsync<long>(
            new CommandDefinition(
                $"SELECT revision FROM normalization.{table} WHERE uuid=@uuid FOR UPDATE",
                new { uuid },
                t,
                cancellationToken: ct
            )
        );
        if (revision != expectedRevision)
        {
            throw new NormalizationConflictException(
                "Identity changed after metadata review was prepared."
            );
        }
    }

    internal static Task BumpMetadataIdentity(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        string domain,
        Guid uuid,
        CancellationToken ct
    )
    {
        var table = domain == "people" ? "people" : "canonical_venues";
        var resolution =
            domain == "people" ? "person_identity_resolution" : "venue_identity_resolution";
        return c.ExecuteAsync(
            new CommandDefinition(
                $"UPDATE normalization.{table} SET revision=revision+1,updated_at=clock_timestamp() WHERE uuid=@uuid OR uuid=(SELECT resolved_uuid FROM normalization.{resolution} WHERE original_uuid=@uuid)",
                new { uuid },
                t,
                cancellationToken: ct
            )
        );
    }

    // Callers select a fixed typed table and columns. This helper only shares revision, evidence and history mechanics.
    internal static async Task ReviewMetadataAssertion(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        string table,
        string ownerColumn,
        Guid owner,
        Guid uuid,
        string columns,
        string values,
        DynamicParameters args,
        NormalizedEvidenceReference evidence,
        string action,
        long expectedRevision,
        Guid operationUuid,
        CancellationToken ct
    )
    {
        var state = action switch
        {
            "accept" or "replace" => "accepted",
            "reject" => "rejected",
            "retract" => "superseded",
            _ => throw new ArgumentException("Unsupported metadata action."),
        };
        var prior = await c.QuerySingleOrDefaultAsync<MetadataAssertionRow>(
            new CommandDefinition(
                $"SELECT {ownerColumn} AS \"Owner\",revision AS \"Revision\" FROM normalization.{table} WHERE uuid=@uuid FOR UPDATE",
                new { uuid },
                t,
                cancellationToken: ct
            )
        );
        if ((prior?.Revision ?? 0) != expectedRevision || (prior != null && prior.Owner != owner))
        {
            throw new NormalizationConflictException(
                "Metadata assertion changed or belongs to another identity."
            );
        }
        if (prior == null && action != "accept")
        {
            throw new ArgumentException(
                "Accept can add a metadata assertion; rejection, replacement and retraction require an existing assertion UUID."
            );
        }
        args.Add("Uuid", uuid);
        args.Add("state", state);
        args.Add("operationUuid", operationUuid);
        args.Add("InputRevisionUuid", evidence.InputRevisionUuid);
        args.Add("JsonPointer", evidence.JsonPointer);
        args.Add("StartCodePoint", evidence.StartCodePoint);
        args.Add("EndCodePoint", evidence.EndCodePoint);
        args.Add("Quote", evidence.Quote);
        if (prior != null)
        {
            // Several raw assertions may support one natural claim. Retraction/replacement applies
            // to that claim, so another accepted UUID must not keep the discarded wording public.
            var sameClaim = table switch
            {
                "person_aliases" => "a.name_key=b.name_key",
                "venue_names" =>
                    "a.name_key=b.name_key AND a.language IS NOT DISTINCT FROM b.language AND a.valid_from IS NOT DISTINCT FROM b.valid_from AND a.valid_to IS NOT DISTINCT FROM b.valid_to",
                "venue_locations" => """
                    a.original_locality=b.original_locality AND a.country IS NOT DISTINCT FROM b.country
                    AND a.region IS NOT DISTINCT FROM b.region AND a.city IS NOT DISTINCT FROM b.city
                    AND a.address IS NOT DISTINCT FROM b.address AND a.latitude IS NOT DISTINCT FROM b.latitude
                    AND a.longitude IS NOT DISTINCT FROM b.longitude AND a.coordinate_precision IS NOT DISTINCT FROM b.coordinate_precision
                    AND a.valid_from IS NOT DISTINCT FROM b.valid_from AND a.valid_to IS NOT DISTINCT FROM b.valid_to
                    """,
                _ =>
                    "a.provider=b.provider AND a.resource_type=b.resource_type AND a.provider_key=b.provider_key",
            };
            await c.ExecuteAsync(
                new CommandDefinition(
                    $"""
                    INSERT INTO normalization.{table}_history SELECT a.*,@operationUuid FROM normalization.{table} a JOIN normalization.{table} b ON b.uuid=@uuid
                        WHERE a.{ownerColumn}=b.{ownerColumn} AND a.uuid<>b.uuid AND a.state='accepted' AND {sameClaim};
                    UPDATE normalization.{table} a SET state='superseded',basis='reviewed',revision=a.revision+1 FROM normalization.{table} b
                        WHERE b.uuid=@uuid AND a.{ownerColumn}=b.{ownerColumn} AND a.uuid<>b.uuid AND a.state='accepted' AND {sameClaim}
                    """,
                    args,
                    t,
                    cancellationToken: ct
                )
            );
            await c.ExecuteAsync(
                new CommandDefinition(
                    $"INSERT INTO normalization.{table}_history SELECT *,@operationUuid FROM normalization.{table} WHERE uuid=@uuid",
                    args,
                    t,
                    cancellationToken: ct
                )
            );
        }
        if (prior != null && action is "reject" or "retract")
        {
            await c.ExecuteAsync(
                new CommandDefinition(
                    $"UPDATE normalization.{table} SET state=@state,basis='reviewed',revision=revision+1 WHERE uuid=@uuid",
                    args,
                    t,
                    cancellationToken: ct
                )
            );
            return;
        }
        var columnList = columns.Split(',');
        var valueList = values.Split(',');
        var assignments = string.Join(
            ',',
            columnList.Zip(valueList, (column, value) => $"{column}={value}")
        );
        await c.ExecuteAsync(
            new CommandDefinition(
                $"""
                INSERT INTO normalization.{table}(uuid,{columns},input_revision_uuid,json_pointer,start_code_point,end_code_point,quote,state,basis)
                VALUES(@uuid,{values},@InputRevisionUuid,@JsonPointer,@StartCodePoint,@EndCodePoint,@Quote,@state,'reviewed')
                ON CONFLICT(uuid) DO UPDATE SET {assignments},input_revision_uuid=@InputRevisionUuid,json_pointer=@JsonPointer,
                    start_code_point=@StartCodePoint,end_code_point=@EndCodePoint,quote=@Quote,state=@state,basis='reviewed',revision={table}.revision+1
                """,
                args,
                t,
                cancellationToken: ct
            )
        );
    }

    internal static async Task ValidateMetadataReviewInputs(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid subjectUuid,
        string action,
        IReadOnlyList<NormalizedEvidenceReference> evidence,
        CancellationToken ct
    )
    {
        if (action is not ("accept" or "replace"))
        {
            return;
        }
        // The work store already holds the owner lock, which also serializes binding changes.
        // A review may cite an older revision; its resource must remain part of this owner's inputs.
        foreach (
            var revisionUuid in evidence.Select(reference => reference.InputRevisionUuid).Distinct()
        )
        {
            var bound = await c.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    """
                    SELECT EXISTS(SELECT 1 FROM normalization.input_revisions revision JOIN normalization.work_inputs binding ON binding.resource_uuid=revision.resource_uuid
                        WHERE revision.uuid=@revisionUuid AND binding.subject_uuid=@subjectUuid)
                    """,
                    new { revisionUuid, subjectUuid },
                    t,
                    cancellationToken: ct
                )
            );
            if (!bound)
            {
                throw new ArgumentException(
                    "Configure the original owner's input-resource bindings before accepting or replacing metadata from that resource."
                );
            }
        }
    }

    internal static void ValidateAutomaticMetadataBasis(string basis)
    {
        if (basis is not ("provider_assertion" or "validated_match"))
        {
            throw new ArgumentException(
                "Automatic metadata may use provider_assertion or validated_match; reviewed authority requires an operator decision."
            );
        }
    }

    internal static async Task<string?> AutomaticMetadataState(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        string table,
        string ownerColumn,
        Guid owner,
        string predicate,
        object values,
        NormalizedEvidenceReference evidence,
        CancellationToken ct
    )
    {
        var args = new DynamicParameters(values);
        args.Add("owner", owner);
        args.Add("InputRevisionUuid", evidence.InputRevisionUuid);
        args.Add("JsonPointer", evidence.JsonPointer);
        args.Add("StartCodePoint", evidence.StartCodePoint);
        args.Add("EndCodePoint", evidence.EndCodePoint);
        args.Add("Quote", evidence.Quote);
        var reviewed = await c.QueryAsync<bool>(
            new CommandDefinition(
                $"""
                SELECT input_revision_uuid=@InputRevisionUuid AND json_pointer=@JsonPointer AND start_code_point IS NOT DISTINCT FROM @StartCodePoint::integer
                    AND end_code_point IS NOT DISTINCT FROM @EndCodePoint::integer AND quote IS NOT DISTINCT FROM @Quote::text
                FROM normalization.{table} WHERE {ownerColumn}=@owner AND basis='reviewed' AND ({predicate})
                UNION ALL SELECT input_revision_uuid=@InputRevisionUuid AND json_pointer=@JsonPointer AND start_code_point IS NOT DISTINCT FROM @StartCodePoint::integer
                    AND end_code_point IS NOT DISTINCT FROM @EndCodePoint::integer AND quote IS NOT DISTINCT FROM @Quote::text
                FROM normalization.{table}_history WHERE {ownerColumn}=@owner AND ({predicate})
                """,
                args,
                t,
                cancellationToken: ct
            )
        );
        var matches = reviewed.ToArray();
        if (matches.Length == 0)
        {
            return "accepted";
        }
        if (matches.Any(same => same))
        {
            return null;
        }
        var pending = await c.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                $"""
                SELECT EXISTS(SELECT 1 FROM normalization.{table} WHERE {ownerColumn}=@owner AND state='pending' AND ({predicate})
                    AND input_revision_uuid=@InputRevisionUuid AND json_pointer=@JsonPointer AND start_code_point IS NOT DISTINCT FROM @StartCodePoint::integer
                    AND end_code_point IS NOT DISTINCT FROM @EndCodePoint::integer AND quote IS NOT DISTINCT FROM @Quote::text)
                """,
                args,
                t,
                cancellationToken: ct
            )
        );
        return pending ? null : "pending";
    }

    private sealed class MetadataAssertionRow
    {
        public Guid Owner { get; init; }
        public long Revision { get; init; }
    }
}
