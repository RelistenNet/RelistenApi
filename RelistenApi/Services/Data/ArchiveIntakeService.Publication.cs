using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json;
using Relisten.Api.Models;
using Relisten.Import;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.TapersSection;
using Relisten.Services.Indexing;
using Relisten.Vendor.ArchiveOrg;
using Relisten.Vendor.ArchiveOrg.Metadata;

namespace Relisten.Data;

public sealed partial class ArchiveIntakeService
{
    /// <summary>
    /// Creates an approved new artist once, under the same registry lock as authenticated
    /// artist writes and Archive collection discovery. A catalog match is deferred, never suffixed.
    /// </summary>
    public Task<ArtistId?> MaterializeArtistAsync(
        Guid candidateId,
        CancellationToken token = default,
        DateTimeOffset? deadline = null
    ) =>
        transactions.RunArtistRegistryAsync<ArtistId?>(
            deadline ?? DateTimeOffset.UtcNow.AddMinutes(5),
            token,
            async () =>
            {
                var candidate = await LockCandidateAsync(candidateId);
                if (candidate == null || !candidate.approved)
                {
                    return null;
                }
                if (candidate.artist_uuid is { } mapped)
                {
                    var existing = await artists.FindArtistForImport(mapped.ToString());
                    if (IdentityMatches(candidate, existing))
                    {
                        return new ArtistId(mapped);
                    }
                    await CandidateErrorAsync(candidateId, "catalog_identity_missing");
                    return null;
                }

                var overlaps = (await LoadCandidatesAsync())
                    .Where(other =>
                        other.candidate_id != candidateId
                        && other.Keys.Intersect(candidate.Keys).Any()
                    )
                    .ToArray();
                var matches = await FindRegistryMatchesAsync(candidate);
                if (
                    matches.Length > 0
                    || overlaps.Length > 0
                    || string.IsNullOrWhiteSpace(candidate.proposed_slug)
                    || string.IsNullOrWhiteSpace(candidate.proposed_name)
                )
                {
                    await db.WithWriteConnection(connection =>
                        connection.ExecuteAsync(
                            """
                            UPDATE normalization.archive_artist_candidates
                            SET identity_status=@state, matched_artist_uuids=@matches,
                                reason=@reason,updated_at=now() WHERE candidate_id=@candidateId
                            """,
                            new
                            {
                                candidateId,
                                matches,
                                state = matches.Length == 1 && overlaps.Length == 0
                                    ? "existing_match"
                                    : "needs_review",
                                reason = matches.Length > 0
                                    ? "existing_artist"
                                    : "ambiguous_creator",
                            }
                        )
                    );
                    return null;
                }

                // Lock every assigned item before membership changes. Ordinary Archive writers
                // lock these same rows before testing ownership; a waiter sees the committed cohort.
                await db.WithWriteConnection(connection =>
                    connection.QueryAsync<string>(
                        "SELECT item_identifier FROM normalization.archive_item_intake WHERE candidate_id=@candidateId ORDER BY item_identifier FOR UPDATE",
                        new { candidateId }
                    )
                );
                var created = await artists.Save(
                    new SlimArtistWithFeatures
                    {
                        name = candidate.proposed_name,
                        slug = candidate.proposed_slug,
                        featured = (int)ArtistFeaturedFlags.AutoCreated,
                        features = ArchiveOrgArtistDefaults.ArchiveOrgDefaultFeatures(),
                    }
                );
                await db.WithWriteConnection(connection =>
                    connection.ExecuteAsync(
                        """
                        UPDATE normalization.archive_artist_candidates
                        SET artist_uuid=@uuid,artist_id=@id,creation_slug=@slug,
                            identity_status='cohort',matched_artist_uuids='{}',reason=NULL,updated_at=now()
                        WHERE candidate_id=@candidateId
                        """,
                        new
                        {
                            created.uuid,
                            created.id,
                            created.slug,
                            candidateId,
                        }
                    )
                );
                return new ArtistId(created.uuid);
            }
        );

