using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

/// <summary>
/// Durable scoped review knowledge over admitted or frozen-source references. Work owns the
/// immutable operator ledger and CAS; this table is its current typed index, not another graph.
/// Evidence files must be retained by the host under its explicitly configured evidence root.
/// </summary>
public sealed partial class NormalizationExpertKnowledgeService(
    DbService db,
    NormalizationWorkService work,
    string evidenceRoot
)
{
    public const string RegistryVersion = "expert-knowledge-registry-v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Columns = """
        rule_uuid AS "RuleUuid",revision AS "Revision",state AS "State",definition::text AS "Definition",
        last_operation_uuid AS "LastOperationUuid",updated_at AS "UpdatedAt"
        """;
    private readonly string evidenceRoot = Path.GetFullPath(evidenceRoot);

    public async Task<ExpertKnowledgeSaveResult> SaveAsync(
        ExpertKnowledgeSaveCommand command,
        string trustedOperator,
        CancellationToken ct = default
    )
    {
        // Freeze all caller-owned evidence collections before registration, hashing or awaits.
        command = JsonSerializer.Deserialize<ExpertKnowledgeSaveCommand>(
            JsonSerializer.SerializeToUtf8Bytes(command, Json),
            Json
        )!;
        ValidateCommand(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedOperator);
        var prior = await GetRuleAsync(command.RuleUuid, ct);
        var definition =
            command.Definition
            ?? prior?.Definition
            ?? throw new ArgumentException("A new rule requires its complete definition.");
        var ownerKey = new NormalizationSubjectKey(
            definition.Domain,
            "expert_rule",
            command.RuleUuid
        );
        var ticket = await work.BeginWorkAsync(
            command.RuleUuid,
            ownerKey,
            RegistryVersion,
            [],
            [],
            cancellationToken: ct
        );
        var writeScopes = new[] { Universe(definition) }
            .Concat(prior == null ? [] : new[] { Universe(prior.Definition) })
            .Distinct()
            .ToArray();
        var receipt = await work.ApplyReviewAsync(
            new(
                command.OperationUuid,
                ticket.SubjectUuid,
                command.ExpectedRevision,
                command.Action,
                JsonSerializer.SerializeToElement(command, Json),
                command.Reason,
                trustedOperator
            ),
            async (c, t, token) =>
            {
                var current = await ReadRule(c, t, command.RuleUuid, true, token);
                if ((current?.Revision ?? 0) != command.ExpectedRevision)
                {
                    throw new NormalizationConflictException(
                        "Expert rule changed after review was prepared."
                    );
                }
                if ((current?.Revision ?? 0) != (prior?.Revision ?? 0))
                {
                    throw new NormalizationConflictException(
                        "Expert rule changed after its candidate write scopes were observed."
                    );
                }
                if (current != null && current.Definition.Domain != definition.Domain)
                {
                    throw new ArgumentException("A rule cannot change its domain.");
                }
                if (command.Action == "retract" && current == null)
                {
                    throw new ArgumentException("Only an existing rule can be withdrawn.");
                }
                var effectiveDefinition = command.Definition ?? current!.Definition;
                // Committed exact replay skips this callback, so later evidence-head or file
                // changes cannot turn the original successful operation into a failed retry.
                if (command.Action != "retract")
                {
                    await AuthenticateEvidence(effectiveDefinition, token);
                    await ValidateCanonicalTarget(c, t, effectiveDefinition, token);
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO normalization.expert_knowledge_rules(rule_uuid,subject_uuid,domain,revision,state,definition,last_operation_uuid)
                        VALUES(@RuleUuid,@subject,@domain,@revision,@state,@definition::jsonb,@OperationUuid)
                        ON CONFLICT(rule_uuid) DO UPDATE SET revision=excluded.revision,state=excluded.state,
                            definition=excluded.definition,last_operation_uuid=excluded.last_operation_uuid,updated_at=clock_timestamp()
                        """,
                        new
                        {
                            command.RuleUuid,
                            command.OperationUuid,
                            subject = ticket.SubjectUuid,
                            domain = effectiveDefinition.Domain,
                            revision = command.ExpectedRevision + 1,
                            state = State(command.Action),
                            definition = JsonSerializer.Serialize(effectiveDefinition, Json),
                        },
                        t,
                        cancellationToken: token
                    )
                );
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "SELECT normalization.bump_candidate_universes(@domains,@scopes)",
                        new
                        {
                            domains = writeScopes.Select(scope => scope.Domain).ToArray(),
                            scopes = writeScopes.Select(scope => scope.ScopeUuid).ToArray(),
                        },
                        t,
                        cancellationToken: token
                    )
                );
            },
            ct,
            candidateWriteScopes: writeScopes
        );
        return new(
            command.RuleUuid,
            receipt.EditorialRevision,
            State(command.Action),
            receipt.Replayed
        );
    }

    public Task<ExpertKnowledgeRule?> GetRuleAsync(Guid ruleUuid, CancellationToken ct = default) =>
        db.WithWriteConnection(c => ReadRule((NpgsqlConnection)c, null, ruleUuid, false, ct));

    public Task<IReadOnlyList<ExpertKnowledgeHistoryEntry>> GetHistoryAsync(
        Guid ruleUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<IReadOnlyList<ExpertKnowledgeHistoryEntry>>(async c =>
        {
            var rows = (
                await c.QueryAsync<HistoryRow>(
                    new CommandDefinition(
                        """
                        SELECT operation.uuid AS "OperationUuid",operation.editorial_revision AS "Revision",
                            operation.action AS "Action",operation.reason AS "Reason",operation.operator_name AS "Operator",
                            operation.created_at AS "CreatedAt",operation.payload::text AS "Command"
                        FROM normalization.review_operations operation JOIN normalization.work_subjects subject ON subject.uuid=operation.subject_uuid
                        WHERE subject.subject_kind='expert_rule' AND subject.subject_uuid=@ruleUuid
                        ORDER BY operation.editorial_revision LIMIT 1001
                        """,
                        new { ruleUuid },
                        cancellationToken: ct
                    )
                )
            ).ToArray();
            if (rows.Length > 1000)
            {
                throw new InvalidOperationException(
                    "Expert rule history exceeds the complete-history response bound."
                );
            }
            return rows.Select(row => new ExpertKnowledgeHistoryEntry(
                    row.OperationUuid,
                    row.Revision,
                    row.Action,
                    row.Reason,
                    row.Operator,
                    row.CreatedAt,
                    JsonSerializer.Deserialize<ExpertKnowledgeSaveCommand>(row.Command, Json)!
                ))
                .ToArray();
        });

    public Task<ExpertKnowledgeSnapshot> ReadSnapshotAsync(CancellationToken ct = default) =>
        db.WithWriteConnection(async connection =>
        {
            var c = (NpgsqlConnection)connection;
            await using var t = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await c.ExecuteAsync(
                new CommandDefinition(
                    "SET TRANSACTION READ ONLY",
                    transaction: t,
                    cancellationToken: ct
                )
            );
            var rows = (
                await c.QueryAsync<RuleRow>(
                    new CommandDefinition(
                        $"SELECT {Columns} FROM normalization.expert_knowledge_rules ORDER BY rule_uuid LIMIT @take",
                        new { take = ExpertKnowledgeResolver.MaximumRules + 1 },
                        t,
                        cancellationToken: ct
                    )
                )
            ).ToArray();
            var snapshot = ExpertKnowledgeResolver.CreateSnapshot(
                rows.Select(row => row.ToRule()).ToArray()
            );
            await t.CommitAsync(ct);
            return snapshot;
        });

    private static async Task<ExpertKnowledgeRule?> ReadRule(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid ruleUuid,
        bool locked,
        CancellationToken ct
    ) =>
        (
            await c.QuerySingleOrDefaultAsync<RuleRow>(
                new CommandDefinition(
                    $"SELECT {Columns} FROM normalization.expert_knowledge_rules WHERE rule_uuid=@ruleUuid"
                        + (locked ? " FOR UPDATE" : ""),
                    new { ruleUuid },
                    t,
                    cancellationToken: ct
                )
            )
        )?.ToRule();

    private async Task AuthenticateEvidence(
        ExpertKnowledgeDefinition definition,
        CancellationToken ct
    )
    {
        foreach (var pin in definition.Evidence)
        {
            var path = Path.GetFullPath(pin.Path);
            if (
                !Path.IsPathFullyQualified(pin.Path)
                || !path.StartsWith(
                    evidenceRoot + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal
                )
            )
            {
                throw new ArgumentException(
                    "Expert evidence must remain inside the configured retained evidence root."
                );
            }
            FileSystemInfo? current = new FileInfo(path);
            while (current != null && current.FullName != evidenceRoot)
            {
                if (current.LinkTarget != null)
                {
                    throw new ArgumentException("Expert evidence cannot traverse symbolic links.");
                }
                current = current switch
                {
                    FileInfo file => file.Directory,
                    DirectoryInfo directory => directory.Parent,
                    _ => null,
                };
            }
            var before = new FileInfo(path);
            if (!before.Exists || before.Length != pin.Bytes)
            {
                throw new NormalizationConflictException(
                    "Retained expert evidence is absent or changed."
                );
            }
            var length = before.Length;
            var modified = before.LastWriteTimeUtc;
            await using var stream = File.OpenRead(path);
            var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            var after = new FileInfo(path);
            if (
                !sha.Equals(pin.Sha256, StringComparison.OrdinalIgnoreCase)
                || after.Length != length
                || after.LastWriteTimeUtc != modified
            )
            {
                throw new NormalizationConflictException(
                    "Retained expert evidence differs from its frozen pin."
                );
            }
        }
    }

    private static async Task ValidateCanonicalTarget(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        ExpertKnowledgeDefinition definition,
        CancellationToken ct
    )
    {
        if (definition.Target.CanonicalUuid is not { } uuid)
        {
            return;
        }
        var table = definition.Domain switch
        {
            "venues" => "canonical_venues",
            "songs" => "repertoire_songs",
            "compositions" => "compositions",
            _ => "people",
        };
        var artist = definition.Domain == "songs" ? " AND artist_uuid=@ArtistUuid" : "";
        if (
            !await c.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    $"SELECT EXISTS(SELECT 1 FROM normalization.{table} WHERE uuid=@uuid{artist})",
                    new { uuid, definition.Selector.ArtistUuid },
                    t,
                    cancellationToken: ct
                )
            )
        )
        {
            throw new ArgumentException(
                "The optional canonical UUID does not exist in the target domain and artist scope."
            );
        }
    }

    private static void ValidateCommand(ExpertKnowledgeSaveCommand command)
    {
        if (
            command.RuleUuid == Guid.Empty
            || command.OperationUuid == Guid.Empty
            || command.ExpectedRevision < 0
            || command.Action is not ("accept" or "reject" or "retract")
        )
        {
            throw new ArgumentException(
                "Expert review requires stable UUIDs, expected revision and a supported action."
            );
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Reason);
        if (command.Action == "retract")
        {
            if (command.Definition != null)
            {
                throw new ArgumentException(
                    "Withdrawal retains the existing definition; omit a replacement."
                );
            }
        }
        else
        {
            ExpertKnowledgeResolver.ValidateDefinition(
                command.Definition
                    ?? throw new ArgumentException("A complete expert rule is required.")
            );
        }
    }

    private static string State(string action) =>
        action switch
        {
            "accept" => "accepted",
            "reject" => "rejected",
            _ => "retracted",
        };

    private static NormalizationCandidateUniverseKey Universe(
        ExpertKnowledgeDefinition definition
    ) =>
        definition.Domain == "songs"
            ? NormalizationCandidateUniverseKey.ForRepertoire(definition.Selector.ArtistUuid!.Value)
            : new(definition.Domain, Guid.Empty);

    private sealed class RuleRow
    {
        public Guid RuleUuid { get; init; }
        public long Revision { get; init; }
        public string State { get; init; } = "";
        public string Definition { get; init; } = "";
        public Guid LastOperationUuid { get; init; }
        public DateTime UpdatedAt { get; init; }

        public ExpertKnowledgeRule ToRule() =>
            new(
                RuleUuid,
                Revision,
                State,
                JsonSerializer.Deserialize<ExpertKnowledgeDefinition>(Definition, Json)!,
                LastOperationUuid,
                UpdatedAt
            );
    }

    private sealed class HistoryRow
    {
        public Guid OperationUuid { get; init; }
        public long Revision { get; init; }
        public string Action { get; init; } = "";
        public string Reason { get; init; } = "";
        public string Operator { get; init; } = "";
        public DateTime CreatedAt { get; init; }
        public string Command { get; init; } = "";
    }
}
