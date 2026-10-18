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
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRepertoireResolutionStore
{
    /// <summary>
    /// Authenticates a completed account inside the consumer's primary RR snapshot. This reader
    /// neither refreshes producer clocks nor enrolls an owner. Accepted repertoire citations may
    /// be historical; only their current accepted revision and owner supply target authority.
    /// </summary>
    internal Task<CompletedRepertoireConsumption> ReadCompletedConsumptionAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid repertoireAttemptUuid,
        CancellationToken ct
    )
    {
        if (t.Connection != c || t.IsolationLevel != IsolationLevel.RepeatableRead)
        {
            throw new ArgumentException(
                "Completed consumption requires the caller's repeatable-read transaction."
            );
        }
        return ReadCompletedConsumption(c, t, repertoireAttemptUuid, ct);
    }

    /// <summary>
    /// Reauthenticates the prepared account under the publication transaction's existing causal
    /// owner and producer input locks. No snapshot is replaced or returned to the writer.
    /// </summary>
    internal async Task RequireCompletedConsumptionAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        CompletedRepertoireConsumption expected,
        CancellationToken ct
    )
    {
        if (t.Connection != c)
        {
            throw new ArgumentException(
                "Completed consumption must use the publication transaction."
            );
        }
        var current = await ReadCompletedConsumption(c, t, expected.Attempt.AttemptUuid, ct);
        RequireSame(
            expected,
            current,
            "The prepared completed repertoire account changed before publication."
        );
    }

    private static async Task<CompletedRepertoireConsumption> ReadCompletedConsumption(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid repertoireAttemptUuid,
        CancellationToken ct
    )
    {
        var budget = new ConsumptionBudget();
        var (attempt, source) = await ReadConsumptionAccount(
            c,
            t,
            repertoireAttemptUuid,
            budget,
            ct
        );
        var acknowledgment = await ReadConsumptionAcknowledgment(c, t, attempt, source, budget, ct);
        var (recording, setlist) = await ReadConsumptionProducer(
            c,
            t,
            attempt.Request,
            source,
            budget,
            ct
        );
        var producer = source.ReadSet;
        await RequireConsumptionProducerCurrent(c, t, producer, ct);
        var inputs = await ReadConsumptionInputReferences(c, t, producer.Inputs, ct);
        var decisions = await ReadConsumptionDecisions(c, t, attempt, source, budget, ct);
        var targets = await ReadConsumptionTargets(c, t, source.ArtistUuid, decisions, budget, ct);
        var accepted = await ReadConsumptionAcceptedOwners(
            c,
            t,
            source.ArtistUuid,
            targets.Count > 0,
            ct
        );
        return new(
            attempt,
            source,
            decisions,
            acknowledgment.Ticket,
            acknowledgment.Receipt,
            new(
                acknowledgment.Ticket.SubjectUuid,
                acknowledgment.Ticket.DesiredGeneration,
                acknowledgment.Ticket.EditorialRevision,
                acknowledgment.Ticket.WorkUuid
            ),
            recording,
            setlist,
            producer,
            inputs,
            accepted,
            targets
        );
    }

    private static async Task<(
        CompletedRepertoireAttempt Attempt,
        RepertoireResolutionSourceSnapshot Source
    )> ReadConsumptionAccount(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid uuid,
        ConsumptionBudget budget,
        CancellationToken ct
    )
    {
        var bytes = await c.ExecuteScalarAsync<long?>(
            new CommandDefinition(
                """
                SELECT octet_length(request::text)+COALESCE(octet_length(result::text),0)
                    +octet_length(registration::text)+COALESCE(octet_length((preparation->'Source')::text),0)
                FROM normalization.repertoire_resolution_attempts WHERE attempt_uuid=@uuid
                """,
                new { uuid },
                t,
                cancellationToken: ct
            )
        );
        if (bytes == null)
        {
            throw new ArgumentException("Unknown repertoire consumption attempt.");
        }
        budget.Add(bytes.Value, MaximumPreparationBytes + (long)MaximumCheckpointBytes);
        var row = await c.QuerySingleAsync<ConsumptionAccountRow>(
            new CommandDefinition(
                """
                SELECT registration_uuid AS "RegistrationUuid",registration::text AS "Registration",request::text AS "Request",
                    processing_version AS "Version",result::text AS "Result",(preparation->'Source')::text AS "Source"
                FROM normalization.repertoire_resolution_attempts WHERE attempt_uuid=@uuid
                """,
                new { uuid },
                t,
                cancellationToken: ct
            )
        );
        var current = await ReadRegistration(c, t, row.RegistrationUuid, ct);
        var saved = JsonSerializer.Deserialize<RepertoireResolutionRegistration>(row.Registration)!;
        var result =
            row.Result == null
                ? null
                : JsonSerializer.Deserialize<RepertoireResolutionResult>(row.Result);
        if (
            current == null
            || current.CurrentAttemptUuid != uuid
            || result?.AttemptUuid != uuid
            || result.Disposition != RepertoireResolutionDisposition.Completed
            || row.Source == null
            || row.Version != NormalizationRepertoireResolutionProcessor.Version
            || current.Lineage != saved.Lineage
            || current.ExpectedArtistUuid != saved.ExpectedArtistUuid
        )
        {
            throw new NormalizationConflictException(
                "Consumption requires the current compatible completed repertoire account."
            );
        }
        var source = JsonSerializer.Deserialize<RepertoireResolutionSourceSnapshot>(row.Source)!;
        var request = JsonSerializer.Deserialize<RepertoireResolutionRequest>(row.Request)!;
        if (
            source.ClaimsOmitted
            || source.Claims.Count > NormalizationImportedCatalogService.MaximumRepertoireSongs
            || source.Claims.Select(value => value.ClaimUuid).Distinct().Count()
                != source.Claims.Count
            || source.Claims.Select(value => value.CorrespondenceKey).Distinct().Count()
                != source.Claims.Count
            || source.SourceVersion != RepertoireResolutionPolicyEvaluator.SourceVersion
            || source.Lineage != current.Lineage
            || request.ExpectedArtistUuid != current.ExpectedArtistUuid
            || source.ArtistUuid is { } artist
                && request.ExpectedArtistUuid is { } expected
                && artist != expected
        )
        {
            throw new NormalizationConflictException(
                "The completed account has incompatible source coordinates or artist scope."
            );
        }
        return (new(uuid, current, request, row.Version, result), source);
    }

    private static async Task<(
        NormalizationWorkTicket Ticket,
        NormalizationPublicationResult Receipt
    )> ReadConsumptionAcknowledgment(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        CompletedRepertoireAttempt attempt,
        RepertoireResolutionSourceSnapshot source,
        ConsumptionBudget budget,
        CancellationToken ct
    )
    {
        var rows = (
            await c.QueryAsync<ConsumptionAckMetadata>(
                new CommandDefinition(
                    """
                    SELECT stage_uuid AS "StageUuid",(COALESCE(octet_length(ticket::text),0)+COALESCE(octet_length(receipt::text),0))::bigint AS "Bytes"
                    FROM normalization.repertoire_resolution_stages WHERE attempt_uuid=@uuid AND (stage->>'Kind')::integer=2
                    ORDER BY ordinal LIMIT 2
                    """,
                    new { uuid = attempt.AttemptUuid },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (rows.Length != 1)
        {
            throw new NormalizationConflictException(
                "The completed account has no unique acknowledgment."
            );
        }
        budget.Add(rows[0].Bytes, 2L * MaximumCheckpointBytes);
        var row = await c.QuerySingleAsync<ConsumptionAckRow>(
            new CommandDefinition(
                """
                SELECT s.work_uuid AS "WorkUuid",s.ticket::text AS "Ticket",s.receipt::text AS "Receipt",
                    (NOT s.superseded AND s.failure_reason IS NULL AND r.state='published' AND r.ticket=s.ticket
                        AND s.stage->>'WorkUuid'=s.work_uuid::text AND owner.domain='songs'
                        AND owner.subject_kind='repertoire_observation' AND owner.subject_uuid=@registration
                        AND owner.published_work_uuid=s.work_uuid
                        AND owner.desired_generation=(s.ticket->>'DesiredGeneration')::bigint
                        AND owner.editorial_revision=(s.ticket->>'EditorialRevision')::bigint
                        AND owner.published_generation=owner.desired_generation AND owner.policy_version=s.ticket->>'PolicyVersion'
                        AND r.subject_uuid=(s.ticket->>'SubjectUuid')::uuid
                        AND s.stage->'Owner'=jsonb_build_object('Domain',owner.domain,'Kind',owner.subject_kind,'Uuid',owner.subject_uuid)) AS "Current",
                    (r.state='published' AND r.ticket=s.ticket AND NOT s.superseded AND s.failure_reason IS NULL
                        AND owner.domain='songs' AND owner.subject_kind='repertoire_observation' AND owner.subject_uuid=@registration
                        AND owner.published_work_uuid=s.work_uuid AND owner.published_generation=(s.ticket->>'DesiredGeneration')::bigint
                        AND owner.desired_generation>(s.ticket->>'DesiredGeneration')::bigint
                        AND owner.editorial_revision=(s.ticket->>'EditorialRevision')::bigint) AS "Pending"
                FROM normalization.repertoire_resolution_stages s
                LEFT JOIN normalization.work_runs r ON r.uuid=s.work_uuid
                LEFT JOIN normalization.work_subjects owner ON owner.uuid=r.subject_uuid WHERE s.stage_uuid=@stageUuid
                """,
                new
                {
                    registration = attempt.Registration.RegistrationUuid,
                    stageUuid = rows[0].StageUuid,
                },
                t,
                cancellationToken: ct
            )
        );
        if (
            (row.Current != true && row.Pending != true)
            || row.Ticket == null
            || row.Receipt == null
        )
        {
            // A candidate watcher being pending is not revocation. A newer admitted owner
            // generation, however, cannot borrow this older publication's authority.
            throw new NormalizationConflictException(
                "The repertoire acknowledgment owner has no current published receipt."
            );
        }
        var ticket = JsonSerializer.Deserialize<NormalizationWorkTicket>(row.Ticket)!;
        var receipt = JsonSerializer.Deserialize<NormalizationPublicationResult>(row.Receipt)!;
        if (
            ticket.WorkUuid != row.WorkUuid
            || receipt.WorkUuid != row.WorkUuid
            || receipt.Status != NormalizationPublicationStatus.Published
            || ticket.PolicyVersion != attempt.ProcessingVersion
            || !ConsumptionContainsOriginal(ticket, source.ReadSet)
        )
        {
            throw new NormalizationConflictException(
                "The acknowledgment does not authenticate the original completed source."
            );
        }
        if (row.Pending == true)
        {
            throw new CompletedRepertoireConsumptionPendingException(
                attempt.AttemptUuid,
                ticket.SubjectUuid,
                "A newer repertoire acknowledgment is admitted but has not published."
            );
        }
        return (ticket, receipt);
    }

    private static async Task<(
        NormalizationRecordingProposalBatch? Recording,
        NormalizationSetlistProposalBatch? Setlist
    )> ReadConsumptionProducer(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        RepertoireResolutionRequest request,
        RepertoireResolutionSourceSnapshot source,
        ConsumptionBudget budget,
        CancellationToken ct
    )
    {
        if (request.Source is RecordingRepertoireResolutionSource recording)
        {
            var batch = await ReadConsumptionBatch<NormalizationRecordingProposalBatch>(
                c,
                t,
                "recording_processing_attempts",
                RecordingMusicConsumptionProjectionSql,
                recording.PublishedProcessingAttemptUuid,
                budget,
                ct
            );
            if (
                batch.AttemptUuid != recording.PublishedProcessingAttemptUuid
                || batch.Ticket.WorkUuid != batch.AttemptUuid
                || batch.ProcessingVersion != NormalizationRecordingProcessor.Version
                || batch.Ticket.PolicyVersion != batch.ProcessingVersion
                || !NormalizationRecordingReadSet.IsComplete(batch.Ticket, batch.Preparation)
                || source.ArtistUuid is { } sourceArtist && sourceArtist != batch.ArtistUuid
                || source.Lineage
                    != new NormalizationResourceKey(
                        "relisten",
                        "recording",
                        batch.SourceUuid.ToString("D")
                    )
            )
            {
                throw new NormalizationConflictException(
                    "The recording producer is incompatible with the completed source."
                );
            }
            await RequireConsumptionProducerReceipt(
                c,
                t,
                batch.Ticket,
                "recording",
                batch.SourceUuid,
                ct
            );
            RequireConsumptionProducerReadSet(source, batch.Ticket);
            RequireConsumptionRecordingCoordinates(source, batch);
            return (batch, null);
        }
        if (request.Source is SetlistRepertoireResolutionSource setlist)
        {
            var batch = await ReadConsumptionBatch<NormalizationSetlistProposalBatch>(
                c,
                t,
                "setlist_processing_attempts",
                "batch",
                setlist.PublishedProcessingAttemptUuid,
                budget,
                ct
            );
            if (
                batch.AttemptUuid != setlist.PublishedProcessingAttemptUuid
                || batch.Ticket.WorkUuid != batch.AttemptUuid
                || batch.ProcessingVersion != NormalizationSetlistProcessor.Version
                || batch.Ticket.PolicyVersion != batch.ProcessingVersion
                || !NormalizationSetlistReadSet.IsComplete(batch.Ticket, batch.Preparation)
                || source.Lineage
                    != new NormalizationResourceKey(
                        "relisten",
                        "setlist",
                        batch.RegistrationUuid.ToString("D")
                    )
            )
            {
                throw new NormalizationConflictException(
                    "The setlist producer is incompatible with the completed source."
                );
            }
            await RequireConsumptionProducerReceipt(
                c,
                t,
                batch.Ticket,
                "setlist",
                batch.RegistrationUuid,
                ct
            );
            RequireConsumptionProducerReadSet(source, batch.Ticket);
            RequireConsumptionSetlistCoordinates(source, batch);
            return (null, batch);
        }
        if (request.Source is ImportedRepertoireResolutionSource imported)
        {
            var valid = await c.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    """
                    SELECT EXISTS(SELECT 1 FROM normalization.input_fetches f
                        JOIN normalization.input_revisions r ON r.uuid=f.input_revision_uuid
                        JOIN normalization.input_resources p ON p.uuid=f.resource_uuid
                        WHERE f.fetch_uuid=@FetchUuid AND f.accepted_as_current AND p.provider='legacy_catalog'
                            AND p.resource_type='artist_repertoire' AND r.capture_version=@version
                            AND EXISTS(SELECT 1 FROM jsonb_to_recordset(@inputs::jsonb) i("ResourceUuid" uuid,"RevisionUuid" uuid,"Generation" bigint)
                                WHERE i."ResourceUuid"=p.uuid AND i."RevisionUuid"=r.uuid AND i."Generation"=f.resource_generation)
                            AND p.provider=@Provider AND p.resource_type=@ResourceType AND p.provider_key=@ProviderKey)
                    """,
                    new
                    {
                        imported.FetchUuid,
                        version = NormalizationImportedCatalogService.RepertoireCaptureVersion,
                        inputs = JsonSerializer.Serialize(source.ReadSet.Inputs),
                        source.Lineage.Provider,
                        source.Lineage.ResourceType,
                        source.Lineage.ProviderKey,
                    },
                    t,
                    cancellationToken: ct
                )
            );
            if (!valid)
            {
                throw new NormalizationConflictException(
                    "The completed dictionary has no exact original producer receipt."
                );
            }
            return (null, null);
        }
        if (
            request.Source is ProviderRepertoireResolutionSource provider
            && provider.Input.Resource == source.Lineage
            && provider.AccountIdentity == source.Lineage
            && source.ReadSet.Inputs.Any(value =>
                value.RevisionUuid == provider.Input.InputRevisionUuid
            )
        )
        {
            return (null, null);
        }
        throw new NormalizationConflictException(
            "The completed source kind or producer coordinates are unsupported."
        );
    }

    private static async Task<T> ReadConsumptionBatch<T>(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        string table,
        string projection,
        Guid uuid,
        ConsumptionBudget budget,
        CancellationToken ct
    )
    {
        // Table and projection are selected only by the two typed producer branches above.
        // Size and read exactly the same domain view; the immutable producer stays unchanged.
        var bytes = await c.ExecuteScalarAsync<long?>(
            new CommandDefinition(
                $"SELECT octet_length(({projection})::text) FROM normalization.{table} WHERE attempt_uuid=@uuid AND disposition='published'",
                new { uuid },
                t,
                cancellationToken: ct
            )
        );
        if (bytes == null)
        {
            throw new NormalizationConflictException("The exact producer batch was not published.");
        }
        budget.Add(bytes.Value, MaximumCheckpointBytes);
        var json = await c.QuerySingleAsync<string>(
            new CommandDefinition(
                $"SELECT ({projection})::text FROM normalization.{table} WHERE attempt_uuid=@uuid",
                new { uuid },
                t,
                cancellationToken: ct
            )
        );
        return JsonSerializer.Deserialize<T>(json)!;
    }

    private static async Task RequireConsumptionProducerReceipt(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationWorkTicket ticket,
        string kind,
        Guid entity,
        CancellationToken ct
    )
    {
        var valid = await c.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT EXISTS(SELECT 1 FROM normalization.work_runs r JOIN normalization.work_subjects s ON s.uuid=r.subject_uuid
                    WHERE r.uuid=@WorkUuid AND r.subject_uuid=@SubjectUuid AND r.state='published' AND r.ticket=@ticket::jsonb
                        AND s.domain='extractions' AND s.subject_kind=@kind AND s.subject_uuid=@entity
                        AND s.published_work_uuid=@WorkUuid AND s.desired_generation=@DesiredGeneration
                        AND s.editorial_revision=@EditorialRevision AND s.policy_version=@PolicyVersion)
                """,
                new
                {
                    ticket.WorkUuid,
                    ticket.SubjectUuid,
                    ticket.DesiredGeneration,
                    ticket.EditorialRevision,
                    ticket.PolicyVersion,
                    kind,
                    entity,
                    ticket = JsonSerializer.Serialize(ticket),
                },
                t,
                cancellationToken: ct
            )
        );
        if (!valid)
        {
            throw new NormalizationConflictException(
                "The original producer publication is no longer current."
            );
        }
    }

    private static void RequireConsumptionProducerReadSet(
        RepertoireResolutionSourceSnapshot source,
        NormalizationWorkTicket ticket
    )
    {
        var expected = new RepertoireResolutionReadSet(
            ticket.Inputs,
            ticket
                .Dependencies.Append(
                    new(
                        ticket.SubjectUuid,
                        ticket.DesiredGeneration,
                        ticket.EditorialRevision,
                        ticket.WorkUuid
                    )
                )
                .Distinct()
                .ToArray(),
            ticket.CatalogInputs ?? [],
            ticket.SourceIdentities ?? [],
            ticket.MappingRegistry,
            ticket.ImportedVenues ?? [],
            ticket.CandidateUniverses ?? [],
            ticket.OwnerObservations ?? [],
            ticket.SourceUuids ?? []
        );
        RequireSame(
            expected,
            source.ReadSet,
            "The source must retain its producer's exact complete original read set."
        );
    }

    private static bool ConsumptionContainsOriginal(
        NormalizationWorkTicket ticket,
        RepertoireResolutionReadSet read
    ) =>
        read.Inputs.All(ticket.Inputs.Contains)
        && read.Dependencies.All(ticket.Dependencies.Contains)
        && read.CatalogInputs.All((ticket.CatalogInputs ?? []).Contains)
        && read.SourceIdentities.All((ticket.SourceIdentities ?? []).Contains)
        && (read.MappingRegistry == null || read.MappingRegistry == ticket.MappingRegistry)
        && read.ImportedVenues.All((ticket.ImportedVenues ?? []).Contains)
        && read.CandidateUniverses.All((ticket.CandidateUniverses ?? []).Contains)
        && read.OwnerObservations.All((ticket.OwnerObservations ?? []).Contains)
        && read.SourceUuids.All((ticket.SourceUuids ?? []).Contains);

    private static void RequireConsumptionSetlistCoordinates(
        RepertoireResolutionSourceSnapshot source,
        NormalizationSetlistProposalBatch batch
    )
    {
        var expected = batch
            .Extractions.SelectMany(input =>
                input.Accounts.SelectMany(account =>
                    account.Passages.Select(proposal =>
                        (
                            Key: "setlist:" + proposal.ProposalUuid.ToString("D"),
                            Proposal: proposal.ProposalUuid,
                            Input: input.InputRevisionUuid,
                            Title: proposal.Passage.Title
                        )
                    )
                )
            )
            .ToDictionary(value => value.Key);
        if (
            expected.Count != source.Claims.Count
            || source.Claims.Any(claim =>
                !expected.TryGetValue(claim.CorrespondenceKey, out var value)
                || claim.ProposalUuid != value.Proposal
                || claim.Title != value.Title
                || claim.Origin.Evidence.InputRevisionUuid != value.Input
            )
        )
        {
            throw new NormalizationConflictException(
                "Setlist claims do not cover their exact published account passage coordinates."
            );
        }
    }

    private static void RequireConsumptionRecordingCoordinates(
        RepertoireResolutionSourceSnapshot source,
        NormalizationRecordingProposalBatch batch
    )
    {
        var expected =
            new Dictionary<
                string,
                (Guid Proposal, Guid Input, Guid Track, ObservedValue<string>? Title)
            >();
        var groups = new[]
        {
            new NormalizationRecordingInputExtraction(
                batch.Preparation.Binding.InputRevisionUuid,
                batch.Extraction
            ),
        }.Concat(batch.AdditionalExtractions ?? []);
        foreach (var input in groups)
        {
            foreach (var proposal in input.Extraction.Titles)
            {
                var key = "recording:" + proposal.ProposalUuid.ToString("D");
                if (
                    proposal.Extraction?.Segments is not { Count: > 0 } segments
                    || proposal.Title == null
                )
                {
                    expected.Add(
                        key,
                        (
                            proposal.ProposalUuid,
                            input.InputRevisionUuid,
                            proposal.TrackUuid,
                            proposal.Title
                        )
                    );
                    continue;
                }
                foreach (var segment in segments)
                {
                    var citation = new NormalizedEvidenceReference(
                        proposal.Title.Evidence.InputRevisionUuid,
                        proposal.Title.Evidence.JsonPointer,
                        (proposal.Title.Evidence.StartCodePoint ?? 0)
                            + segment.Evidence.StartCodePoint,
                        (proposal.Title.Evidence.StartCodePoint ?? 0)
                            + segment.Evidence.EndCodePoint,
                        segment.Evidence.Quote
                    );
                    expected.Add(
                        key + ":segment:" + segment.Position,
                        (
                            proposal.ProposalUuid,
                            input.InputRevisionUuid,
                            proposal.TrackUuid,
                            new(segment.Title ?? segment.Evidence.Quote, citation)
                        )
                    );
                }
            }
        }
        if (
            expected.Count != source.Claims.Count
            || source.Claims.Any(claim =>
                !expected.TryGetValue(claim.CorrespondenceKey, out var value)
                || claim.ProposalUuid != value.Proposal
                || claim.TrackUuid != value.Track
                || claim.Title != value.Title
                || claim.Origin.Evidence.InputRevisionUuid != value.Input
            )
        )
        {
            throw new NormalizationConflictException(
                "Recording claims do not cover their exact published title segment coordinates."
            );
        }
    }

    private static async Task<NormalizationBindingInputReference[]> ReadConsumptionInputReferences(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        IReadOnlyList<NormalizationInputDependency> inputs,
        CancellationToken ct
    )
    {
        if (inputs.Count > 100)
        {
            throw new CompletedRepertoireConsumptionCapacityException(
                "The complete producer input set exceeds 100 revisions."
            );
        }
        var ids = inputs.Select(value => value.RevisionUuid).ToArray();
        var rows = (
            await c.QueryAsync<ConsumptionInputRow>(
                new CommandDefinition(
                    """
                    SELECT r.uuid AS "Uuid",r.resource_uuid AS "ResourceUuid",p.provider AS "Provider",p.resource_type AS "Type",p.provider_key AS "Key"
                    FROM normalization.input_revisions r JOIN normalization.input_resources p ON p.uuid=r.resource_uuid WHERE r.uuid=ANY(@ids)
                    """,
                    new { ids },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToDictionary(value => value.Uuid);
        if (
            rows.Count != inputs.Count
            || inputs.Any(value =>
                !rows.TryGetValue(value.RevisionUuid, out var row)
                || row.ResourceUuid != value.ResourceUuid
            )
        )
        {
            throw new NormalizationConflictException(
                "A producer input has no exact retained resource identity."
            );
        }
        return inputs
            .Select(value => new NormalizationBindingInputReference(
                value.RevisionUuid,
                new(
                    rows[value.RevisionUuid].Provider,
                    rows[value.RevisionUuid].Type,
                    rows[value.RevisionUuid].Key
                )
            ))
            .ToArray();
    }

    private static async Task<RepertoireResolutionAssociation[]> ReadConsumptionDecisions(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        CompletedRepertoireAttempt attempt,
        RepertoireResolutionSourceSnapshot source,
        ConsumptionBudget budget,
        CancellationToken ct
    )
    {
        var bound = await c.QuerySingleAsync<ConsumptionPayloadBound>(
            new CommandDefinition(
                """
                SELECT count(*) AS "Count",COALESCE(sum(octet_length(evidence::text)+octet_length(correspondence_key)+octet_length(reason)),0)::bigint AS "Bytes",
                    COALESCE(max(octet_length(evidence::text)),0)::bigint AS "Largest"
                FROM normalization.repertoire_resolution_associations WHERE attempt_uuid=@uuid
                """,
                new { uuid = attempt.AttemptUuid },
                t,
                cancellationToken: ct
            )
        );
        budget.Add(bound.Bytes, MaximumPreparationBytes);
        if (
            bound.Count != source.Claims.Count
            || bound.Count > NormalizationImportedCatalogService.MaximumRepertoireSongs
            || bound.Largest > MaximumCheckpointBytes
        )
        {
            throw new NormalizationConflictException(
                "The completed account has no bounded complete claim correspondence."
            );
        }
        var rows = await c.QueryAsync<AssociationRow>(
            new CommandDefinition(
                """
                SELECT decision_uuid AS "DecisionUuid",registration_uuid AS "RegistrationUuid",attempt_uuid AS "AttemptUuid",
                    claim_uuid AS "ClaimUuid",correspondence_key AS "Key",repertoire_song_uuid AS "SongUuid",disposition AS "Disposition",
                    reason AS "Reason",evidence::text AS "Evidence" FROM normalization.repertoire_resolution_associations WHERE attempt_uuid=@uuid ORDER BY claim_uuid
                """,
                new { uuid = attempt.AttemptUuid },
                t,
                cancellationToken: ct
            )
        );
        var claims = source.Claims.ToDictionary(value => value.ClaimUuid);
        var decisions = rows.Select(row => new RepertoireResolutionAssociation(
                row.DecisionUuid,
                row.RegistrationUuid,
                row.AttemptUuid,
                row.ClaimUuid,
                row.Key,
                row.SongUuid,
                Enum.Parse<RepertoireResolutionClaimDisposition>(row.Disposition),
                row.Reason,
                JsonSerializer.Deserialize<NormalizedEvidenceReference[]>(row.Evidence)!
            ))
            .ToArray();
        if (
            decisions.Any(value =>
                value.RegistrationUuid != attempt.Registration.RegistrationUuid
                || !claims.TryGetValue(value.ClaimUuid, out var claim)
                || claim.CorrespondenceKey != value.CorrespondenceKey
                || (
                    value.Disposition
                    is RepertoireResolutionClaimDisposition.Bound
                        or RepertoireResolutionClaimDisposition.Provisional
                ) != value.RepertoireSongUuid.HasValue
            ) || !decisions.Select(value => value.ClaimUuid).ToHashSet().SetEquals(claims.Keys)
        )
        {
            throw new NormalizationConflictException(
                "Completed decisions do not identify the exact original claims and targets."
            );
        }
        return decisions;
    }

    private static async Task<IReadOnlyList<CompletedRepertoireTarget>> ReadConsumptionTargets(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid? artist,
        RepertoireResolutionAssociation[] decisions,
        ConsumptionBudget budget,
        CancellationToken ct
    )
    {
        var roots = decisions
            .Where(value => value.RepertoireSongUuid.HasValue)
            .Select(value => value.RepertoireSongUuid!.Value)
            .Distinct()
            .ToArray();
        if (roots.Length == 0)
        {
            return [];
        }
        if (artist == null)
        {
            throw new NormalizationConflictException(
                "An unbound source cannot supply accepted repertoire targets."
            );
        }
        var known = new Dictionary<Guid, ConsumptionTargetHeader>();
        var pending = roots;
        while (pending.Length > 0)
        {
            if (known.Count + pending.Length > 100000)
            {
                throw new CompletedRepertoireConsumptionCapacityException(
                    "The accepted repertoire redirect closure exceeds 100,000 identities."
                );
            }
            var lengths = await c.QuerySingleAsync<ConsumptionPayloadBound>(
                new CommandDefinition(
                    """
                    SELECT count(*) AS "Count",COALESCE(sum(octet_length(display_title)),0)::bigint AS "Bytes",
                        COALESCE(max(octet_length(display_title)),0)::bigint AS "Largest"
                    FROM normalization.repertoire_songs WHERE uuid=ANY(@pending)
                    """,
                    new { pending },
                    t,
                    cancellationToken: ct
                )
            );
            budget.Add(lengths.Bytes, MaximumCheckpointBytes);
            if (lengths.Count != pending.Length || lengths.Largest > MaximumCheckpointBytes)
            {
                throw new NormalizationConflictException(
                    "An accepted repertoire path is missing or exceeds its projection bound."
                );
            }
            var rows = (
                await c.QueryAsync<ConsumptionTargetHeader>(
                    new CommandDefinition(
                        """
                        SELECT x.uuid AS "Uuid",x.artist_uuid AS "ArtistUuid",x.display_title AS "Title",x.retracted AS "Retracted",
                            x.redirect_uuid AS "RedirectUuid",x.revision_uuid AS "RevisionUuid",r.revision AS "Revision"
                        FROM normalization.repertoire_songs x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                        WHERE x.uuid=ANY(@pending) ORDER BY x.uuid
                        """,
                        new { pending },
                        t,
                        cancellationToken: ct
                    )
                )
            ).ToArray();
            if (
                rows.Length != pending.Length
                || rows.Any(value => value.Retracted || value.ArtistUuid != artist)
            )
            {
                throw new NormalizationConflictException(
                    "An accepted repertoire path is retracted, missing or outside the source artist."
                );
            }
            foreach (var row in rows)
            {
                known[row.Uuid] = row;
            }
            pending = rows.Where(value =>
                    value.RedirectUuid.HasValue && !known.ContainsKey(value.RedirectUuid.Value)
                )
                .Select(value => value.RedirectUuid!.Value)
                .Distinct()
                .ToArray();
        }
        var encodedTitles = known.ToDictionary(
            pair => pair.Key,
            pair => JsonEncodedText.Encode(pair.Value.Title).EncodedUtf8Bytes.Length
        );
        var paths = new Dictionary<Guid, ConsumptionTargetHeader[]>();
        var pathEntries = 0;
        foreach (var root in roots)
        {
            var path = new List<ConsumptionTargetHeader>();
            var visited = new HashSet<Guid>();
            var current = root;
            while (true)
            {
                if (!visited.Add(current))
                {
                    throw new NormalizationConflictException(
                        "The accepted repertoire redirect path contains a cycle."
                    );
                }
                var row = known[current];
                // The unique-header bound alone does not bound many roots sharing a long suffix.
                // Charge every returned path entry before allocating either path array below.
                if (++pathEntries > 100000)
                {
                    throw new CompletedRepertoireConsumptionCapacityException(
                        "The complete accepted repertoire paths exceed 100,000 entries."
                    );
                }
                budget.Add(512L + encodedTitles[row.Uuid], MaximumCheckpointBytes);
                path.Add(row);
                if (row.RedirectUuid is not { } next)
                {
                    break;
                }
                current = next;
            }
            paths[root] = path.ToArray();
        }
        var canonical = paths.Values.Select(path => path[^1].Uuid).Distinct().ToArray();
        var bound = await c.QuerySingleAsync<ConsumptionPayloadBound>(
            new CommandDefinition(
                """
                SELECT count(*) AS "Count",COALESCE(sum(octet_length(r.payload::text)+octet_length(r.evidence::text)),0)::bigint AS "Bytes",
                    COALESCE(max(octet_length(r.payload::text)+octet_length(r.evidence::text)),0)::bigint AS "Largest"
                FROM normalization.repertoire_songs x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid WHERE x.uuid=ANY(@canonical)
                """,
                new { canonical },
                t,
                cancellationToken: ct
            )
        );
        budget.Add(bound.Bytes, MaximumPreparationBytes);
        if (bound.Count != canonical.Length || bound.Largest > MaximumCheckpointBytes)
        {
            throw new CompletedRepertoireConsumptionCapacityException(
                "The complete accepted repertoire assertion projection exceeds its bound."
            );
        }
        var assertions = (
            await c.QueryAsync<ConsumptionTargetAssertion>(
                new CommandDefinition(
                    """
                    SELECT x.uuid AS "Uuid",r.payload::text AS "Payload",r.evidence::text AS "Evidence",r.basis AS "Basis",
                        r.created_at AS "CreatedAt",r.decision_uuid AS "DecisionUuid"
                    FROM normalization.repertoire_songs x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid WHERE x.uuid=ANY(@canonical)
                    """,
                    new { canonical },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToDictionary(value => value.Uuid);
        var owner =
            await ReadConsumptionArtistOwner(c, t, artist.Value, ct)
            ?? throw new NormalizationConflictException(
                "The accepted repertoire artist has no retained owner clock."
            );
        // Several immutable associations can converge on one survivor. Decode each canonical
        // value once; its per-root encoded expansion is charged separately below.
        var accepted = assertions.ToDictionary(
            pair => pair.Key,
            pair =>
            {
                var header = known[pair.Key];
                var assertion = pair.Value;
                var value = JsonSerializer.Deserialize<RepertoireSongChange>(
                    assertion.Payload
                )! with
                {
                    ExpectedRevision = header.Revision,
                    Basis = assertion.Basis,
                    Evidence = JsonSerializer.Deserialize<NormalizedEvidenceReference[]>(
                        assertion.Evidence
                    )!,
                    Retracted = false,
                };
                if (
                    value.Uuid != header.Uuid
                    || value.ArtistUuid != artist
                    || value.DisplayTitle != header.Title
                )
                {
                    throw new NormalizationConflictException(
                        "The accepted repertoire revision contradicts its current identity scope."
                    );
                }
                return new MusicRecord<RepertoireSongChange>(
                    value,
                    header.Revision,
                    assertion.CreatedAt,
                    assertion.DecisionUuid
                );
            }
        );
        // Default JSON encoding may expand Unicode beyond PostgreSQL's jsonb text. Measure
        // one complete accepted record per canonical, including evidence and request metadata,
        // then bound every embedded copy before constructing the returned target projections.
        var encodedAccepted = accepted.ToDictionary(
            pair => pair.Key,
            pair =>
                JsonSerializer
                    .SerializeToUtf8Bytes(pair.Value with { RequestedUuid = Guid.Empty })
                    .LongLength
        );
        foreach (var root in roots)
        {
            budget.Add(encodedAccepted[paths[root][^1].Uuid], MaximumCheckpointBytes);
        }
        return roots
            .Select(root =>
            {
                var path = paths[root];
                var header = path[^1];
                return new CompletedRepertoireTarget(
                    root,
                    header.Uuid,
                    header.RevisionUuid,
                    accepted[header.Uuid] with
                    {
                        RequestedUuid = root,
                    },
                    path.Select(item => new CandidateIdentityHeader(
                            item.Uuid,
                            item.Title,
                            item.Revision,
                            owner.SubjectUuid,
                            ArtistUuid: item.ArtistUuid
                        ))
                        .ToArray()
                );
            })
            .ToArray();
    }

    private static async Task<CompletedRepertoireAcceptedReadSet> ReadConsumptionAcceptedOwners(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid? artist,
        bool required,
        CancellationToken ct
    )
    {
        var owner = artist is { } uuid ? await ReadConsumptionArtistOwner(c, t, uuid, ct) : null;
        if (owner == null)
        {
            if (required)
            {
                throw new NormalizationConflictException(
                    "Accepted targets require their current retained artist owner clock."
                );
            }
            return new([], []);
        }
        return new(
            [
                new(
                    owner.SubjectUuid,
                    owner.DesiredGeneration,
                    owner.EditorialRevision,
                    owner.PublishedWorkUuid
                ),
            ],
            [owner]
        );
    }

    private static Task<NormalizationSubjectObservation?> ReadConsumptionArtistOwner(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid artist,
        CancellationToken ct
    ) =>
        c.QuerySingleOrDefaultAsync<NormalizationSubjectObservation>(
            new CommandDefinition(
                """
                SELECT uuid AS "SubjectUuid",desired_generation AS "DesiredGeneration",editorial_revision AS "EditorialRevision",
                    published_work_uuid AS "PublishedWorkUuid" FROM normalization.work_subjects
                WHERE domain='songs' AND subject_kind='artist_repertoire' AND subject_uuid=@artist
                """,
                new { artist },
                t,
                cancellationToken: ct
            )
        );

    private sealed class ConsumptionBudget
    {
        private long consumed;

        public void Add(long bytes, long phaseLimit)
        {
            if (bytes < 0 || bytes > phaseLimit || bytes > MaximumSummaryBytes - consumed)
            {
                throw new CompletedRepertoireConsumptionCapacityException(
                    "The complete repertoire consumption exceeds its bounded snapshot budget."
                );
            }
            consumed += bytes;
        }
    }

    private sealed record ConsumptionAccountRow(
        Guid RegistrationUuid,
        string Registration,
        string Request,
        string Version,
        string? Result,
        string? Source
    );

    private sealed record ConsumptionAckMetadata(Guid StageUuid, long Bytes);

    private sealed record ConsumptionAckRow(
        Guid WorkUuid,
        string? Ticket,
        string? Receipt,
        bool? Current,
        bool? Pending
    );

    private sealed record ConsumptionInputRow(
        Guid Uuid,
        Guid ResourceUuid,
        string Provider,
        string Type,
        string Key
    );

    private sealed record ConsumptionPayloadBound(long Count, long Bytes, long Largest);

    private sealed record ConsumptionTargetHeader(
        Guid Uuid,
        Guid ArtistUuid,
        string Title,
        bool Retracted,
        Guid? RedirectUuid,
        Guid RevisionUuid,
        long Revision
    );

    private sealed record ConsumptionTargetAssertion(
        Guid Uuid,
        string Payload,
        string Evidence,
        string Basis,
        DateTime CreatedAt,
        Guid DecisionUuid
    );

    private static async Task RequireConsumptionProducerCurrent(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        RepertoireResolutionReadSet read,
        CancellationToken ct
    )
    {
        // These are producer observations, including absence. Re-reading current values and
        // replacing them here would authenticate different bytes under an old extraction.
        var current = await c.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT true
                    AND NOT EXISTS(
                        SELECT 1 FROM jsonb_to_recordset(@inputs::jsonb) AS i("ResourceUuid" uuid,"RevisionUuid" uuid,"Generation" bigint)
                        LEFT JOIN normalization.input_resources p ON p.uuid=i."ResourceUuid"
                        WHERE p.uuid IS NULL OR p.current_revision_uuid IS DISTINCT FROM i."RevisionUuid" OR p.generation<>i."Generation")
                    AND NOT EXISTS(
                        SELECT 1 FROM jsonb_to_recordset(@catalog::jsonb) AS i("ArtistUuid" uuid,"Generation" bigint)
                        LEFT JOIN normalization.catalog_artist_revisions p ON p.artist_uuid=i."ArtistUuid"
                        WHERE p.artist_uuid IS NULL OR p.generation<>i."Generation")
                    AND NOT EXISTS(
                        SELECT 1 FROM jsonb_to_recordset(@identities::jsonb) AS i("UpstreamIdentifier" text,"Generation" bigint)
                        LEFT JOIN normalization.catalog_source_identity_revisions p ON p.upstream_identifier=i."UpstreamIdentifier"
                        WHERE p.upstream_identifier IS NULL OR p.generation<>i."Generation")
                    AND NOT EXISTS(
                        SELECT 1 FROM jsonb_to_recordset(@sourceClocks::jsonb) AS i("SourceUuid" uuid,"Generation" bigint)
                        LEFT JOIN normalization.catalog_source_uuid_revisions p ON p.source_uuid=i."SourceUuid"
                        WHERE p.source_uuid IS NULL OR p.generation<>i."Generation")
                    AND NOT EXISTS(
                        SELECT 1 FROM jsonb_to_recordset(@venues::jsonb) AS i("VenueUuid" uuid,"Generation" bigint)
                        LEFT JOIN normalization.catalog_venue_revisions p ON p.venue_uuid=i."VenueUuid"
                        WHERE p.venue_uuid IS NULL OR p.generation<>i."Generation")
                    AND (@mappingGeneration::bigint IS NULL OR EXISTS(SELECT 1 FROM normalization.catalog_mapping_registry_revision
                        WHERE singleton AND generation=@mappingGeneration))
                    AND NOT EXISTS(
                        SELECT 1 FROM jsonb_to_recordset(@dependencies::jsonb) AS i("SubjectUuid" uuid,"DesiredGeneration" bigint,"EditorialRevision" bigint,"PublishedWorkUuid" uuid)
                        LEFT JOIN normalization.work_subjects s ON s.uuid=i."SubjectUuid"
                        WHERE s.uuid IS NULL OR s.desired_generation<>i."DesiredGeneration" OR s.editorial_revision<>i."EditorialRevision"
                            OR s.published_work_uuid IS DISTINCT FROM i."PublishedWorkUuid")
                    AND NOT EXISTS(
                        SELECT 1 FROM jsonb_to_recordset(@observations::jsonb) AS i("SubjectUuid" uuid,"DesiredGeneration" bigint,"EditorialRevision" bigint,"PublishedWorkUuid" uuid)
                        LEFT JOIN normalization.work_subjects s ON s.uuid=i."SubjectUuid"
                        WHERE s.uuid IS NULL OR s.desired_generation<>i."DesiredGeneration" OR s.editorial_revision<>i."EditorialRevision"
                            OR s.published_work_uuid IS DISTINCT FROM i."PublishedWorkUuid")
                    AND NOT EXISTS(
                        SELECT 1 FROM jsonb_array_elements(@universes::jsonb) i
                        LEFT JOIN normalization.candidate_universe_revisions p ON p.domain=i->'Universe'->>'Domain'
                            AND p.scope_uuid=(i->'Universe'->>'ScopeUuid')::uuid
                        WHERE (p.domain IS NULL AND i->'Universe'->>'Domain'<>'repertoire')
                            OR COALESCE(p.generation,0)<>(i->>'Generation')::bigint)
                """,
                new
                {
                    inputs = JsonSerializer.Serialize(read.Inputs),
                    catalog = JsonSerializer.Serialize(read.CatalogInputs),
                    identities = JsonSerializer.Serialize(read.SourceIdentities),
                    sourceClocks = JsonSerializer.Serialize(read.SourceUuids),
                    venues = JsonSerializer.Serialize(read.ImportedVenues),
                    mappingGeneration = read.MappingRegistry?.Generation,
                    dependencies = JsonSerializer.Serialize(read.Dependencies),
                    observations = JsonSerializer.Serialize(read.OwnerObservations),
                    universes = JsonSerializer.Serialize(read.CandidateUniverses),
                },
                t,
                cancellationToken: ct
            )
        );
        if (!current)
        {
            throw new NormalizationConflictException(
                "The original producer observations changed before consumption."
            );
        }
    }
}
