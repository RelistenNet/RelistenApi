using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedVenueService
{
    /// <summary>
    /// Creates one reviewed physical identity and accepted metadata atomically. The owner must
    /// already exist and own every cited input resource. Replaying the operation UUID returns
    /// its original receipt; a different operation cannot reuse an existing identity UUID.
    /// No imported-venue or show assignment is created by this identity-owner decision.
    /// </summary>
    public Task<NormalizationReviewResult> CreateReviewedVenueAsync(
        VenueCreationDecision command,
        string trustedOperator,
        CancellationToken cancellationToken = default
    )
    {
        command = command with
        {
            Names = command.Names.ToArray(),
            Locations = command.Locations.ToArray(),
            ExternalIdentities = command.ExternalIdentities.ToArray(),
        };
        ValidateCreation(command);
        return work.ApplyReviewAsync(
            new(
                command.OperationUuid,
                command.SubjectUuid,
                command.ExpectedEditorialRevision,
                "accept",
                JsonSerializer.SerializeToElement(command),
                command.Reason,
                trustedOperator
            ),
            async (c, t, ct) =>
            {
                await NormalizedPeopleService.ValidateOwnerAsync(
                    c,
                    t,
                    command.SubjectUuid,
                    "venues",
                    "venue",
                    command.VenueUuid,
                    ct
                );
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "SELECT pg_advisory_xact_lock_shared(hashtextextended('normalization:venues:identity',0))",
                        transaction: t,
                        cancellationToken: ct
                    )
                );
                await ValidateCreationEvidence(c, t, command, ct);
                await RequireCreationParent(c, t, command.Parent, ct);
                await InsertReviewedIdentity(c, t, command, ct);
                await CreateReviewedAssertions(c, t, command, ct);
            },
            cancellationToken,
            candidateWriteScopes: IdentityCandidateWriteScopes
        );
    }

    private static void ValidateCreation(VenueCreationDecision command)
    {
        if (
            command.VenueUuid == Guid.Empty
            || command.OperationUuid == Guid.Empty
            || command.SubjectUuid == Guid.Empty
        )
        {
            throw new ArgumentException(
                "Reviewed venue creation requires stable identity, owner and operation UUIDs."
            );
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DisplayName);
        if (
            command.Kind is not ("site" or "room")
            || command.Parent != null && command.Kind != "room"
        )
        {
            throw new ArgumentException(
                "Reviewed venue creation requires an explicit site or room kind; only a room may have a parent."
            );
        }
        if (
            command.Parent is { } parent
            && (
                parent.VenueUuid == Guid.Empty
                || parent.VenueUuid == command.VenueUuid
                || parent.ExpectedRevision < 1
            )
        )
        {
            throw new ArgumentException(
                "A room parent requires a different stable site UUID and its reviewed revision."
            );
        }
        if (
            command.Names.Count > 1000
            || command.Locations.Count > 1000
            || command.ExternalIdentities.Count > 1000
        )
        {
            throw new ArgumentException(
                "Reviewed venue creation exceeds its metadata assertion limit."
            );
        }
        if (
            !command.Names.Any(name =>
                NormalizedPeopleService.NameKey(name.Wording)
                == NormalizedPeopleService.NameKey(command.DisplayName)
            )
        )
        {
            throw new ArgumentException(
                "Reviewed creation requires an evidenced display-name assertion."
            );
        }
        var assertions = command
            .Names.Select(name => (AssertionUuid: name.Uuid, VenueUuid: name.VenueUuid))
            .Concat(
                command.Locations.Select(location =>
                    (AssertionUuid: location.Uuid, VenueUuid: location.VenueUuid)
                )
            )
            .Concat(
                command.ExternalIdentities.Select(identity =>
                    (AssertionUuid: identity.Uuid ?? Guid.Empty, VenueUuid: identity.VenueUuid)
                )
            )
            .ToArray();
        if (
            assertions.Any(assertion =>
                assertion.AssertionUuid == Guid.Empty || assertion.VenueUuid != command.VenueUuid
            )
            || assertions.Select(assertion => assertion.AssertionUuid).Distinct().Count()
                != assertions.Length
        )
        {
            throw new ArgumentException(
                "Creation assertions require distinct stable UUIDs and this venue's ownership."
            );
        }
    }

    private static async Task ValidateCreationEvidence(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        VenueCreationDecision command,
        CancellationToken ct
    )
    {
        var evidence = command
            .Names.Select(name => name.Evidence)
            .Concat(command.Locations.Select(location => location.Evidence))
            .Concat(command.ExternalIdentities.Select(identity => identity.Evidence))
            .Prepend(command.IdentityEvidence)
            .ToArray();
        await NormalizedPeopleService.ValidateEvidenceAsync(c, t, evidence, ct);
        await NormalizedPeopleService.ValidateMetadataReviewInputs(
            c,
            t,
            command.SubjectUuid,
            "accept",
            evidence,
            ct
        );
        await NormalizedPeopleService.ValidateNameEvidenceAsync(
            c,
            t,
            command.DisplayName,
            command.IdentityEvidence,
            ct
        );
    }

    private static async Task RequireCreationParent(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        VenueCreationParent? parent,
        CancellationToken ct
    )
    {
        if (parent == null)
        {
            return;
        }
        // ApplyReview holds the global venue write scope before the owner lock. Every typed
        // parent correction shares that scope, so this check cannot race a merge or kind edit.
        var valid = await c.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT EXISTS(SELECT 1 FROM normalization.canonical_venues v
                    JOIN normalization.work_subjects owner ON owner.domain='venues' AND owner.subject_kind='venue'
                        AND owner.subject_uuid=v.uuid
                    WHERE v.uuid=@VenueUuid AND v.kind='site' AND v.revision=@ExpectedRevision
                        AND NOT EXISTS(SELECT 1 FROM normalization.venue_redirects r WHERE r.retired_uuid=v.uuid))
                """,
                parent,
                t,
                cancellationToken: ct
            )
        );
        if (!valid)
        {
            throw new NormalizationConflictException(
                "The reviewed parent is missing, redirected or changed; review its current terminal site."
            );
        }
    }

    private static async Task InsertReviewedIdentity(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        VenueCreationDecision command,
        CancellationToken ct
    )
    {
        var inserted = await c.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO normalization.canonical_venues(uuid,display_name,name_key,kind,parent_venue_uuid,metadata_reviewed)
                VALUES(@VenueUuid,@DisplayName,@key,@Kind,@parent,true) ON CONFLICT(uuid) DO NOTHING
                """,
                new
                {
                    command.VenueUuid,
                    command.DisplayName,
                    key = NormalizedPeopleService.NameKey(command.DisplayName),
                    command.Kind,
                    parent = command.Parent?.VenueUuid,
                },
                t,
                cancellationToken: ct
            )
        );
        if (inserted != 1)
        {
            throw new NormalizationConflictException(
                "Reviewed venue creation cannot reuse an existing physical identity UUID."
            );
        }
    }

    private static async Task CreateReviewedAssertions(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        VenueCreationDecision command,
        CancellationToken ct
    )
    {
        var metadata = new VenueMetadataDecision(
            command.OperationUuid,
            command.SubjectUuid,
            command.ExpectedEditorialRevision,
            command.VenueUuid,
            1,
            "accept",
            command.Reason
        );
        foreach (var name in command.Names)
        {
            await ReviewVenueName(c, t, metadata, name, ct);
        }
        foreach (var location in command.Locations)
        {
            await ReviewVenueLocation(c, t, metadata, location, ct);
        }
        foreach (var identity in command.ExternalIdentities)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(identity.Provider);
            ArgumentException.ThrowIfNullOrWhiteSpace(identity.ResourceType);
            ArgumentException.ThrowIfNullOrWhiteSpace(identity.ProviderKey);
            await ReviewVenueExternalIdentity(c, t, metadata, identity, ct);
        }
    }
}
