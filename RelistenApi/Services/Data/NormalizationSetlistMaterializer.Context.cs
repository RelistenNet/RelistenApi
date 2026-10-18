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
    private async Task<SetlistMaterializationPreparation> ReadPreparationAsync(
        SetlistMaterializationRegistration registration,
        Guid repertoireAttemptUuid,
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

        MusicMaterializationOwnerContext? capacityContext = null;
        try
        {
            var consumption = await repertoire.ReadCompletedConsumptionAsync(
                c,
                t,
                repertoireAttemptUuid,
                ct
            );
            var batch =
                consumption.Setlist
                ?? throw new UnsupportedSetlistSourceException(
                    "Setlist materialization requires a completed setlist repertoire account."
                );
            var policy = await RepertoireConsumerPolicyService.ReadStateAsync(c, t, ct);
            var sources = batch
                .Extractions.SelectMany(input =>
                    input.Accounts.Select(
                        (account, index) =>
                            new SetlistStructureSource(
                                batch.AttemptUuid,
                                input.InputRevisionUuid,
                                index,
                                account
                            )
                    )
                )
                .ToArray();
            if (
                sources.Length is 0 or > 100
                || sources.Sum(source => source.Account.Account.Sets.Count) > 10000
                || sources.Sum(source => source.Account.Passages.Count) > 10000
            )
            {
                throw new MusicMaterializationCapacityException(
                    "The complete setlist source exceeds the supported account or claim limits."
                );
            }
            var identities = sources
                .Select(source => source.Account.Account.Origin.Item)
                .Distinct()
                .ToArray();
            if (identities.Length != 1 || identities[0] == null)
            {
                throw new UnsupportedSetlistSourceException(
                    "The source does not identify one qualified setlist account."
                );
            }
            var identity = identities[0]!;
            var artist = consumption.Source.ArtistUuid;
            var edition = artist is { } artistUuid
                ? await ReadCanonicalEditionAsync(c, t, artistUuid, identity, ct)
                : null;
            var editionUuid = edition?.Value.Uuid ?? QualifiedUuid(artist ?? Guid.Empty, identity);
            var owner =
                artist == null
                    ? ObservationOwner(registration.ProcessingRegistrationUuid)
                    : new NormalizationSubjectKey("songs", "setlist", editionUuid);
            var ownerContext = await NormalizationMaterializationOwners.ReadAsync(c, t, owner, ct);
            capacityContext = ownerContext;
            var structure =
                edition == null ? null : await ReadCurrentStructureAsync(c, t, editionUuid, ct);
            var ownRead = ConsumptionReadSet(consumption, policy);
            var read = RepertoireResolutionReadSets.Merge(
                ownRead,
                PeerReadSet(ownerContext, ContributionKey(registration.RegistrationUuid))
            );
            var references = consumption
                .InputReferences.Concat(ownerContext.InputReferences)
                .Distinct()
                .ToArray();
            SetlistMaterializationSetState[] sets =
                edition == null ? [] : await ReadSetsAsync(c, t, editionUuid, ct);
            MusicRecord<SongOccurrenceChange>[] occurrences =
                edition == null ? [] : await ReadOccurrencesAsync(c, t, editionUuid, ct);
            var (authorizations, correspondences) =
                edition == null
                    ? (
                        Array.Empty<SetlistAuthorizationRecord>(),
                        Array.Empty<SetlistMaterializationCorrespondence>()
                    )
                    : await ReadCorrespondenceHistoryAsync(c, t, editionUuid, ct);
            var sourceVersions = await ReadSourceVersionsAsync(
                c,
                t,
                sources,
                structure?.Value.Sources ?? [],
                ct
            );
            var obsolete = await ReadContributionsToRetireAsync(c, t, registration, owner, ct);
            var issues = consumption.Source.Issues.ToList();
            if (edition != null && structure == null)
            {
                issues.Add("unmanaged_accepted_edition");
            }
            if (edition?.Value.Retracted == true)
            {
                issues.Add("canonical_edition_retracted");
            }
            if (sources.Length > 1)
            {
                // Every observation remains visible, but an automatic full image cannot choose
                // which competing sequence is authoritative or merge source occurrences by position.
                issues.Add("multiple_account_observations");
            }
            var context = new SetlistMaterializationContext
            {
                Consumption = consumption,
                EditionUuid = editionUuid,
                StructureUuid = structure?.Value.Uuid ?? PurposeUuid(editionUuid, "structure"),
                Owner = owner,
                OwnerContext = ownerContext,
                ReadSet = read,
                OwnReadSet = ownRead,
                InputReferences = references,
                Policy = policy,
                Edition = edition,
                Structure = structure,
                Sets = sets,
                Occurrences = occurrences,
                Sources = sources,
                SourceVersions = sourceVersions,
                Authorizations = authorizations,
                Correspondences = correspondences,
                Issues = issues.Distinct().ToArray(),
                ContributionsToRetire = obsolete,
            };
            // Exclusion identity is the exact full proposal hash used by Apply. Read it before
            // semantic no-op detection so an empty acknowledgment cannot bypass a reviewed rejection.
            var proposed = Decide(context);
            var exclusions = await ReadReviewedClaimExclusionsAsync(c, t, proposed, ct);
            var prepared = Decide(
                context with
                {
                    ReviewedClaimExclusions = exclusions,
                },
                proposed.Decisions
            );
            await t.CommitAsync(ct);
            return prepared;
        }
        catch (MusicMaterializationCapacityException error)
            when (error.Owner == null && capacityContext != null)
        {
            throw WithCapacityOwner(error, capacityContext.Membership);
        }
    }

    internal static async Task<string[]> ReadReviewedClaimExclusionsAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistMaterializationPreparation proposed,
        CancellationToken ct
    )
    {
        if (
            proposed.Decisions.Any(value =>
                value is SetlistAccountMaterializationDecision
                || value.Disposition
                    is MusicMaterializationDecisionDisposition.Review
                        or MusicMaterializationDecisionDisposition.Unresolved
            )
        )
        {
            return [];
        }
        var image = proposed.Image ?? BuildImage(proposed.Context!, proposed.Decisions);
        MeasureBoundedJson(image, MaximumImageBytes);
        var hashes = NormalizedMusicService.SetlistMaterializationClaimHashes(image);
        return (
            await c.QueryAsync<string>(
                new CommandDefinition(
                    "SELECT DISTINCT claim_hash FROM normalization.music_exclusions WHERE claim_hash=ANY(@hashes) ORDER BY claim_hash",
                    new { hashes },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
    }

    internal static RepertoireResolutionReadSet ConsumptionReadSet(
        CompletedRepertoireConsumption consumption,
        MusicMaterializationPolicyState policy
    ) =>
        RepertoireResolutionReadSets.Merge(
            consumption.ProducerReadSet,
            new(
                [],
                [
                    consumption.AcknowledgmentPublication,
                    .. consumption.AcceptedReadSet.Dependencies,
                    new(
                        policy.OwnerObservation.SubjectUuid,
                        policy.OwnerObservation.DesiredGeneration,
                        policy.OwnerObservation.EditorialRevision,
                        policy.OwnerObservation.PublishedWorkUuid
                    ),
                ],
                [],
                [],
                null,
                [],
                [],
                [],
                []
            )
        );

    internal static RepertoireResolutionReadSet PeerReadSet(
        MusicMaterializationOwnerContext owner,
        NormalizationResourceKey own
    ) =>
        RepertoireResolutionReadSets.Merge(
            owner
                .Membership.Contributions.Where(state => state.Contributor != own)
                .Concat(owner.Membership.UnmanagedBaseline is { } baseline ? [baseline] : [])
                .Select(state =>
                    RepertoireResolutionReadSets.ForContribution(
                        owner.Bindings,
                        state,
                        owner.InputReferences
                    )
                )
                .ToArray()
        );

    private static async Task<MusicRecord<SetlistEditionChange>?> ReadCanonicalEditionAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid artist,
        NormalizationResourceKey identity,
        CancellationToken ct
    )
    {
        var row = await c.QuerySingleOrDefaultAsync<AcceptedImageRow>(
            new CommandDefinition(
                """
                SELECT r.payload::text AS "Payload",r.revision AS "Revision",r.created_at AS "UpdatedAt",r.decision_uuid AS "DecisionUuid",e.selected AS "Selected"
                FROM normalization.setlist_editions e JOIN normalization.music_revisions r ON r.uuid=e.revision_uuid
                WHERE e.artist_uuid=@artist AND e.provider=@Provider AND e.resource_type=@ResourceType AND e.provider_key=@ProviderKey
                """,
                new
                {
                    artist,
                    identity.Provider,
                    identity.ResourceType,
                    identity.ProviderKey,
                },
                t,
                cancellationToken: ct
            )
        );
        if (row == null)
        {
            return null;
        }
        return new(
            JsonSerializer.Deserialize<SetlistEditionChange>(row.Payload)! with
            {
                Selected = row.Selected,
            },
            row.Revision,
            row.UpdatedAt,
            row.DecisionUuid
        );
    }

    private static async Task<MusicRecord<SetlistStructureChange>?> ReadCurrentStructureAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid edition,
        CancellationToken ct
    )
    {
        var bytes = await c.ExecuteScalarAsync<long?>(
            new CommandDefinition(
                "SELECT octet_length(r.payload::text)::bigint FROM normalization.setlist_structures s JOIN normalization.music_revisions r ON r.uuid=s.revision_uuid WHERE s.edition_uuid=@edition",
                new { edition },
                t,
                cancellationToken: ct
            )
        );
        if (bytes == null)
        {
            return null;
        }
        if (bytes > MaximumImageBytes)
        {
            throw new MusicMaterializationCapacityException(
                "The accepted setlist image exceeds 16 MiB."
            );
        }
        var row = await c.QuerySingleAsync<AcceptedImageRow>(
            new CommandDefinition(
                """
                SELECT r.payload::text AS "Payload",r.revision AS "Revision",r.created_at AS "UpdatedAt",r.decision_uuid AS "DecisionUuid",e.selected AS "Selected"
                FROM normalization.setlist_structures s JOIN normalization.music_revisions r ON r.uuid=s.revision_uuid
                JOIN normalization.setlist_editions e ON e.uuid=s.edition_uuid WHERE s.edition_uuid=@edition
                """,
                new { edition },
                t,
                cancellationToken: ct
            )
        );
        var value = JsonSerializer.Deserialize<SetlistStructureChange>(row.Payload)!;
        return new(
            value with
            {
                Edition = value.Edition with { Selected = row.Selected },
            },
            row.Revision,
            row.UpdatedAt,
            row.DecisionUuid
        );
    }

    private static async Task<SetlistMaterializationSetState[]> ReadSetsAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid edition,
        CancellationToken ct
    )
    {
        await RequireRowBoundAsync(c, t, "setlist_sets", edition, 10000, ct);
        var rows = await c.QueryAsync<AcceptedSetRow>(
            new CommandDefinition(
                """
                WITH versions AS MATERIALIZED (
                    SELECT DISTINCT revision_uuid FROM normalization.setlist_sets WHERE edition_uuid=@edition)
                SELECT entry::text AS "Payload",r.revision AS "Revision"
                FROM versions version JOIN normalization.music_revisions r ON r.uuid=version.revision_uuid
                CROSS JOIN LATERAL jsonb_array_elements(r.payload->'Sets') entry
                JOIN normalization.setlist_sets s ON s.uuid=(entry->>'Uuid')::uuid AND s.revision_uuid=r.uuid
                WHERE s.edition_uuid=@edition ORDER BY s.uuid
                """,
                new { edition },
                t,
                cancellationToken: ct
            )
        );
        return rows.Select(row => new SetlistMaterializationSetState(
                JsonSerializer.Deserialize<SetlistStructureSet>(row.Payload)!,
                row.Revision
            ))
            .ToArray();
    }

    private static async Task<MusicRecord<SongOccurrenceChange>[]> ReadOccurrencesAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid edition,
        CancellationToken ct
    )
    {
        await RequireRowBoundAsync(c, t, "song_occurrences", edition, 10000, ct);
        var rows = await c.QueryAsync<AcceptedImageRow>(
            new CommandDefinition(
                """
                SELECT r.payload::text AS "Payload",r.revision AS "Revision",r.created_at AS "UpdatedAt",r.decision_uuid AS "DecisionUuid",false AS "Selected"
                FROM normalization.song_occurrences occurrence JOIN normalization.music_revisions r ON r.uuid=occurrence.revision_uuid
                WHERE occurrence.edition_uuid=@edition ORDER BY occurrence.uuid
                """,
                new { edition },
                t,
                cancellationToken: ct
            )
        );
        return rows.Select(row => new MusicRecord<SongOccurrenceChange>(
                JsonSerializer.Deserialize<SongOccurrenceChange>(row.Payload)!,
                row.Revision,
                row.UpdatedAt,
                row.DecisionUuid
            ))
            .ToArray();
    }

    private static async Task RequireRowBoundAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        string table,
        Guid edition,
        int maximum,
        CancellationToken ct
    )
    {
        var bound = await c.QuerySingleAsync<ImageBound>(
            new CommandDefinition(
                $"SELECT (SELECT count(*) FROM normalization.{table} WHERE edition_uuid=@edition)::integer AS \"Count\",(SELECT COALESCE(sum(octet_length(payload::text)),0)::bigint FROM normalization.music_revisions WHERE uuid IN (SELECT revision_uuid FROM normalization.{table} WHERE edition_uuid=@edition)) AS \"Bytes\"",
                new { edition },
                t,
                cancellationToken: ct
            )
        );
        if (bound.Count > maximum || bound.Bytes > 2L * MaximumImageBytes)
        {
            throw new MusicMaterializationCapacityException(
                "The complete accepted setlist target context exceeds its row or byte limit."
            );
        }
    }

    private static async Task<(
        SetlistAuthorizationRecord[],
        SetlistMaterializationCorrespondence[]
    )> ReadCorrespondenceHistoryAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid edition,
        CancellationToken ct
    )
    {
        var bound = await c.QuerySingleAsync<ImageBound>(
            new CommandDefinition(
                """
                SELECT ((SELECT count(*) FROM normalization.setlist_structure_correspondences WHERE edition_uuid=@edition)
                    +(SELECT count(*) FROM normalization.setlist_authorization_heads WHERE edition_uuid=@edition))::integer AS "Count",
                    ((SELECT COALESCE(sum(octet_length(payload::text)+octet_length(source_tickets::text)),0)::bigint
                      FROM normalization.setlist_authorization_events a JOIN normalization.setlist_authorization_heads h ON h.authorization_uuid=a.uuid WHERE a.edition_uuid=@edition)
                    +(SELECT COALESCE(sum(octet_length(source_key)+COALESCE(octet_length(provider_identity::text),0)),0)::bigint
                      FROM normalization.setlist_structure_correspondences WHERE edition_uuid=@edition)) AS "Bytes"
                """,
                new { edition },
                t,
                cancellationToken: ct
            )
        );
        if (bound.Count > 100000 || bound.Bytes > 2L * MaximumImageBytes)
        {
            throw new MusicMaterializationCapacityException(
                "The complete setlist correspondence authority exceeds its supported history limit."
            );
        }
        var authorizations = await c.QueryAsync<AuthorizationImageRow>(
            new CommandDefinition(
                """
                SELECT a.uuid AS "Uuid",a.operation_uuid AS "OperationUuid",a.processor_decision_uuid AS "ProcessorDecisionUuid",a.kind AS "Kind",a.action AS "Action",
                    a.payload::text AS "Payload",a.source_tickets::text AS "SourceTickets",a.created_at AS "CreatedAt"
                FROM normalization.setlist_authorization_heads h JOIN normalization.setlist_authorization_events a ON a.uuid=h.authorization_uuid
                WHERE a.edition_uuid=@edition ORDER BY a.uuid
                """,
                new { edition },
                t,
                cancellationToken: ct
            )
        );
        var authority = authorizations
            .Select(row => new SetlistAuthorizationRecord(
                row.Uuid,
                edition,
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
                true,
                row.CreatedAt,
                row.ProcessorDecisionUuid
            ))
            .ToArray();
        var links = await c.QueryAsync<CorrespondenceImageRow>(
            new CommandDefinition(
                """
                SELECT r.revision AS "Revision",link.source_attempt_uuid AS "AttemptUuid",p.processing_version AS "Version",link.input_revision_uuid AS "InputUuid",
                    link.account_index AS "AccountIndex",link.kind AS "Kind",link.source_key AS "SourceKey",link.target_uuid AS "TargetUuid",
                    link.provider_identity::text AS "Identity",link.authorization_uuid AS "AuthorizationUuid"
                FROM normalization.setlist_structure_correspondences link JOIN normalization.music_revisions r ON r.uuid=link.revision_uuid
                JOIN normalization.setlist_processing_attempts p ON p.attempt_uuid=link.source_attempt_uuid WHERE link.edition_uuid=@edition
                ORDER BY r.revision,link.source_attempt_uuid,link.input_revision_uuid,link.account_index,link.kind,link.source_key,link.target_uuid
                """,
                new { edition },
                t,
                cancellationToken: ct
            )
        );
        return (
            authority,
            links
                .Select(row => new SetlistMaterializationCorrespondence(
                    row.Revision,
                    row.AttemptUuid,
                    row.Version,
                    row.InputUuid,
                    row.AccountIndex,
                    row.Kind == "set"
                        ? new(
                            SetIndex: int.Parse(
                                row.SourceKey,
                                System.Globalization.CultureInfo.InvariantCulture
                            )
                        )
                        : new(ProposalUuid: Guid.Parse(row.SourceKey)),
                    row.TargetUuid,
                    row.Identity == null
                        ? null
                        : JsonSerializer.Deserialize<NormalizationResourceKey>(row.Identity),
                    row.AuthorizationUuid
                ))
                .ToArray()
        );
    }

    private static async Task<SetlistMaterializationSourceVersion[]> ReadSourceVersionsAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        IReadOnlyList<SetlistStructureSource> current,
        IReadOnlyList<SetlistStructureSource> previous,
        CancellationToken ct
    )
    {
        var ids = current
            .Concat(previous)
            .Select(value => value.ProposalAttemptUuid)
            .Distinct()
            .ToArray();
        var versions = (
            await c.QueryAsync<SetlistMaterializationSourceVersion>(
                new CommandDefinition(
                    "SELECT attempt_uuid AS \"ProcessingAttemptUuid\",processing_version AS \"ProcessingVersion\" FROM normalization.setlist_processing_attempts WHERE attempt_uuid=ANY(@ids) ORDER BY attempt_uuid",
                    new { ids },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (versions.Length != ids.Length)
        {
            throw new NormalizationConflictException(
                "An accepted setlist source lacks its immutable producer version."
            );
        }
        return versions;
    }

    internal static async Task RequireAcceptedContextAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistMaterializationContext context,
        SetlistMaterializationRegistration registration,
        CancellationToken ct
    )
    {
        var batch =
            context.Consumption.Setlist
            ?? throw new NormalizationConflictException(
                "The saved context has no setlist producer."
            );
        var sources = batch
            .Extractions.SelectMany(input =>
                input.Accounts.Select(
                    (account, index) =>
                        new SetlistStructureSource(
                            batch.AttemptUuid,
                            input.InputRevisionUuid,
                            index,
                            account
                        )
                )
            )
            .ToArray();
        if (!Same(context.Sources, sources))
        {
            throw new NormalizationConflictException(
                "The consumer source accounts differ from the exact completed producer batch."
            );
        }
        var obsolete = await ReadContributionsToRetireAsync(c, t, registration, context.Owner, ct);
        if (
            !Same(obsolete, context.ContributionsToRetire)
            || !Same(context.OwnReadSet, ConsumptionReadSet(context.Consumption, context.Policy))
        )
        {
            throw new NormalizationConflictException(
                "The saved private subscription ownership proof changed."
            );
        }
        var versions = await ReadSourceVersionsAsync(
            c,
            t,
            sources,
            context.Structure?.Value.Sources ?? [],
            ct
        );
        if (!Same(versions, context.SourceVersions))
        {
            throw new NormalizationConflictException(
                "The saved setlist source versions differ from their immutable receipts."
            );
        }
        if (context.Consumption.Source.ArtistUuid is not { } artist)
        {
            return;
        }
        var identity = sources
            .Select(value => value.Account.Account.Origin.Item)
            .Distinct()
            .Single()!;
        var edition = await ReadCanonicalEditionAsync(c, t, artist, identity, ct);
        if (edition?.Value.Uuid != context.Edition?.Value.Uuid)
        {
            throw new SetlistMaterializationTargetChangedException();
        }
        if (context.EditionUuid != (edition?.Value.Uuid ?? QualifiedUuid(artist, identity)))
        {
            throw new NormalizationConflictException(
                "The consumer cannot choose another edition for this qualified account."
            );
        }
        var structure =
            edition == null ? null : await ReadCurrentStructureAsync(c, t, context.EditionUuid, ct);
        SetlistMaterializationSetState[] sets =
            edition == null ? [] : await ReadSetsAsync(c, t, context.EditionUuid, ct);
        MusicRecord<SongOccurrenceChange>[] occurrences =
            edition == null ? [] : await ReadOccurrencesAsync(c, t, context.EditionUuid, ct);
        var history =
            edition == null
                ? (
                    Array.Empty<SetlistAuthorizationRecord>(),
                    Array.Empty<SetlistMaterializationCorrespondence>()
                )
                : await ReadCorrespondenceHistoryAsync(c, t, context.EditionUuid, ct);
        if (
            !Same(edition, context.Edition)
            || !Same(structure, context.Structure)
            || !Same(sets, context.Sets)
            || !Same(occurrences, context.Occurrences)
            || !Same(history.Item1, context.Authorizations)
            || !Same(history.Item2, context.Correspondences)
            || context.StructureUuid
                != (structure?.Value.Uuid ?? PurposeUuid(context.EditionUuid, "structure"))
        )
        {
            throw new NormalizationConflictException(
                "The complete accepted setlist image or its current authority changed."
            );
        }
    }

    private sealed record AcceptedImageRow(
        string Payload,
        long Revision,
        DateTime UpdatedAt,
        Guid DecisionUuid,
        bool Selected
    );

    private sealed record ImageBound(int Count, long Bytes);

    private sealed record AcceptedSetRow(string Payload, long Revision);

    private sealed record AuthorizationImageRow(
        Guid Uuid,
        Guid? OperationUuid,
        Guid? ProcessorDecisionUuid,
        string Kind,
        string Action,
        string Payload,
        string SourceTickets,
        DateTime CreatedAt
    );

    private sealed record CorrespondenceImageRow(
        long Revision,
        Guid AttemptUuid,
        string Version,
        Guid InputUuid,
        int AccountIndex,
        string Kind,
        string SourceKey,
        Guid TargetUuid,
        string? Identity,
        Guid? AuthorizationUuid
    );
}

internal sealed class SetlistMaterializationTargetChangedException : Exception;