    /// <summary>
    /// Approves a retained candidate for an existing catalog artist after reviewing the exact
    /// registry match. Native recordings keep their importer; only missing items published by
    /// intake acquire item ownership. This never creates provider mappings or changes artist flags.
    /// </summary>
    public Task<ArtistId> BindExistingArtistAsync(
        Guid candidateId,
        ArtistId artist,
        string reviewBasis,
        CancellationToken token = default,
        DateTimeOffset? deadline = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewBasis);
        return transactions.RunArtistRegistryAsync(
            deadline ?? DateTimeOffset.UtcNow.AddMinutes(5),
            token,
            async () =>
            {
                var candidate =
                    await LockCandidateAsync(candidateId)
                    ?? throw new ArgumentException(
                        "The artist candidate does not exist.",
                        nameof(candidateId)
                    );
                var target =
                    await artists.FindArtistForImport(artist.ToString())
                    ?? throw new ArgumentException(
                        "The catalog artist does not exist.",
                        nameof(artist)
                    );
                if (candidate.artist_uuid != null)
                {
                    if (
                        !candidate.is_existing_artist
                        || candidate.artist_uuid != target.uuid
                        || candidate.artist_id != target.id
                    )
                    {
                        throw new InvalidOperationException(
                            "A mapped candidate cannot change its catalog identity."
                        );
                    }
                }
                var matches = await FindRegistryMatchesAsync(candidate);
                if (candidate.artist_uuid != null)
                {
                    if (matches.Any(match => match != artist.Value))
                    {
                        throw new InvalidOperationException(
                            "The retained artist labels now match another catalog artist; review the labels before rebinding."
                        );
                    }
                }
                else
                {
                    if (matches.Length != 1 || matches[0] != artist.Value)
                    {
                        throw new ArgumentException(
                            "The candidate must have exactly this one current registry match."
                        );
                    }
                }

                // Candidate mapping is artist approval, not recording ownership. Existing
                // sources remain with the native importer until an intake item actually commits.
                await db.WithWriteConnection(connection =>
                    connection.ExecuteAsync(
                        """
                        UPDATE normalization.archive_artist_candidates
                        SET artist_uuid=@uuid,artist_id=@id,creation_slug=@slug,
                            proposed_name=@name,proposed_slug=@slug,reviewed_creator_keys=@keys,approved=true,
                            is_existing_artist=true,identity_status='existing_match',matched_artist_uuids=ARRAY[@uuid]::uuid[],
                            reason=@reviewBasis,updated_at=now()
                        WHERE candidate_id=@candidateId
                        """,
                        new
                        {
                            candidateId,
                            target.uuid,
                            target.id,
                            target.slug,
                            target.name,
                            keys = candidate
                                .Keys.Append(
                                    ArtistRegistryService.NormalizeIdentityKey(target.name)
                                )
                                .Distinct()
                                .ToArray(),
                            reviewBasis,
                        }
                    )
                );
                return artist;
            }
        );
    }

    private static bool IdentityMatches(CandidateRow candidate, Artist? artist) =>
        artist != null
        && artist.uuid == candidate.artist_uuid
        && artist.id == candidate.artist_id
        && artist.slug == candidate.creation_slug
        && artist.name == candidate.proposed_name;

    private Task CandidateErrorAsync(Guid id, string reason) =>
        db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                "UPDATE normalization.archive_artist_candidates SET identity_status='needs_review',reason=@reason,updated_at=now() WHERE candidate_id=@id",
                new { id, reason }
            )
        );

    /// <summary>
    /// Retains and classifies current evidence before converting the legacy recording DTO.
    /// Supply the raw response from which the projection was made. Unavailable or uncertain
    /// items do not need legacy parsing; a conversion failure leaves retained evidence and
    /// never commits a partial recording. Catalog writes join one publication transaction.
    /// </summary>
    public async Task<TapersSectionItemResult> ApplyObservationAsync(
        TapersSectionItemInput input,
        ArchiveObservationProjection observation,
        string metadataJson,
        CancellationToken token = default,
        DateTimeOffset? deadline = null
    )
    {
        if (input.Item != observation.Item)
        {
            throw new ArgumentException(
                "The observation must identify the requested item.",
                nameof(observation)
            );
        }

        var nativeResolution = await ResolveNativeAsync(observation, token, deadline);
        if (nativeResolution != null)
        {
            return nativeResolution;
        }

        await RecordObservationAsync(observation, token, deadline);
        return await InTransaction(
            token,
            async () =>
            {
                // Every publication follows this lock order. Approval changes take effect
                // between whole recording commits, and never leave half an assignment.
                var candidate = await LockCandidateAsync(input.CandidateId);
                var item = await LockItemAsync(input.Item);
                // Recheck after the item lock: a native writer may have committed after
                // the observation check. Reuse its source rather than acquiring it.
                var linkedNative = await ResolveNativeAsync(observation, token, deadline);
                if (linkedNative != null)
                {
                    return linkedNative;
                }
                if (candidate == null || !candidate.approved)
                {
                    await SetItemStateAsync(input.Item, "paused", "unapproved_artist");
                    return new(
                        input.Item,
                        TapersSectionItemOutcome.Paused,
                        item.published_source_uuid,
                        TapersSectionReason.UnapprovedArtist
                    );
                }

                var capturedId = await db.WithWriteConnection(connection =>
                    connection.QuerySingleAsync<Guid>(
                        """
                        SELECT observation_id FROM normalization.archive_item_observations
                        WHERE item_identifier=@item AND projection_version=@ProjectionVersion AND content_hash=@ContentHash
                        """,
                        new
                        {
                            item = input.Item.Value,
                            observation.ProjectionVersion,
                            observation.ContentHash,
                        }
                    )
                );
                if (!observation.IsAvailabilityOnly && item.latest_observation_id != capturedId)
                {
                    return new(
                        input.Item,
                        TapersSectionItemOutcome.NeedsReview,
                        item.published_source_uuid
                    );
                }

                var interpretation = await InterpretAsync(observation, item);
                if (
                    !interpretation.IsEligible
                    || interpretation.CandidateId != candidate.candidate_id
                )
                {
                    await SaveInterpretationAsync(item, capturedId, observation, interpretation);
                    return Result(item, interpretation);
                }

                var artist = await ResolvePublicationArtistAsync(candidate, item);
                if (artist == null)
                {
                    await SetItemStateAsync(input.Item, "needs_review", "catalog_identity_missing");
                    return new(
                        input.Item,
                        TapersSectionItemOutcome.NeedsReview,
                        item.published_source_uuid,
                        TapersSectionReason.CatalogIdentityMissing
                    );
                }

                var sameEvidence =
                    item.published_observation_id == capturedId
                    && item.published_rules_version == interpretation.RulesVersion;
                // Reviews are deliberately outside the decision projection. Refresh the writer
                // even when assignment evidence is unchanged, so review-only changes still apply.
                var recording =
                    JsonConvert.DeserializeObject<RootObject>(
                        metadataJson,
                        new TolerantStringConverter()
                    ) ?? throw new System.IO.InvalidDataException("Archive metadata was empty.");
                Source source;
                try
                {
                    source = await CommitRecordingAsync(
                        artist,
                        item,
                        capturedId,
                        interpretation,
                        recording
                    );
                }
                catch (ArchivePerformanceHeldException held)
                {
                    return await RetainPerformanceHoldAsync(
                        item,
                        capturedId,
                        observation,
                        interpretation,
                        held.Message
                    );
                }
                // Accept current metadata with the owned source in the same transaction.
                // Native source linking uses the separate conservative resolution policy.
                await new ArchiveItemResolutionService(db).BindPublishedAsync(
                    input.Item.Value,
                    capturedId,
                    artist.uuid,
                    source.uuid,
                    token,
                    deadline
                );
                return new(
                    input.Item,
                    sameEvidence
                        ? TapersSectionItemOutcome.Unchanged
                        : TapersSectionItemOutcome.Published,
                    source.uuid
                );
            },
            deadline
        );
    }

    private async Task<TapersSectionItemResult> RetainPerformanceHoldAsync(
        ItemRow item,
        Guid observationId,
        ArchiveObservationProjection observation,
        TapersSectionInterpretation interpretation,
        string reason
    )
    {
        // The writer retained unresolved coverage without changing playback. Preserve
        // accepted publication references while retaining why current metadata is held.
        var title =
            ArchiveObservationProjector
                .ParseObject(observation.PayloadJson)["metadata"]
                ?["title"]?.ToString()
            ?? "";
        var held = interpretation with
        {
            Reasons = interpretation
                .Reasons.Append(TapersSectionReason.AmbiguousDate)
                .Distinct()
                .ToArray(),
            Evidence = interpretation
                .Evidence.Append(new TapersSectionEvidence("metadata.title", title, reason))
                .ToArray(),
        };
        await SaveInterpretationAsync(item, observationId, observation, held);
        if (item.published_source_uuid != null)
        {
            // Coverage changes derived enrichment even when playback stays accepted.
            // Durable dirty work lets a paused artist recover after workflow loss.
            await db.WithWriteConnection(connection =>
                connection.ExecuteAsync(
                    "UPDATE normalization.archive_item_intake SET catalog_revision=catalog_revision+1 WHERE item_identifier=@item",
                    new { item = item.item_identifier }
                )
            );
        }
        return Result(item, held);
    }

    private async Task<Artist?> ResolvePublicationArtistAsync(CandidateRow candidate, ItemRow item)
    {
        var artist = candidate.artist_uuid is { } uuid
            ? await artists.FindArtistForImport(uuid.ToString())
            : null;
        if (!IdentityMatches(candidate, artist) || item.candidate_id != candidate.candidate_id)
        {
            return null;
        }

        // An unexplained source for this identifier cannot be adopted or reparented.
        // Check the catalog against the last committed ownership record before
        // applying current metadata. The new date may change in the same transaction;
        // artist/source identity and out-of-band catalog edits remain protected.
        var sources = (
            await db.WithWriteConnection(connection =>
                connection.QueryAsync<SourceIdentity>(
                    "SELECT uuid,artist_id,display_date FROM sources WHERE upstream_identifier=@item",
                    new { item = item.item_identifier }
                )
            )
        ).ToArray();
        var unexpectedSource = sources.Any(source =>
            source.artist_id != artist!.id
            || source.uuid != item.published_source_uuid
            || source.display_date != item.published_display_date
        );
        var publishedSourceMissing =
            item.published_source_uuid != null
            && sources.All(source => source.uuid != item.published_source_uuid);
        return unexpectedSource || publishedSourceMissing ? null : artist;
    }

    private async Task<Source> CommitRecordingAsync(
        Artist artist,
        ItemRow item,
        Guid observationId,
        TapersSectionInterpretation interpretation,
        RootObject recording
    )
    {
        var provider =
            await upstreamSources.FindUpstreamSourceByName(UpstreamProvider.ArchiveOrg)
            ?? throw new InvalidOperationException("Archive provider is not configured.");
        var source = await importer.WriteResolvedRecordingAsync(
            artist,
            provider.id,
            new SearchDoc
            {
                identifier = item.item_identifier,
                raw_display_date = interpretation.DisplayDate!,
                // Item intake has no search freshness record. This write uses the current
                // metadata check time for catalog update timestamps, never DateTime.MinValue.
                updatedate = [DateTime.UtcNow],
            },
            recording,
            interpretation.DisplayDate!
        );
        await db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                """
                UPDATE normalization.archive_item_intake SET
                    published_observation_id=@observationId,published_rules_version=@RulesVersion,
                    published_artist_uuid=@artistUuid,published_artist_id=@artistId,
                    published_display_date=@DisplayDate,published_source_uuid=@sourceUuid,
                    published_at=now(),catalog_revision=catalog_revision+1,
                    status='published',reason=NULL,last_error=NULL
                WHERE item_identifier=@item
                """,
                new
                {
                    observationId,
                    interpretation.RulesVersion,
                    artistUuid = artist.uuid,
                    artistId = artist.id,
                    interpretation.DisplayDate,
                    sourceUuid = source.uuid,
                    item = item.item_identifier,
                }
            )
        );
        return source;
    }

    private Task SetItemStateAsync(ArchiveItemId item, string status, string reason) =>
        db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                "UPDATE normalization.archive_item_intake SET status=@status,reason=@reason WHERE item_identifier=@item",
                new
                {
                    item = item.Value,
                    status,
                    reason,
                }
            )
        );

    private sealed class SourceIdentity
    {
        public Guid uuid { get; set; }
        public int artist_id { get; set; }
        public string display_date { get; set; } = "";
    }
}
