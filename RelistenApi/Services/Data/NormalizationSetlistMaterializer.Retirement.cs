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

public sealed partial class NormalizationSetlistMaterializer
{
    private static async Task<
        IReadOnlyList<SetlistContributionRetirementRequirement>
    > ReadContributionsToRetireAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistMaterializationRegistration registration,
        NormalizationSubjectKey successor,
        CancellationToken ct
    )
    {
        var contributor = ContributionKey(registration.RegistrationUuid);
        var owners = (
            await c.QueryAsync<NormalizationSubjectKey>(
                new CommandDefinition(
                    """
                    SELECT owner.domain AS "Domain",owner.subject_kind AS "Kind",owner.subject_uuid AS "Uuid"
                    FROM normalization.work_input_contributions contribution JOIN normalization.work_subjects owner ON owner.uuid=contribution.subject_uuid
                    WHERE contribution.contributor_provider=@Provider AND contribution.contributor_type=@ResourceType AND contribution.contributor_key=@ProviderKey
                        AND NOT(owner.domain=@Domain AND owner.subject_kind=@Kind AND owner.subject_uuid=@Uuid)
                        AND (EXISTS(SELECT 1 FROM normalization.work_contribution_inputs i WHERE i.contribution_uuid=contribution.uuid)
                            OR EXISTS(SELECT 1 FROM normalization.work_contribution_dependencies i WHERE i.contribution_uuid=contribution.uuid)
                            OR EXISTS(SELECT 1 FROM normalization.work_contribution_catalog_inputs i WHERE i.contribution_uuid=contribution.uuid)
                            OR EXISTS(SELECT 1 FROM normalization.work_contribution_source_identities i WHERE i.contribution_uuid=contribution.uuid)
                            OR EXISTS(SELECT 1 FROM normalization.work_contribution_mapping_registry_inputs i WHERE i.contribution_uuid=contribution.uuid)
                            OR EXISTS(SELECT 1 FROM normalization.work_contribution_venue_inputs i WHERE i.contribution_uuid=contribution.uuid)
                            OR EXISTS(SELECT 1 FROM normalization.work_contribution_candidate_inputs i WHERE i.contribution_uuid=contribution.uuid)
                            OR EXISTS(SELECT 1 FROM normalization.work_contribution_owner_observations i WHERE i.contribution_uuid=contribution.uuid)
                            OR EXISTS(SELECT 1 FROM normalization.work_contribution_source_uuids i WHERE i.contribution_uuid=contribution.uuid))
                    ORDER BY owner.uuid LIMIT 1001
                    """,
                    new
                    {
                        contributor.Provider,
                        contributor.ResourceType,
                        contributor.ProviderKey,
                        successor.Domain,
                        successor.Kind,
                        successor.Uuid,
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (owners.Length > 1000)
        {
            throw new MusicMaterializationCapacityException(
                "The private subscription owner history exceeds 1000 owners."
            );
        }
        var requirements = new List<SetlistContributionRetirementRequirement>();
        long bytes = 0;
        foreach (var owner in owners)
        {
            if (
                owner.Domain != "songs"
                || owner.Kind is not ("setlist" or "setlist_materialization")
            )
            {
                throw new UnsupportedSetlistSourceException(
                    "The private setlist contributor has an unsupported former owner."
                );
            }
            var membership = await NormalizationWorkService.GetInputContributionsInSnapshotAsync(
                c,
                t,
                owner,
                new(1000, 100000),
                ct
            );
            var observed = membership.Contributions.Single(value =>
                value.Contributor == contributor
            );
            InstallingAdmission admission;
            try
            {
                admission = await ReadInstallingAdmissionAsync(
                    c,
                    t,
                    registration,
                    owner,
                    observed,
                    ct
                );
            }
            catch (MusicMaterializationCapacityException error)
            {
                throw WithCapacityOwner(error, membership);
            }
            var requirement = new SetlistContributionRetirementRequirement(
                owner,
                contributor,
                observed.Revision,
                admission.WorkUuid,
                admission.Ticket,
                admission.InstalledRevision,
                observed
            );
            bytes += MeasureBoundedJson(requirement, 32 * 1024 * 1024);
            if (bytes > 32 * 1024 * 1024)
            {
                throw WithCapacityOwner(
                    new("The complete former subscription proof exceeds 32 MiB."),
                    membership
                );
            }
            requirements.Add(requirement);
        }
        return requirements;
    }

    internal static async Task<InstallingAdmission> ReadInstallingAdmissionAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistMaterializationRegistration registration,
        NormalizationSubjectKey owner,
        NormalizationInputContributionState observed,
        CancellationToken ct
    )
    {
        var metadata = await c.QuerySingleOrDefaultAsync<InstallingAdmissionMetadata>(
            new CommandDefinition(
                """
                SELECT attempt.attempt_uuid AS "WorkUuid",
                    (COALESCE(octet_length((attempt.preparation->'Context'->'OwnReadSet')::text),0)
                        +COALESCE(octet_length((attempt.preparation->'Context'->'InputReferences')::text),0)
                        +octet_length(run.ticket::text))::bigint AS "Bytes"
                FROM normalization.setlist_materialization_attempts attempt JOIN normalization.work_runs run ON run.uuid=attempt.attempt_uuid
                JOIN normalization.work_subjects subject ON subject.uuid=run.subject_uuid
                WHERE attempt.registration_uuid=@registration AND attempt.preparation->'Context'->'Owner'=@owner::jsonb
                    AND attempt.preparation->'Context'->'Consumption'->'Setlist'->>'RegistrationUuid'=@processing::text
                    AND subject.domain=@Domain AND subject.subject_kind=@Kind AND subject.subject_uuid=@Uuid
                    AND run.state IN ('running','stale','published') AND run.ticket->>'PolicyVersion'=attempt.processing_version
                    AND (attempt.ticket IS NULL OR attempt.ticket=run.ticket)
                ORDER BY attempt.sequence DESC LIMIT 1
                """,
                new
                {
                    registration = registration.RegistrationUuid,
                    owner = JsonSerializer.Serialize(owner),
                    processing = registration.ProcessingRegistrationUuid,
                    owner.Domain,
                    owner.Kind,
                    owner.Uuid,
                },
                t,
                cancellationToken: ct
            )
        );
        if (metadata == null)
        {
            throw new UnsupportedSetlistSourceException(
                "A former private subscription has no original materializer admission proof."
            );
        }
        if (metadata.Bytes > 32 * 1024 * 1024)
        {
            throw new MusicMaterializationCapacityException(
                "The original private subscription admission exceeds 32 MiB."
            );
        }
        var row = await c.QuerySingleAsync<InstallingAdmissionRow>(
            new CommandDefinition(
                """
                SELECT (attempt.preparation->'Context'->'OwnReadSet')::text AS "ReadSet",
                    (attempt.preparation->'Context'->'InputReferences')::text AS "References",run.ticket::text AS "Ticket",
                    COALESCE((SELECT (member->>'Revision')::bigint FROM jsonb_array_elements(
                        attempt.preparation->'Context'->'OwnerContext'->'Membership'->'Contributions') member
                        WHERE member->'Contributor'=@contributor::jsonb),0)+1 AS "InstalledRevision"
                FROM normalization.setlist_materialization_attempts attempt JOIN normalization.work_runs run ON run.uuid=attempt.attempt_uuid
                WHERE attempt.attempt_uuid=@uuid
                """,
                new
                {
                    uuid = metadata.WorkUuid,
                    contributor = JsonSerializer.Serialize(observed.Contributor),
                },
                t,
                cancellationToken: ct
            )
        );
        var own =
            row.ReadSet == null
                ? null
                : JsonSerializer.Deserialize<RepertoireResolutionReadSet>(row.ReadSet);
        NormalizationBindingInputReference[] references =
            row.References == null
                ? []
                : JsonSerializer.Deserialize<NormalizationBindingInputReference[]>(row.References)!;
        var ticket = JsonSerializer.Deserialize<NormalizationWorkTicket>(row.Ticket)!;
        if (
            own == null
            || ticket.WorkUuid != metadata.WorkUuid
            || ticket.SubjectUuid != observed.SubjectUuid
            || row.InstalledRevision > observed.Revision
            || !OwnedKeysMatch(own, references, observed)
        )
        {
            throw new UnsupportedSetlistSourceException(
                "The current private subscription differs from its saved installing admission."
            );
        }
        return new(metadata.WorkUuid, ticket, row.InstalledRevision);
    }

    internal static bool OwnedKeysMatch(
        RepertoireResolutionReadSet read,
        IReadOnlyList<NormalizationBindingInputReference> references,
        NormalizationInputContributionState observed
    )
    {
        var revisions = read.Inputs.Select(value => value.RevisionUuid).ToHashSet();
        var resources = references
            .Where(value => revisions.Contains(value.InputRevisionUuid))
            .Select(value => value.Resource)
            .ToArray();
        return resources.Length == revisions.Count
            && SameMembers(resources, observed.Resources)
            && SameMembers(
                read.Dependencies.Select(value => value.SubjectUuid),
                observed.DependencySubjectUuids
            )
            && SameMembers(
                read.CatalogInputs.Select(value => value.ArtistUuid),
                observed.CatalogArtistUuids
            )
            && SameMembers(
                read.SourceIdentities.Select(value => value.UpstreamIdentifier),
                observed.SourceIdentifiers
            )
            && (read.MappingRegistry != null) == observed.UsesMappingRegistry
            && SameMembers(
                read.ImportedVenues.Select(value => value.VenueUuid),
                observed.ImportedVenueUuids ?? []
            )
            && SameMembers(
                read.CandidateUniverses.Select(value => value.Universe),
                observed.CandidateUniverses ?? []
            )
            && SameMembers(
                read.OwnerObservations.Select(value => value.SubjectUuid),
                observed.OwnerObservationSubjectUuids ?? []
            )
            && SameMembers(
                read.SourceUuids.Select(value => value.SourceUuid),
                observed.SourceUuids ?? []
            );
    }

    private async Task<SetlistMaterializationAttempt> ResumeRetirementAsync(
        SetlistMaterializationAttempt attempt,
        CancellationToken ct
    )
    {
        if (attempt.CleanupRetryRequirement is { } blocked)
        {
            var changed =
                blocked.ProcessorVersion != Version
                || blocked.LimitProfile != LimitProfile
                || blocked.CapacityOwner != null
                    && await CapacityOwnerChangedAsync(
                        blocked.CapacityOwner,
                        blocked.CapacityMembership,
                        ct
                    );
            if (!changed)
            {
                return attempt;
            }
            return await store.SaveCleanupRetryRequirementAsync(attempt, null, ct);
        }
        try
        {
            return await AdvanceRetirementAsync(attempt, ct);
        }
        catch (NormalizationInputContributionsCapacityException error)
        {
            return await SaveCleanupCapacityAsync(attempt, error.Owner, error.Membership, ct);
        }
        catch (MusicMaterializationCapacityException error)
        {
            return await SaveCleanupCapacityAsync(attempt, error.Owner, error.Membership, ct);
        }
    }

    private Task<SetlistMaterializationAttempt> SaveCleanupCapacityAsync(
        SetlistMaterializationAttempt attempt,
        NormalizationSubjectObservation? owner,
        NormalizationInputContributionsCapacityStamp? membership,
        CancellationToken ct
    ) =>
        store.SaveCleanupRetryRequirementAsync(
            attempt,
            new(
                attempt.SelectedRepertoireAttemptUuid,
                Version,
                LimitProfile,
                "materialization_capacity_exceeded",
                owner,
                membership
            ),
            ct
        );

    private async Task<SetlistMaterializationAttempt> AdvanceRetirementAsync(
        SetlistMaterializationAttempt attempt,
        CancellationToken ct
    )
    {
        var requirement = RequirePreparation(attempt)
            .ContributionsToRetire.FirstOrDefault(value =>
                !attempt.CompletedRetirements.Any(done =>
                    done.Owner == value.Owner
                    && done.Contributor == value.Contributor
                    && done.ObservedContributionRevision == value.ContributionRevision
                    && (
                        done.Superseded
                        || done.Receipt?.Status == NormalizationPublicationStatus.Published
                    )
                )
            );
        if (requirement == null)
        {
            return await store.CompleteAsync(attempt, CompletedResult(attempt), ct);
        }
        if (
            attempt.Retirement == null
            || attempt.Retirement.Receipt != null
            || attempt.Retirement.Superseded
            || attempt.Retirement.FailureReason != null
        )
        {
            return await PrepareRetirementAsync(attempt, requirement, ct);
        }
        var cleanup = attempt.Retirement;
        if (cleanup.Owner != requirement.Owner || cleanup.Contributor != requirement.Contributor)
        {
            throw new NormalizationConflictException(
                "Finish the current owned subscription cleanup before advancing another owner."
            );
        }
        if (cleanup.Ticket == null)
        {
            try
            {
                var ticket = await AdmitContributionAsync(
                    cleanup.WorkUuid,
                    cleanup.Owner,
                    attempt.ProcessingVersion,
                    cleanup.Contributor,
                    cleanup.OwnerContext,
                    RepertoireResolutionReadSets.Empty,
                    cleanup.OwnerContext.InputReferences,
                    ct
                );
                if (!AdmissionMatches(ticket, cleanup.ReadSet, cleanup.OwnerContext))
                {
                    return await store.SaveFailedRetirementAdmissionAsync(
                        attempt,
                        ticket,
                        "retirement_read_set_changed",
                        ct
                    );
                }
                return await store.SaveRetirementTicketAsync(attempt, ticket, ct);
            }
            catch (NormalizationConflictException)
            {
                return await store.FailRetirementAsync(
                    attempt,
                    "retirement_coordination_changed",
                    ct
                );
            }
        }
        var receipt = await music.PublishSetlistMaterializationRetirementAsync(
            cleanup.WorkUuid,
            attempt.AttemptUuid,
            ct
        );
        return receipt.Status == NormalizationPublicationStatus.Stale
            ? await store.FailRetirementAsync(attempt, "retirement_publication_stale", ct)
            : (await store.GetProcessingAsync(attempt.AttemptUuid, ct))!;
    }

    private async Task<SetlistMaterializationAttempt> PrepareRetirementAsync(
        SetlistMaterializationAttempt attempt,
        SetlistContributionRetirementRequirement requirement,
        CancellationToken ct
    )
    {
        // Cleanup reads current peer coordination after the successor receipt. Original
        // source and policy freshness cannot strand an obsolete owned subscription.
        var snapshot = await ReadRetirementOwnerAsync(attempt.Registration, requirement, ct);
        var context = snapshot.Context;
        var observed = context.Membership.Contributions.SingleOrDefault(value =>
            value.Contributor == requirement.Contributor
        );
        var originalInstaller =
            snapshot.Installation is { } installation
            && installation.WorkUuid == requirement.ObservedWorkUuid
            && installation.InstalledRevision == requirement.InstalledContributionRevision
            && Same(installation.Ticket, requirement.ObservedTicket);
        var continuing =
            originalInstaller
            && observed != null
            && attempt.Retirement is { Ticket: not null, Receipt: null, Superseded: false } old
            && old.Owner == requirement.Owner
            && old.Contributor == requirement.Contributor
            && observed.Revision >= old.ExpectedContributionRevision + 1
            && !HasBindings(observed);
        var sameInstallation = originalInstaller && observed != null && HasBindings(observed);
        if (observed == null || observed.Revision < requirement.ContributionRevision)
        {
            throw new NormalizationConflictException(
                "The retained private subscription is missing from its original owner."
            );
        }
        // A higher revision alone can be harmless peer forwarding. Supersession needs
        // a different actual installing materializer run that owns the observed keys.
        var superseded = !continuing && !sameInstallation;
        if (
            superseded
            && (
                snapshot.Installation == null
                || observed.Revision <= requirement.ContributionRevision
            )
        )
        {
            throw new NormalizationConflictException(
                "The obsolete subscription has no authenticated continuation or newer installation."
            );
        }
        var retirement = new SetlistMaterializationRetirement(
            Guid.NewGuid(),
            requirement.Owner,
            requirement.Contributor,
            continuing || sameInstallation ? observed.Revision : requirement.ContributionRevision,
            attempt.AttemptUuid,
            context,
            PeerReadSet(context, requirement.Contributor)
        );
        attempt = await store.SaveRetirementAsync(attempt, retirement, ct);
        return superseded ? await store.MarkRetirementSupersededAsync(attempt, ct) : attempt;
    }

    private async Task<RetirementOwnerSnapshot> ReadRetirementOwnerAsync(
        SetlistMaterializationRegistration registration,
        SetlistContributionRetirementRequirement requirement,
        CancellationToken ct
    )
    {
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
        var context = await NormalizationMaterializationOwners.ReadAsync(
            c,
            t,
            requirement.Owner,
            ct
        );
        var observed = context.Membership.Contributions.SingleOrDefault(value =>
            value.Contributor == requirement.Contributor
        );
        // Forwarding an already-empty cleanup contribution also advances its revision. Its
        // original installer must still be current; the writer checks the real cleanup chain.
        var installation =
            observed == null
                ? null
                : await ReadInstallingAdmissionAsync(
                    c,
                    t,
                    registration,
                    requirement.Owner,
                    HasBindings(observed) ? observed : requirement.ObservedContribution,
                    ct
                );
        await t.CommitAsync(ct);
        return new(context, installation);
    }

    private static bool HasBindings(NormalizationInputContributionState state) =>
        state.Resources.Count > 0
        || state.DependencySubjectUuids.Count > 0
        || state.CatalogArtistUuids.Count > 0
        || state.SourceIdentifiers.Count > 0
        || state.UsesMappingRegistry
        || (state.SourceUuids?.Count ?? 0) > 0
        || (state.ImportedVenueUuids?.Count ?? 0) > 0
        || (state.CandidateUniverses?.Count ?? 0) > 0
        || (state.OwnerObservationSubjectUuids?.Count ?? 0) > 0;

    private sealed record RetirementOwnerSnapshot(
        MusicMaterializationOwnerContext Context,
        InstallingAdmission? Installation
    );

    private sealed record InstallingAdmissionMetadata(Guid WorkUuid, long Bytes);

    private sealed record InstallingAdmissionRow(
        string? ReadSet,
        string? References,
        string Ticket,
        long InstalledRevision
    );

    internal sealed record InstallingAdmission(
        Guid WorkUuid,
        NormalizationWorkTicket Ticket,
        long InstalledRevision
    );
}
