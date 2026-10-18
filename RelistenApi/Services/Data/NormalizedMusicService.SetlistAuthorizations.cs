using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedMusicService
{
    /// <summary>
    /// Approves, blocks or revokes exact historical source interpretation. Only authorization
    /// and the edition owner's editorial receipt change; accepted metadata remains untouched.
    /// A later image must independently pass its original proposal and catalog freshness checks.
    /// </summary>
    public Task<NormalizationReviewResult> ReviewSetlistCorrespondenceAsync(
        SetlistCorrespondenceDecision command,
        string trustedOperator,
        CancellationToken cancellationToken = default
    )
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(command);
        if (bytes.Length > MaximumStructureBytes)
        {
            throw new ArgumentException("The setlist authorization command exceeds 16 MiB.");
        }
        command = JsonSerializer.Deserialize<SetlistCorrespondenceDecision>(bytes)!;
        ValidateAuthorizationCommand(command);
        return work.ApplyReviewAsync(
            new(
                command.OperationUuid,
                command.SubjectUuid,
                command.ExpectedEditorialRevision,
                "replace",
                JsonSerializer.SerializeToElement(command),
                command.Reason,
                trustedOperator
            ),
            (c, t, ct) => ApplySetlistAuthorizations(c, t, command, ct),
            cancellationToken
        );
    }

    /// <summary>Pages immutable approval history, retaining original historical tickets and active block/revocation heads.</summary>
    public Task<IReadOnlyList<SetlistAuthorizationRecord>> GetSetlistAuthorizationsAsync(
        Guid editionUuid,
        int limit = 100,
        Guid? afterUuid = null,
        CancellationToken cancellationToken = default
    )
    {
        ValidateLimit(limit);
        return db.WithWriteConnection<IReadOnlyList<SetlistAuthorizationRecord>>(async connection =>
        {
            var c = (NpgsqlConnection)connection;
            await using var t = await c.BeginTransactionAsync(
                System.Data.IsolationLevel.RepeatableRead,
                cancellationToken
            );
            await c.ExecuteAsync(
                new CommandDefinition(
                    "SET TRANSACTION READ ONLY",
                    transaction: t,
                    cancellationToken: cancellationToken
                )
            );
            var parameters = new
            {
                editionUuid,
                afterUuid,
                limit,
            };
            var bytes = await c.QuerySingleAsync<long>(
                new CommandDefinition(
                    """
                    SELECT COALESCE(sum(bytes),0)::bigint FROM (
                        SELECT octet_length(payload::text)::bigint+octet_length(source_tickets::text) AS bytes
                        FROM normalization.setlist_authorization_events
                        WHERE edition_uuid=@editionUuid AND (@afterUuid::uuid IS NULL OR uuid>@afterUuid)
                        ORDER BY uuid LIMIT @limit
                    ) page
                    """,
                    parameters,
                    t,
                    cancellationToken: cancellationToken
                )
            );
            if (bytes > 2 * MaximumStructureBytes)
            {
                throw new ArgumentException(
                    "The selected authorization history page exceeds 32 MiB; request a smaller page."
                );
            }
            var rows = await c.QueryAsync<SetlistAuthorizationRow>(
                new CommandDefinition(
                    """
                    SELECT a.uuid AS AuthorizationUuid,a.edition_uuid AS EditionUuid,a.operation_uuid AS OperationUuid,
                        a.authorization_key AS AuthorizationKey,a.kind AS Kind,a.action AS Action,a.payload::text AS Payload,
                        a.source_tickets::text AS SourceTickets,a.created_at AS CreatedAt,
                        EXISTS(SELECT 1 FROM normalization.setlist_authorization_heads h WHERE h.authorization_uuid=a.uuid) AS Active,
                        a.processor_decision_uuid AS ProcessorDecisionUuid
                    FROM normalization.setlist_authorization_events a
                    WHERE a.edition_uuid=@editionUuid AND (@afterUuid::uuid IS NULL OR a.uuid>@afterUuid)
                    ORDER BY a.uuid LIMIT @limit
                    """,
                    parameters,
                    t,
                    cancellationToken: cancellationToken
                )
            );
            var result = rows.Select(AuthorizationRecord).ToArray();
            await t.CommitAsync(cancellationToken);
            return result;
        });
    }

    private static void ValidateAuthorizationCommand(SetlistCorrespondenceDecision command)
    {
        var ids = command
            .Correspondence.Select(item => item.AuthorizationUuid)
            .Concat(command.Removals.Select(item => item.AuthorizationUuid))
            .ToArray();
        if (
            command.EditionUuid == Guid.Empty
            || command.ExpectedStructureRevision < 0
            || command.ExpectedEditionRevision <= 0
            || ids.Length is 0 or > 100
            || ids.Any(id => id == Guid.Empty)
            || ids.Distinct().Count() != ids.Length
        )
        {
            throw new ArgumentException(
                "Review requires an edition, its current structure revision and one to 100 distinct authorization UUIDs."
            );
        }
        foreach (var item in command.Correspondence)
        {
            ValidateAuthorizationAction(item.Action);
            ClaimKey(item.Source, item.Claim);
            if (
                (item.Action == SetlistAuthorizationAction.Authorize && item.Targets.Count == 0)
                || item.Targets.Count > 100
                || item.Targets.Select(target => target.Uuid).Distinct().Count()
                    != item.Targets.Count
            )
            {
                throw new ArgumentException(
                    "Correspondence authorization requires a bounded, distinct target list."
                );
            }
            if (item.Claim.SetIndex.HasValue && item.Targets.Count > 1)
            {
                throw new ArgumentException("A source set authorizes one accepted set identity.");
            }
        }
        foreach (var item in command.Removals)
        {
            ValidateAuthorizationAction(item.Action);
            RemovalKind(item.Kind);
            if (item.Target.ExpectedRevision <= 0 || item.Sources.Count is 0 or > 100)
            {
                throw new ArgumentException(
                    "Removal review needs the existing target revision and its complete historical source accounts."
                );
            }
        }
    }

    private static void ValidateAuthorizationAction(SetlistAuthorizationAction action)
    {
        if (
            action
            is not (
                SetlistAuthorizationAction.Authorize
                or SetlistAuthorizationAction.Block
                or SetlistAuthorizationAction.Revoke
            )
        )
        {
            throw new ArgumentException("Unknown setlist authorization action.");
        }
    }

    private static async Task ApplySetlistAuthorizations(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistCorrespondenceDecision command,
        CancellationToken ct
    )
    {
        var edition = await ReadReviewedSetlistEdition(c, t, command, ct);
        var batches = await ReadCompatibleAuthorizationBatches(c, t, command, edition, ct);

        // The edition owner serializes these keys and target revisions. No raw resource locks
        // are taken: these receipts intentionally describe historical, immutable evidence.
        var keys = new HashSet<string>(StringComparer.Ordinal);
        await ApplyCorrespondenceApprovals(c, t, command, edition, batches, keys, ct);
        await ApplyRemovalApprovals(c, t, command, edition, batches, keys, ct);
    }

    private static async Task<SetlistEditionChange> ReadReviewedSetlistEdition(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistCorrespondenceDecision command,
        CancellationToken ct
    )
    {
        await NormalizedPeopleService.ValidateOwnerAsync(
            c,
            t,
            command.SubjectUuid,
            "songs",
            "setlist",
            command.EditionUuid,
            ct
        );
        var edition =
            await c.QuerySingleOrDefaultAsync<string>(
                new CommandDefinition(
                    """
                    SELECT r.payload::text FROM normalization.setlist_editions e
                    JOIN normalization.music_revisions r ON r.uuid=e.revision_uuid WHERE e.uuid=@EditionUuid
                    """,
                    command,
                    t,
                    cancellationToken: ct
                )
            ) ?? throw new ArgumentException("The accepted edition is missing.");
        await Require(
            c,
            t,
            """
            SELECT COALESCE((SELECT r.revision FROM normalization.setlist_structures s
                JOIN normalization.music_revisions r ON r.uuid=s.revision_uuid
                WHERE s.edition_uuid=@EditionUuid),0)=@ExpectedStructureRevision
                AND EXISTS(SELECT 1 FROM normalization.setlist_editions e JOIN normalization.music_revisions r ON r.uuid=e.revision_uuid
                    WHERE e.uuid=@EditionUuid AND r.revision=@ExpectedEditionRevision)
            """,
            command,
            "The accepted structure changed after review was prepared.",
            ct
        );
        return JsonSerializer.Deserialize<SetlistEditionChange>(edition)!;
    }

    private static async Task<
        Dictionary<Guid, NormalizationSetlistProposalBatch>
    > ReadCompatibleAuthorizationBatches(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistCorrespondenceDecision command,
        SetlistEditionChange metadata,
        CancellationToken ct
    )
    {
        var sources = command
            .Correspondence.Select(item => item.Source)
            .Concat(command.Removals.SelectMany(item => item.Sources))
            .ToArray();
        if (sources.Length > 100)
        {
            throw new ArgumentException(
                "An authorization command consumes at most 100 source account references."
            );
        }
        var interpretedSources = command
            .Correspondence.Where(item =>
                item.Action == SetlistAuthorizationAction.Authorize
                || item.ExpectedPriorAuthorizationUuid == null
            )
            .Select(item => item.Source)
            .Concat(
                command
                    .Removals.Where(item =>
                        item.Action == SetlistAuthorizationAction.Authorize
                        || item.ExpectedPriorAuthorizationUuid == null
                    )
                    .SelectMany(item => item.Sources)
            )
            .ToArray();
        // Preflight the unique compatible batch set once for the entire command. Repeated
        // claims must not multiply payload reads or evade the aggregate 32 MiB bound.
        return interpretedSources.Length == 0
            ? new Dictionary<Guid, NormalizationSetlistProposalBatch>()
            : await ReadStructureProposals(
                c,
                t,
                new SetlistStructureChange { Edition = metadata, Sources = interpretedSources },
                ct
            );
    }

    private static async Task ApplyCorrespondenceApprovals(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistCorrespondenceDecision command,
        SetlistEditionChange metadata,
        Dictionary<Guid, NormalizationSetlistProposalBatch> batches,
        HashSet<string> keys,
        CancellationToken ct
    )
    {
        foreach (var item in command.Correspondence)
        {
            var key = ClaimKey(item.Source, item.Claim);
            RequireUniqueAuthorizationKey(keys, key);
            var tickets = await ReadAuthorizationSourceTickets(
                c,
                t,
                metadata,
                batches,
                key,
                item.Action,
                item.ExpectedPriorAuthorizationUuid,
                [item.Source],
                item,
                null,
                ct
            );
            var sourceEvidence = ClaimEvidence(item.Source, item.Claim);
            await ValidateAuthorizationEvidence(
                c,
                t,
                item.Evidence,
                [item.Source],
                sourceEvidence,
                ct
            );
            foreach (var target in item.Targets)
            {
                await ValidateAuthorizationTarget(
                    c,
                    t,
                    command.EditionUuid,
                    item.Claim.SetIndex.HasValue
                        ? SetlistRemovalKind.Set
                        : SetlistRemovalKind.Occurrence,
                    target,
                    ct
                );
            }
            await AppendSetlistAuthorization(
                c,
                t,
                command,
                key,
                "correspondence",
                item.AuthorizationUuid,
                item.Action,
                item.ExpectedPriorAuthorizationUuid,
                item,
                tickets,
                ct
            );
        }
    }

    private static async Task ApplyRemovalApprovals(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistCorrespondenceDecision command,
        SetlistEditionChange metadata,
        Dictionary<Guid, NormalizationSetlistProposalBatch> batches,
        HashSet<string> keys,
        CancellationToken ct
    )
    {
        foreach (var item in command.Removals)
        {
            var key = RemovalKey(item.Kind, item.Target.Uuid);
            RequireUniqueAuthorizationKey(keys, key);
            var tickets = await ReadAuthorizationSourceTickets(
                c,
                t,
                metadata,
                batches,
                key,
                item.Action,
                item.ExpectedPriorAuthorizationUuid,
                item.Sources,
                null,
                item,
                ct
            );
            await ValidateAuthorizationEvidence(c, t, item.Evidence, item.Sources, null, ct);
            await ValidateAuthorizationTarget(
                c,
                t,
                command.EditionUuid,
                item.Kind,
                item.Target,
                ct
            );
            await AppendSetlistAuthorization(
                c,
                t,
                command,
                key,
                "removal",
                item.AuthorizationUuid,
                item.Action,
                item.ExpectedPriorAuthorizationUuid,
                item,
                tickets,
                ct
            );
        }
    }

    private static async Task<
        IReadOnlyList<NormalizationWorkTicket>
    > ReadAuthorizationSourceTickets(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistEditionChange edition,
        Dictionary<Guid, NormalizationSetlistProposalBatch> batches,
        string key,
        SetlistAuthorizationAction action,
        Guid? expectedPrior,
        IReadOnlyList<SetlistStructureSource> sources,
        SetlistCorrespondenceApproval? correspondence,
        SetlistRemovalApproval? removal,
        CancellationToken ct
    )
    {
        if (action != SetlistAuthorizationAction.Authorize && expectedPrior.HasValue)
        {
            // Revocation does not reinterpret an older decoder's account. Compare against the
            // immutable prior approval and retain its original tickets, even after a version bump.
            var head = await ReadSetlistAuthorizationHead(c, t, edition.Uuid, key, ct);
            if (head == null || head.AuthorizationUuid != expectedPrior)
            {
                throw new NormalizationConflictException(
                    "The authorization changed after review was prepared."
                );
            }
            if (correspondence != null)
            {
                var prior = JsonSerializer.Deserialize<SetlistCorrespondenceApproval>(
                    head.Payload
                )!;
                if (
                    head.Kind != "correspondence"
                    || !SameStructureJson(prior.Source, correspondence.Source)
                    || prior.Claim != correspondence.Claim
                    || !prior
                        .Targets.Select(target => target.Uuid)
                        .SequenceEqual(correspondence.Targets.Select(target => target.Uuid))
                )
                {
                    throw new ArgumentException(
                        "Revocation or blocking must retain the prior exact source claim and target identities."
                    );
                }
            }
            else
            {
                var prior = JsonSerializer.Deserialize<SetlistRemovalApproval>(head.Payload)!;
                if (
                    head.Kind != "removal"
                    || prior.Kind != removal!.Kind
                    || prior.Target.Uuid != removal.Target.Uuid
                    || !SameStructureJson(prior.Sources, removal.Sources)
                )
                {
                    throw new ArgumentException(
                        "Revocation or blocking must retain the prior exact removal sources and target identity."
                    );
                }
            }
            return JsonSerializer.Deserialize<NormalizationWorkTicket[]>(head.SourceTickets)!;
        }
        if (action == SetlistAuthorizationAction.Revoke)
        {
            throw new ArgumentException("Revocation requires the exact prior authorization head.");
        }
        foreach (var source in sources)
        {
            ValidateAuthorizationSource(source, batches[source.ProposalAttemptUuid], edition);
        }
        if (removal != null)
        {
            RequireCompleteRemovalSources(sources, batches, edition);
        }
        return sources
            .Select(source => batches[source.ProposalAttemptUuid].Ticket)
            .DistinctBy(ticket => ticket.WorkUuid)
            .ToArray();
    }

    private static void ValidateAuthorizationSource(
        SetlistStructureSource source,
        NormalizationSetlistProposalBatch batch,
        SetlistEditionChange edition
    )
    {
        var extraction = batch.Extractions.SingleOrDefault(item =>
            item.InputRevisionUuid == source.InputRevisionUuid
        );
        if (
            extraction == null
            || source.AccountIndex < 0
            || source.AccountIndex >= extraction.Accounts.Count
            || !SameStructureJson(source.Account, extraction.Accounts[source.AccountIndex])
            || source.Account.Account.Origin.Item
                != new NormalizationResourceKey(
                    edition.Provider,
                    edition.ResourceType,
                    edition.ProviderKey
                )
        )
        {
            throw new ArgumentException(
                "Authorization must reproduce an exact published account belonging to this edition."
            );
        }
    }

    private static void RequireCompleteRemovalSources(
        IReadOnlyList<SetlistStructureSource> sources,
        Dictionary<Guid, NormalizationSetlistProposalBatch> batches,
        SetlistEditionChange edition
    )
    {
        var identity = new NormalizationResourceKey(
            edition.Provider,
            edition.ResourceType,
            edition.ProviderKey
        );
        foreach (
            var batch in sources.Select(source => batches[source.ProposalAttemptUuid]).Distinct()
        )
        {
            foreach (var extraction in batch.Extractions)
            {
                for (var index = 0; index < extraction.Accounts.Count; index++)
                {
                    if (
                        extraction.Accounts[index].Account.Origin.Item == identity
                        && !sources.Any(source =>
                            source.ProposalAttemptUuid == batch.AttemptUuid
                            && source.InputRevisionUuid == extraction.InputRevisionUuid
                            && source.AccountIndex == index
                        )
                    )
                    {
                        throw new ArgumentException(
                            "Removal review must retain every matching account observation in its complete proposal batches."
                        );
                    }
                }
            }
        }
    }

    private static async Task ValidateAuthorizationEvidence(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        IReadOnlyList<NormalizedEvidenceReference> evidence,
        IReadOnlyList<SetlistStructureSource> sources,
        NormalizedEvidenceReference? required,
        CancellationToken ct
    )
    {
        if (
            evidence.Count == 0
            || (required != null && !evidence.Contains(required))
            || evidence.Any(item =>
                !sources.Any(source =>
                    EvidenceInsideSetlistAccount(source.Account.Account.Origin.Evidence, item)
                )
            )
        )
        {
            throw new ArgumentException(
                "Authorization needs exact source evidence, including the set boundary or passage title it interprets."
            );
        }
        await NormalizedPeopleService.ValidateEvidenceAsync(c, t, evidence, ct);
    }

    private static bool EvidenceInsideSetlistAccount(
        NormalizedEvidenceReference account,
        NormalizedEvidenceReference evidence
    )
    {
        if (account.InputRevisionUuid != evidence.InputRevisionUuid)
        {
            return false;
        }
        if (account.StartCodePoint is { } start)
        {
            return account.JsonPointer == evidence.JsonPointer
                && evidence.StartCodePoint >= start
                && evidence.EndCodePoint <= account.EndCodePoint;
        }
        return account.JsonPointer == evidence.JsonPointer
            || evidence.JsonPointer.StartsWith(account.JsonPointer + "/", StringComparison.Ordinal);
    }

    private static NormalizedEvidenceReference ClaimEvidence(
        SetlistStructureSource source,
        SetlistClaimReference claim
    )
    {
        if (claim.SetIndex is { } index && index >= 0 && index < source.Account.Account.Sets.Count)
        {
            return source.Account.Account.Sets[index].Evidence;
        }
        if (
            claim.ProposalUuid is { } uuid
            && source.Account.Passages.SingleOrDefault(item => item.ProposalUuid == uuid)
                is { } passage
        )
        {
            return passage.Passage.Title.Evidence;
        }
        throw new ArgumentException(
            "The reviewed set or passage does not exist at the supplied source coordinates."
        );
    }

    private static async Task ValidateAuthorizationTarget(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid edition,
        SetlistRemovalKind kind,
        SetlistTargetRevision target,
        CancellationToken ct
    )
    {
        if (target.Uuid == Guid.Empty || target.ExpectedRevision < 0)
        {
            throw new ArgumentException(
                "Authorization targets need a UUID and nonnegative revision."
            );
        }
        var table = RemovalKind(kind) switch
        {
            "set" => "setlist_sets",
            "occurrence" => "song_occurrences",
            _ => "setlist_editions",
        };
        var editionPredicate =
            kind == SetlistRemovalKind.Edition ? "x.uuid=@edition" : "x.edition_uuid=@edition";
        var revision = await c.QuerySingleOrDefaultAsync<long?>(
            new CommandDefinition(
                $"SELECT r.revision FROM normalization.{table} x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid WHERE x.uuid=@Uuid AND {editionPredicate}",
                new { target.Uuid, edition },
                t,
                cancellationToken: ct
            )
        );
        var existsElsewhere =
            revision == null
            && await c.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    $"SELECT EXISTS(SELECT 1 FROM normalization.{table} WHERE uuid=@Uuid)",
                    target,
                    t,
                    cancellationToken: ct
                )
            );
        if (existsElsewhere || (revision ?? 0) != target.ExpectedRevision)
        {
            throw new NormalizationConflictException(
                "An authorization target changed or belongs to another edition."
            );
        }
    }

    private static void RequireUniqueAuthorizationKey(HashSet<string> keys, string key)
    {
        if (!keys.Add(key))
        {
            throw new ArgumentException("Each authorization key appears once in a review command.");
        }
    }

    private static string ClaimKey(SetlistStructureSource source, SetlistClaimReference claim)
    {
        if (
            claim.SetIndex.HasValue == claim.ProposalUuid.HasValue
            || claim.SetIndex is < 0
            || claim.ProposalUuid == Guid.Empty
        )
        {
            throw new ArgumentException(
                "A correspondence claim identifies exactly one retained set index or passage proposal UUID."
            );
        }
        return $"claim/{source.ProposalAttemptUuid:D}/{source.InputRevisionUuid:D}/{source.AccountIndex.ToString(CultureInfo.InvariantCulture)}/{(claim.SetIndex.HasValue ? "set/" + claim.SetIndex.Value.ToString(CultureInfo.InvariantCulture) : "passage/" + claim.ProposalUuid!.Value.ToString("D"))}";
    }

    private static string RemovalKind(SetlistRemovalKind kind) =>
        kind switch
        {
            SetlistRemovalKind.Set => "set",
            SetlistRemovalKind.Occurrence => "occurrence",
            SetlistRemovalKind.Edition => "edition",
            _ => throw new ArgumentException("Unknown setlist removal kind."),
        };

    private static string RemovalKey(SetlistRemovalKind kind, Guid uuid) =>
        $"removal/{RemovalKind(kind)}/{uuid:D}";

    private static async Task AppendSetlistAuthorization<T>(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistCorrespondenceDecision command,
        string key,
        string kind,
        Guid uuid,
        SetlistAuthorizationAction action,
        Guid? prior,
        T payload,
        IReadOnlyList<NormalizationWorkTicket> tickets,
        CancellationToken ct
    )
    {
        var head = await c.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                "SELECT authorization_uuid FROM normalization.setlist_authorization_heads WHERE edition_uuid=@EditionUuid AND authorization_key=@key",
                new { command.EditionUuid, key },
                t,
                cancellationToken: ct
            )
        );
        if (head != prior)
        {
            throw new NormalizationConflictException(
                "The authorization changed after review was prepared."
            );
        }
        await c.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO normalization.setlist_authorization_events(uuid,edition_uuid,operation_uuid,authorization_key,kind,action,
                    expected_structure_revision,expected_edition_revision,prior_uuid,payload,source_tickets)
                VALUES(@uuid,@EditionUuid,@OperationUuid,@key,@kind,@action,@ExpectedStructureRevision,@ExpectedEditionRevision,@prior,@payload::jsonb,@tickets::jsonb);
                INSERT INTO normalization.setlist_authorization_heads(edition_uuid,authorization_key,authorization_uuid)
                VALUES(@EditionUuid,@key,@uuid) ON CONFLICT(edition_uuid,authorization_key) DO UPDATE SET authorization_uuid=EXCLUDED.authorization_uuid;
                """,
                new
                {
                    uuid,
                    command.EditionUuid,
                    command.OperationUuid,
                    key,
                    kind,
                    action = action.ToString().ToLowerInvariant(),
                    command.ExpectedStructureRevision,
                    command.ExpectedEditionRevision,
                    prior,
                    payload = JsonSerializer.Serialize(payload),
                    tickets = JsonSerializer.Serialize(tickets),
                },
                t,
                cancellationToken: ct
            )
        );
    }

    private static SetlistAuthorizationRecord AuthorizationRecord(SetlistAuthorizationRow row) =>
        new(
            row.AuthorizationUuid,
            row.EditionUuid,
            row.OperationUuid,
            row.Kind,
            Enum.Parse<SetlistAuthorizationAction>(row.Action, true),
            row.Kind == "correspondence"
                ? JsonSerializer.Deserialize<SetlistCorrespondenceApproval>(row.Payload)
                : null,
            row.Kind == "removal"
                ? JsonSerializer.Deserialize<SetlistRemovalApproval>(row.Payload)
                : null,
            JsonSerializer.Deserialize<NormalizationWorkTicket[]>(row.SourceTickets)!,
            row.Active,
            row.CreatedAt,
            row.ProcessorDecisionUuid
        );

    private sealed record SetlistAuthorizationRow(
        Guid AuthorizationUuid,
        Guid EditionUuid,
        Guid? OperationUuid,
        string AuthorizationKey,
        string Kind,
        string Action,
        string Payload,
        string SourceTickets,
        DateTime CreatedAt,
        bool Active,
        Guid? ProcessorDecisionUuid = null
    );
}
