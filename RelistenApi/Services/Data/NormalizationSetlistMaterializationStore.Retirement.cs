using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationSetlistMaterializationStore
{
    // Each cleanup retry retains its own original peer snapshot and actual admission.
    // Only the current slot changes; an older ticket can never authenticate a newer cleanup.
    internal Task<SetlistMaterializationAttempt> SaveRetirementAsync(
        SetlistMaterializationAttempt expected,
        SetlistMaterializationRetirement retirement,
        CancellationToken ct = default
    )
    {
        var json = Serialize(retirement, MaximumPreparationBytes);
        var frozen = JsonSerializer.Deserialize<SetlistMaterializationRetirement>(json)!;
        if (
            frozen.Ticket != null
            || frozen.Receipt != null
            || frozen.FailureReason != null
            || frozen.Superseded
            || frozen.WorkUuid == Guid.Empty
        )
        {
            throw new ArgumentException("Save an original, unadmitted watcher retirement.");
        }
        return InTransaction(
            async (c, t) =>
            {
                var current = await RequireAttempt(c, t, expected.AttemptUuid, ct);
                if (current.Retirement?.WorkUuid == frozen.WorkUuid)
                {
                    var original = current.Retirement with
                    {
                        Ticket = null,
                        Receipt = null,
                        FailureReason = null,
                        Superseded = false,
                    };
                    RequireSame(original, frozen, "The original cleanup snapshot is immutable.");
                    return current;
                }
                if (current.Result != null || !SameCheckpoint(current, expected))
                {
                    return current;
                }
                var requirement = current.Preparation?.ContributionsToRetire.SingleOrDefault(
                    value => value.Owner == frozen.Owner && value.Contributor == frozen.Contributor
                );
                if (
                    current.Receipt?.Status != NormalizationPublicationStatus.Published
                    || requirement == null
                    || !current.Preparation!.RequiresObservationRetirement
                    || frozen.AdoptedWorkUuid != current.AttemptUuid
                    || frozen.Owner != requirement.Owner
                    || frozen.Contributor != requirement.Contributor
                    || frozen.ExpectedContributionRevision < requirement.ContributionRevision
                )
                {
                    throw new NormalizationConflictException(
                        "Watcher cleanup requires its saved canonical adoption and original ownership."
                    );
                }
                if (current.Retirement is { } previous)
                {
                    var completed = previous.Receipt != null || previous.Superseded;
                    var sameOwner =
                        previous.Owner == frozen.Owner
                        && previous.Contributor == frozen.Contributor;
                    if (completed && sameOwner)
                    {
                        throw new NormalizationConflictException(
                            "A completed owner cleanup cannot be replaced."
                        );
                    }
                    var stale =
                        previous.Ticket != null
                        && await c.ExecuteScalarAsync<bool>(
                            new CommandDefinition(
                                "SELECT EXISTS(SELECT 1 FROM normalization.work_runs WHERE uuid=@uuid AND state='stale')",
                                new { uuid = previous.WorkUuid },
                                t,
                                cancellationToken: ct
                            )
                        );
                    if (!completed && (!sameOwner || previous.FailureReason == null && !stale))
                    {
                        throw new NormalizationConflictException(
                            "Finish the current owner cleanup before advancing or retrying it."
                        );
                    }
                }
                if (
                    current.CompletedRetirements.Any(value =>
                        value.Owner == requirement.Owner
                        && value.Contributor == requirement.Contributor
                        && value.ObservedContributionRevision == requirement.ContributionRevision
                    )
                )
                {
                    throw new NormalizationConflictException(
                        "This saved contribution cleanup is already complete."
                    );
                }
                var ordinal = await c.ExecuteScalarAsync<int>(
                    new CommandDefinition(
                        "SELECT COALESCE(max(ordinal)+1,0) FROM normalization.setlist_materialization_retirements WHERE attempt_uuid=@uuid",
                        new { uuid = current.AttemptUuid },
                        t,
                        cancellationToken: ct
                    )
                );
                if (ordinal >= 1000)
                {
                    throw new MusicMaterializationCapacityException(
                        "Watcher cleanup retry capacity is exhausted."
                    );
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "INSERT INTO normalization.setlist_materialization_retirements(work_uuid,attempt_uuid,ordinal,observed_contribution_revision,retirement) VALUES(@work,@attempt,@ordinal,@observed,@json::jsonb)",
                        new
                        {
                            work = frozen.WorkUuid,
                            attempt = current.AttemptUuid,
                            ordinal,
                            observed = requirement.ContributionRevision,
                            json,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                await AdvanceRetirementCheckpoint(c, t, current.AttemptUuid, frozen.WorkUuid, ct);
                return (await Read(c, t, current.AttemptUuid, false, true, ct))!;
            },
            ct
        );
    }

    // A post-publication capacity refusal remains pending; it cannot hide live obsolete keys
    // in a terminal result. Only changed capacity coordinates reopen this same cleanup attempt.
    internal Task<SetlistMaterializationAttempt> SaveCleanupRetryRequirementAsync(
        SetlistMaterializationAttempt expected,
        MusicMaterializationRetryRequirement? requirement,
        CancellationToken ct = default
    )
    {
        var json = Serialize(requirement, MaximumDecisionPageBytes);
        var frozen = JsonSerializer.Deserialize<MusicMaterializationRetryRequirement>(json);
        return InTransaction(
            async (c, t) =>
            {
                var current = await RequireAttempt(c, t, expected.AttemptUuid, ct);
                if (current.Result != null || !SameCheckpoint(current, expected))
                {
                    return current;
                }
                if (
                    current.Receipt?.Status != NormalizationPublicationStatus.Published
                    || RetirementsCompleted(current)
                )
                {
                    throw new NormalizationConflictException(
                        "Only a published successor with unfinished contribution cleanup records a pending capacity requirement."
                    );
                }
                if (current.CleanupRetryRequirement == frozen)
                {
                    return current;
                }
                if (
                    current.CleanupRetryRequirement is { } previous
                    && previous.ProcessorVersion == NormalizationSetlistMaterializer.Version
                    && previous.LimitProfile == NormalizationSetlistMaterializer.LimitProfile
                    && !await CapacityObservationChanged(c, t, previous, ct)
                )
                {
                    throw new NormalizationConflictException(
                        "The unchanged cleanup capacity requirement is still pending."
                    );
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE normalization.setlist_materialization_attempts SET cleanup_retry_requirement=@json::jsonb,checkpoint_revision=checkpoint_revision+1 WHERE attempt_uuid=@uuid",
                        new { uuid = current.AttemptUuid, json = frozen == null ? null : json },
                        t,
                        cancellationToken: ct
                    )
                );
                return (await Read(c, t, current.AttemptUuid, false, true, ct))!;
            },
            ct
        );
    }

    internal Task<SetlistMaterializationAttempt> SaveRetirementTicketAsync(
        SetlistMaterializationAttempt expected,
        NormalizationWorkTicket ticket,
        CancellationToken ct = default
    ) => SaveRetirementAdmission(expected, ticket, null, ct);

    internal Task<SetlistMaterializationAttempt> SaveFailedRetirementAdmissionAsync(
        SetlistMaterializationAttempt expected,
        NormalizationWorkTicket ticket,
        string reason,
        CancellationToken ct = default
    ) => SaveRetirementAdmission(expected, ticket, RequireReason(reason), ct);

    private Task<SetlistMaterializationAttempt> SaveRetirementAdmission(
        SetlistMaterializationAttempt expected,
        NormalizationWorkTicket ticket,
        string? failure,
        CancellationToken ct
    )
    {
        var json = Serialize(ticket, MaximumDecisionPageBytes);
        var frozen = JsonSerializer.Deserialize<NormalizationWorkTicket>(json)!;
        return InTransaction(
            async (c, t) =>
            {
                var current = await RequireAttempt(c, t, expected.AttemptUuid, ct);
                var cleanup =
                    current.Retirement
                    ?? throw new NormalizationConflictException(
                        "Prepare watcher cleanup before admission."
                    );
                if (cleanup.Ticket != null)
                {
                    RequireSame(cleanup.Ticket, frozen, "The cleanup admission is immutable.");
                    if (failure == null || cleanup.FailureReason != null)
                    {
                        return current;
                    }
                }
                if (current.Result != null || !SameCheckpoint(current, expected))
                {
                    return current;
                }
                if (cleanup.Receipt != null || cleanup.Superseded || cleanup.FailureReason != null)
                {
                    throw new NormalizationConflictException(
                        "Only an unfinished watcher cleanup may be admitted."
                    );
                }
                await RequireRetirementRun(c, t, cleanup, frozen, false, ct);
                if (failure == null)
                {
                    RequireRetirementTicket(current, frozen);
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE normalization.setlist_materialization_retirements SET ticket=@json::jsonb,admitted_work_uuid=work_uuid,failure_reason=@failure WHERE work_uuid=@uuid",
                        new
                        {
                            uuid = cleanup.WorkUuid,
                            json,
                            failure,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                await AdvanceRetirementCheckpoint(c, t, current.AttemptUuid, cleanup.WorkUuid, ct);
                return (await Read(c, t, current.AttemptUuid, false, true, ct))!;
            },
            ct
        );
    }

    internal Task<SetlistMaterializationAttempt> FailRetirementAsync(
        SetlistMaterializationAttempt expected,
        string reason,
        CancellationToken ct = default
    )
    {
        reason = RequireReason(reason);
        return InTransaction(
            async (c, t) =>
            {
                var current = await RequireAttempt(c, t, expected.AttemptUuid, ct);
                var cleanup =
                    current.Retirement
                    ?? throw new NormalizationConflictException("No watcher cleanup exists.");
                if (
                    current.Result != null
                    || !SameCheckpoint(current, expected)
                    || cleanup.FailureReason != null
                )
                {
                    return current;
                }
                if (cleanup.Receipt != null || cleanup.Superseded)
                {
                    throw new NormalizationConflictException("A completed cleanup cannot fail.");
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE normalization.setlist_materialization_retirements SET failure_reason=@reason WHERE work_uuid=@uuid",
                        new { uuid = cleanup.WorkUuid, reason },
                        t,
                        cancellationToken: ct
                    )
                );
                await AdvanceRetirementCheckpoint(c, t, current.AttemptUuid, cleanup.WorkUuid, ct);
                return (await Read(c, t, current.AttemptUuid, false, true, ct))!;
            },
            ct
        );
    }

    internal Task<SetlistMaterializationAttempt> MarkRetirementSupersededAsync(
        SetlistMaterializationAttempt expected,
        CancellationToken ct = default
    ) =>
        InTransaction(
            async (c, t) =>
            {
                var current = await RequireAttempt(c, t, expected.AttemptUuid, ct);
                var cleanup =
                    current.Retirement
                    ?? throw new NormalizationConflictException("No watcher cleanup exists.");
                if (
                    current.Result != null
                    || !SameCheckpoint(current, expected)
                    || cleanup.Superseded
                )
                {
                    return current;
                }
                if (cleanup.Receipt != null)
                {
                    throw new NormalizationConflictException(
                        "A published cleanup cannot be superseded."
                    );
                }
                var revision = await c.QuerySingleOrDefaultAsync<long?>(
                    new CommandDefinition(
                        """
                        SELECT owned.revision FROM normalization.work_input_contributions owned
                        JOIN normalization.work_subjects owner ON owner.uuid=owned.subject_uuid
                        WHERE owner.domain=@Domain AND owner.subject_kind=@Kind AND owner.subject_uuid=@Uuid
                            AND owned.contributor_provider=@Provider AND owned.contributor_type=@ResourceType AND owned.contributor_key=@ProviderKey
                        FOR UPDATE OF owned
                        """,
                        new
                        {
                            cleanup.Owner.Domain,
                            cleanup.Owner.Kind,
                            cleanup.Owner.Uuid,
                            cleanup.Contributor.Provider,
                            cleanup.Contributor.ResourceType,
                            cleanup.Contributor.ProviderKey,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                if (revision == null || revision <= cleanup.ExpectedContributionRevision)
                {
                    throw new NormalizationConflictException(
                        "Only a strictly newer owned contribution supersedes the saved cleanup."
                    );
                }
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE normalization.setlist_materialization_retirements SET superseded=true WHERE work_uuid=@uuid",
                        new { uuid = cleanup.WorkUuid },
                        t,
                        cancellationToken: ct
                    )
                );
                await AdvanceRetirementCheckpoint(c, t, current.AttemptUuid, cleanup.WorkUuid, ct);
                return (await Read(c, t, current.AttemptUuid, false, true, ct))!;
            },
            ct
        );

    internal async Task<SetlistMaterializationAttempt> RequireRetirementForPublicationAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid attemptUuid,
        Guid workUuid,
        CancellationToken ct
    )
    {
        var attempt = await RequireAttempt(c, t, attemptUuid, ct);
        var cleanup = attempt.Retirement;
        if (
            attempt.Result != null
            || attempt.Receipt?.Status != NormalizationPublicationStatus.Published
            || cleanup?.WorkUuid != workUuid
            || cleanup.Ticket == null
            || cleanup.Receipt != null
            || cleanup.FailureReason != null
            || cleanup.Superseded
        )
        {
            throw new NormalizationConflictException(
                "Publish only the current admitted watcher cleanup after canonical adoption."
            );
        }
        await RequireRetirementRun(c, t, cleanup, cleanup.Ticket, true, ct);
        RequireRetirementTicket(attempt, cleanup.Ticket);
        return attempt;
    }

    internal async Task WriteRetirementPublishedAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid attemptUuid,
        NormalizationPublicationResult receipt,
        CancellationToken ct
    )
    {
        var json = Serialize(receipt, MaximumDecisionPageBytes);
        var current = await RequireAttempt(c, t, attemptUuid, ct);
        var cleanup =
            current.Retirement
            ?? throw new NormalizationConflictException("No watcher cleanup exists.");
        if (cleanup.Receipt != null)
        {
            RequireSame(cleanup.Receipt, receipt, "The cleanup receipt is immutable.");
            return;
        }
        await RequireRetirementForPublicationAsync(c, t, attemptUuid, receipt.WorkUuid, ct);
        if (receipt.Status != NormalizationPublicationStatus.Published)
        {
            throw new NormalizationConflictException(
                "Watcher retirement needs its actual successful Work receipt."
            );
        }
        await c.ExecuteAsync(
            new CommandDefinition(
                "UPDATE normalization.setlist_materialization_retirements SET receipt=@json::jsonb WHERE work_uuid=@uuid",
                new { uuid = cleanup.WorkUuid, json },
                t,
                cancellationToken: ct
            )
        );
        await AdvanceRetirementCheckpoint(c, t, attemptUuid, cleanup.WorkUuid, ct);
        current = (await Read(c, t, attemptUuid, false, true, ct))!;
        if (RetirementsCompleted(current))
        {
            await SaveResult(c, t, current, BuildCompletedResult(current), ct);
        }
    }

    private static void RequireRetirementTicket(
        SetlistMaterializationAttempt attempt,
        NormalizationWorkTicket ticket
    )
    {
        var cleanup = attempt.Retirement!;
        var read = cleanup.ReadSet;
        var savedOwner = cleanup.OwnerContext.Membership.Owner;
        if (
            ticket.WorkUuid != cleanup.WorkUuid
            || ticket.PolicyVersion != attempt.ProcessingVersion
            || ticket.EditorialRevision != (savedOwner?.EditorialRevision ?? 0)
            || savedOwner != null && ticket.SubjectUuid != savedOwner.SubjectUuid
            || !SetEquals(ticket.Inputs, read.Inputs)
            || !SetEquals(ticket.Dependencies, read.Dependencies)
            || !SetEquals(ticket.CatalogInputs ?? [], read.CatalogInputs)
            || !SetEquals(ticket.SourceIdentities ?? [], read.SourceIdentities)
            || ticket.MappingRegistry != read.MappingRegistry
            || !SetEquals(ticket.ImportedVenues ?? [], read.ImportedVenues)
            || !SetEquals(ticket.CandidateUniverses ?? [], read.CandidateUniverses)
            || !SetEquals(ticket.OwnerObservations ?? [], read.OwnerObservations)
            || !SetEquals(ticket.SourceUuids ?? [], read.SourceUuids)
        )
        {
            throw new NormalizationConflictException(
                "Cleanup must retain its exact fresh peer coordination snapshot."
            );
        }
    }

    private static async Task RequireRetirementRun(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistMaterializationRetirement cleanup,
        NormalizationWorkTicket ticket,
        bool publishing,
        CancellationToken ct
    )
    {
        var row = await c.QuerySingleOrDefaultAsync<WorkRow>(
            new CommandDefinition(
                "SELECT s.domain AS \"Domain\",s.subject_kind AS \"Kind\",s.subject_uuid AS \"Uuid\",r.state AS \"State\",r.ticket::text AS \"Ticket\" FROM normalization.work_runs r JOIN normalization.work_subjects s ON s.uuid=r.subject_uuid WHERE r.uuid=@uuid",
                new { uuid = ticket.WorkUuid },
                t,
                cancellationToken: ct
            )
        );
        if (
            ticket.WorkUuid != cleanup.WorkUuid
            || row == null
            || (publishing ? row.State != "running" : row.State is not ("running" or "stale"))
            || cleanup.Owner != new NormalizationSubjectKey(row.Domain, row.Kind, row.Uuid)
            || !Same(ticket, JsonSerializer.Deserialize<NormalizationWorkTicket>(row.Ticket))
        )
        {
            throw new NormalizationConflictException(
                "Cleanup requires its exact actual Work admission and owner."
            );
        }
    }

    private static Task<int> AdvanceRetirementCheckpoint(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid attempt,
        Guid work,
        CancellationToken ct
    ) =>
        c.ExecuteAsync(
            new CommandDefinition(
                "UPDATE normalization.setlist_materialization_attempts SET retirement_work_uuid=@work,checkpoint_revision=checkpoint_revision+1 WHERE attempt_uuid=@attempt",
                new { attempt, work },
                t,
                cancellationToken: ct
            )
        );

    private static string RequireReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1024)
        {
            throw new ArgumentException("A bounded cleanup failure reason is required.");
        }
        return reason;
    }

    private static async Task<SetlistMaterializationRetirement?> ReadRetirement(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid attempt,
        Guid work,
        bool full,
        CancellationToken ct
    )
    {
        var size = await c.ExecuteScalarAsync<long>(
            new CommandDefinition(
                "SELECT COALESCE(octet_length(retirement::text),0)+COALESCE(octet_length(ticket::text),0) FROM normalization.setlist_materialization_retirements WHERE attempt_uuid=@attempt AND work_uuid=@work",
                new { attempt, work },
                t,
                cancellationToken: ct
            )
        );
        if (size > MaximumPreparationBytes + MaximumDecisionPageBytes)
        {
            throw new InvalidOperationException(
                "Watcher cleanup exceeds its bounded read profile."
            );
        }
        var original = full
            ? "retirement"
            : "retirement || jsonb_build_object('OwnerContext',null,'ReadSet',null)";
        var row = await c.QuerySingleOrDefaultAsync<RetirementRow>(
            new CommandDefinition(
                "SELECT ("
                    + original
                    + ")::text AS \"Original\","
                    + (full ? "ticket::text" : "NULL::text")
                    + " AS \"Ticket\",receipt::text AS \"Receipt\",failure_reason AS \"Failure\",superseded AS \"Superseded\" FROM normalization.setlist_materialization_retirements WHERE attempt_uuid=@attempt AND work_uuid=@work",
                new { attempt, work },
                t,
                cancellationToken: ct
            )
        );
        if (row == null)
        {
            return null;
        }
        return JsonSerializer.Deserialize<SetlistMaterializationRetirement>(row.Original)! with
        {
            Ticket =
                row.Ticket == null
                    ? null
                    : JsonSerializer.Deserialize<NormalizationWorkTicket>(row.Ticket),
            Receipt =
                row.Receipt == null
                    ? null
                    : JsonSerializer.Deserialize<NormalizationPublicationResult>(row.Receipt),
            FailureReason = row.Failure,
            Superseded = row.Superseded,
        };
    }

    private static bool RetirementsCompleted(SetlistMaterializationAttempt attempt) =>
        attempt.Preparation?.ContributionsToRetire.All(required =>
            attempt.CompletedRetirements.Any(done =>
                done.Owner == required.Owner
                && done.Contributor == required.Contributor
                && done.ObservedContributionRevision == required.ContributionRevision
                && (
                    done.Superseded
                    || done.Receipt?.Status == NormalizationPublicationStatus.Published
                )
            )
        ) == true;

    private static async Task<SetlistCompletedContributionRetirement[]> ReadCompletedRetirements(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid attempt,
        CancellationToken ct
    )
    {
        var rows = await c.QueryAsync<CompletedRetirementRow>(
            new CommandDefinition(
                """
                SELECT (retirement->'Owner')::text AS "Owner",(retirement->'Contributor')::text AS "Contributor",
                    observed_contribution_revision AS "ObservedRevision",work_uuid AS "WorkUuid",
                    receipt::text AS "Receipt",superseded AS "Superseded"
                FROM normalization.setlist_materialization_retirements
                WHERE attempt_uuid=@attempt AND (receipt IS NOT NULL OR superseded) ORDER BY ordinal
                """,
                new { attempt },
                t,
                cancellationToken: ct
            )
        );
        return rows.Select(row => new SetlistCompletedContributionRetirement(
                JsonSerializer.Deserialize<NormalizationSubjectKey>(row.Owner)!,
                JsonSerializer.Deserialize<NormalizationResourceKey>(row.Contributor)!,
                row.ObservedRevision,
                row.WorkUuid,
                row.Receipt == null
                    ? null
                    : JsonSerializer.Deserialize<NormalizationPublicationResult>(row.Receipt),
                row.Superseded
            ))
            .ToArray();
    }

    private sealed record CompletedRetirementRow(
        string Owner,
        string Contributor,
        long ObservedRevision,
        Guid WorkUuid,
        string? Receipt,
        bool Superseded
    );

    private sealed record RetirementRow(
        string Original,
        string? Ticket,
        string? Receipt,
        string? Failure,
        bool Superseded
    );
}
