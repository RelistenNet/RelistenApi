using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json.Linq;
using Relisten.Api.Models;
using Relisten.Ingestion.Providers.TapersSection;

namespace Relisten.Data;

/// <summary>
/// Links retained Archive items to exact canonical recordings. This service writes only resolution
/// evidence: it never imports, reparents, refreshes, deletes, or claims ownership of a source.
/// </summary>
public sealed class ArchiveItemResolutionService(DbService db)
{
    /// <summary>Returns operator state, including unresolved, conflicting, and missing canonical references.</summary>
    public Task<ArchiveItemResolution?> FindAsync(string itemIdentifier) =>
        db.WithWriteConnection(connection =>
            connection.QuerySingleOrDefaultAsync<ArchiveItemResolution>(
                "SELECT * FROM normalization.archive_item_resolutions WHERE item_identifier=@itemIdentifier",
                new { itemIdentifier }
            )
        );

    /// <summary>
    /// Rechecks provider-qualified identity on the primary. A uniquely matched native source can
    /// establish the performer; an explicit expected performer or metadata conflict takes precedence.
    /// A prior accepted source/artist cannot be replaced by this ordinary refresh operation.
    /// </summary>
    public Task<ArchiveItemResolution> ResolveExistingAsync(
        ArchiveItemResolutionRequest request,
        CancellationToken token = default,
        DateTimeOffset? deadline = null
    )
    {
        if (string.IsNullOrWhiteSpace(request.item_identifier))
        {
            throw new ArgumentException("An Archive item identifier is required.");
        }
        if (string.IsNullOrWhiteSpace(request.decision_basis))
        {
            throw new ArgumentException("A retained decision basis is required.");
        }
        return new CatalogImportTransaction(db).RunAsync(
            deadline ?? DateTimeOffset.UtcNow.AddMinutes(5),
            token,
            async () =>
            {
                await ValidateEvidenceAsync(request);
                var prior = await LockResolutionAsync(request.item_identifier);
                // Read the project identity without locking its candidate here: writers use
                // candidate -> intake item -> resolution, and we already hold resolution.
                var performer = await new ArchivePerformerReviewService(
                    db
                ).FindPerformerDecisionAsync(request.item_identifier);
                var reviewedArtist = performer?.SelectedArtistUuid;
                var matches = await LoadCanonicalMatchesAsync(request.item_identifier);

                var conflict = !string.IsNullOrWhiteSpace(request.conflict_reason)
                    ? request.conflict_reason
                    : await MetadataChangeReasonAsync(prior, request);
                if (performer != null)
                {
                    conflict =
                        await PerformerReviewConflictAsync(performer, prior, request) ?? conflict;
                }
                if (
                    prior.canonical_source_uuid != null
                    && prior.selected_artist_uuid != null
                    && request.expected_artist_uuid != null
                    && prior.selected_artist_uuid != request.expected_artist_uuid
                )
                {
                    conflict =
                        "The requested performer conflicts with the retained item assignment.";
                }
                if (matches.Length > 1)
                {
                    conflict =
                        "Several Archive sources match this exact provider item; catalog repair is required.";
                }
                var match = matches.Length == 1 ? matches[0] : null;
                var expectedArtist =
                    prior.canonical_source_uuid != null
                        ? prior.selected_artist_uuid
                        : request.expected_artist_uuid
                            ?? reviewedArtist
                            ?? prior.selected_artist_uuid;
                if (match != null && expectedArtist != null && match.artist_uuid != expectedArtist)
                {
                    conflict =
                        "The canonical source performer conflicts with the retained item assignment.";
                }
                if (
                    match != null
                    && prior.canonical_source_uuid != null
                    && prior.canonical_source_uuid != match.source_uuid
                )
                {
                    conflict =
                        "The exact item now resolves to a different source; preserve the accepted reference for repair.";
                }

                // Missing/held decisions retain accepted UUIDs. Reads join those UUIDs against
                // current catalog state; they never substitute another same-date recording.
                var resolved = string.IsNullOrWhiteSpace(conflict) && match != null;
                var sourceUuid =
                    prior.canonical_source_uuid ?? (resolved ? match!.source_uuid : (Guid?)null);
                var artistUuid =
                    (prior.canonical_source_uuid != null ? prior.selected_artist_uuid : null)
                    ?? request.expected_artist_uuid
                    ?? reviewedArtist
                    ?? prior.selected_artist_uuid
                    ?? (resolved ? match!.artist_uuid : (Guid?)null);
                var status =
                    !string.IsNullOrWhiteSpace(conflict) ? "conflict"
                    : resolved ? "resolved"
                    : prior.canonical_source_uuid != null ? "missing"
                    : "pending";
                var basis =
                    prior.canonical_source_uuid != null
                        ? prior.decision_basis
                        : request.decision_basis;
                var acceptedObservation =
                    prior.accepted_observation_id
                    ?? (
                        resolved
                            ? request.observation_id
                                ?? prior.observation_id
                                ?? performer?.SupportingObservationId
                            : null
                    );
                return await db.WithWriteConnection(connection =>
                    connection.QuerySingleAsync<ArchiveItemResolution>(
                        """
                        UPDATE normalization.archive_item_resolutions SET observation_id=COALESCE(@observationId,observation_id),
                            accepted_observation_id=@acceptedObservation,selected_artist_uuid=@artistUuid,canonical_source_uuid=@sourceUuid,
                            decision_basis=@basis,status=@status,reason=@reason,last_error=NULL,last_attempt_at=now(),updated_at=now()
                        WHERE item_identifier=@item RETURNING *
                        """,
                        new
                        {
                            item = request.item_identifier,
                            observationId = request.observation_id,
                            acceptedObservation,
                            artistUuid,
                            sourceUuid,
                            basis,
                            status,
                            reason = conflict
                                ?? (
                                    status == "missing"
                                        ? "The accepted Archive source is absent from the current catalog."
                                    : status == "pending"
                                        ? "No exact provider-qualified canonical source is available."
                                    : null
                                ),
                        }
                    )
                );
            }
        );
    }

    /// <summary>
    /// Acknowledges an owned publication and advances its accepted metadata. Unlike native source
    /// reuse, this requires matching intake publication references and an approved artist. A retained
    /// item review must also remain approved and applicable. It cannot acquire ownership or replace an accepted canonical artist/source.
    /// </summary>
    public Task<ArchiveItemResolution> BindPublishedAsync(
        string itemIdentifier,
        Guid observationId,
        Guid artistUuid,
        Guid sourceUuid,
        CancellationToken token = default,
        DateTimeOffset? deadline = null
    ) =>
        new CatalogImportTransaction(db).RunAsync(
            deadline ?? DateTimeOffset.UtcNow.AddMinutes(5),
            token,
            async () =>
            {
                var publication = await db.WithWriteConnection(connection =>
                    connection.QuerySingleOrDefaultAsync<OwnedPublication>(
                        """
                        SELECT i.published_observation_id,i.published_artist_uuid,i.published_source_uuid,c.artist_uuid AS cohort_artist_uuid,c.approved
                        FROM normalization.archive_item_intake i
                        JOIN normalization.archive_artist_candidates c ON c.candidate_id=i.candidate_id
                        WHERE i.item_identifier=@itemIdentifier FOR UPDATE OF i
                        """,
                        new { itemIdentifier }
                    )
                );
                if (
                    publication == null
                    || !publication.approved
                    || publication.published_observation_id != observationId
                    || publication.published_artist_uuid != artistUuid
                    || publication.cohort_artist_uuid != artistUuid
                    || publication.published_source_uuid != sourceUuid
                )
                {
                    throw new InvalidOperationException(
                        "Only the current owned intake publication can advance accepted item metadata."
                    );
                }
                var prior = await LockResolutionAsync(itemIdentifier);
                if (
                    prior.canonical_source_uuid != null
                    && (
                        prior.canonical_source_uuid != sourceUuid
                        || prior.selected_artist_uuid != artistUuid
                    )
                )
                {
                    throw new InvalidOperationException(
                        "The accepted canonical identity requires reviewed catalog repair before replacement."
                    );
                }
                var matches = await LoadCanonicalMatchesAsync(itemIdentifier);
                if (
                    matches.Length != 1
                    || matches[0].source_uuid != sourceUuid
                    || matches[0].artist_uuid != artistUuid
                )
                {
                    throw new InvalidOperationException(
                        "The owned publication must be the unique exact Archive source for this item."
                    );
                }
                var performer = await new ArchivePerformerReviewService(
                    db
                ).FindPerformerDecisionAsync(itemIdentifier);
                var request = new ArchiveItemResolutionRequest(itemIdentifier)
                {
                    observation_id = observationId,
                    expected_artist_uuid = artistUuid,
                    decision_basis =
                        performer?.ReviewBasis
                        ?? "Approved intake artist and committed recording ownership.",
                };
                if (performer != null)
                {
                    var conflict = await PerformerReviewConflictAsync(performer, prior, request);
                    if (conflict != null)
                    {
                        throw new InvalidOperationException(conflict);
                    }
                }
                return await db.WithWriteConnection(connection =>
                    connection.QuerySingleAsync<ArchiveItemResolution>(
                        """
                        UPDATE normalization.archive_item_resolutions SET observation_id=@observationId,
                            accepted_observation_id=@observationId,selected_artist_uuid=@artistUuid,canonical_source_uuid=@sourceUuid,
                            decision_basis=@basis,status='resolved',reason=NULL,last_error=NULL,last_attempt_at=now(),updated_at=now()
                        WHERE item_identifier=@itemIdentifier RETURNING *
                        """,
                        new
                        {
                            itemIdentifier,
                            observationId,
                            artistUuid,
                            sourceUuid,
                            basis = prior.canonical_source_uuid != null
                                ? prior.decision_basis
                                : request.decision_basis,
                        }
                    )
                );
            }
        );

    private async Task<CanonicalMatch[]> LoadCanonicalMatchesAsync(string itemIdentifier) =>
        (
            await db.WithWriteConnection(connection =>
                connection.QueryAsync<CanonicalMatch>(
                    """
                    SELECT s.uuid AS source_uuid,a.uuid AS artist_uuid
                    FROM sources s JOIN artists a ON a.id=s.artist_id
                    WHERE s.upstream_identifier=@item AND EXISTS (
                        SELECT 1 FROM links l JOIN upstream_sources provider ON provider.id=l.upstream_source_id
                        WHERE l.source_id=s.id AND l.for_source AND provider.name='archive.org'
                    ) ORDER BY s.uuid LIMIT 2
                    """,
                    new { item = itemIdentifier }
                )
            )
        ).ToArray();

    private async Task<string?> PerformerReviewConflictAsync(
        ArchivePerformerDecision performer,
        ArchiveItemResolution prior,
        ArchiveItemResolutionRequest request
    )
    {
        if (!performer.Approved)
        {
            return "The retained performer review is not approved.";
        }
        if (performer.Alternatives.Length > 0)
        {
            return "The performer review still has unresolved alternatives.";
        }
        if (performer.SelectedArtistUuid == null)
        {
            return "The reviewed project has no unambiguous catalog artist yet.";
        }
        if (
            request.expected_artist_uuid != null
            && request.expected_artist_uuid != performer.SelectedArtistUuid
        )
        {
            return "The requested performer conflicts with the retained project review.";
        }
        if (
            prior.canonical_source_uuid != null
            && prior.selected_artist_uuid != performer.SelectedArtistUuid
        )
        {
            return "The retained project review conflicts with the accepted canonical performer.";
        }
        var currentObservationId =
            request.observation_id ?? prior.observation_id ?? performer.SupportingObservationId;
        var payload =
            currentObservationId == performer.SupportingObservationId
                ? performer.SupportingPayloadJson
                : await db.WithWriteConnection(connection =>
                    connection.QuerySingleAsync<string>(
                        "SELECT payload::text FROM normalization.archive_item_observations WHERE observation_id=@id",
                        new { id = currentObservationId }
                    )
                );
        return
            ArchivePerformerEvidence.IsApplicable(performer, JObject.Parse(payload))
            && ArchivePerformerEvidence.SupportsProject(performer.ProjectLabels, performer.Evidence)
            ? null
            : "The retained project evidence no longer supports this current observation.";
    }

    private async Task<string?> MetadataChangeReasonAsync(
        ArchiveItemResolution prior,
        ArchiveItemResolutionRequest request
    )
    {
        // A catalog-only retry still uses the latest retained provider evidence. Omitting
        // a fresh observation must not clear a hold established by that evidence.
        var currentObservationId = request.observation_id ?? prior.observation_id;
        if (
            prior.accepted_observation_id == null
            || currentObservationId == null
            || prior.accepted_observation_id == currentObservationId
        )
        {
            return null;
        }

        var observations = (
            await db.WithWriteConnection(connection =>
                connection.QueryAsync<ObservationEvidence>(
                    "SELECT observation_id,payload::text AS payload FROM normalization.archive_item_observations WHERE observation_id=ANY(@ids)",
                    new
                    {
                        ids = new[]
                        {
                            prior.accepted_observation_id.Value,
                            currentObservationId.Value,
                        },
                    }
                )
            )
        ).ToDictionary(row => row.observation_id, row => JObject.Parse(row.payload));
        var accepted = observations[prior.accepted_observation_id.Value]["metadata"];
        var current = observations[currentObservationId.Value]["metadata"];

        // Native catalog identity is the initial performer basis. Later billing/date changes
        // require review before replacing that basis. Even a harmless title edit can hold an
        // update; this compares retained facts and does not infer performing projects.
        foreach (var field in new[] { "creator", "date", "year", "title" })
        {
            var currentValues = MetadataValues(current?[field]);
            if (
                currentValues.Length > 0
                && !currentValues.SequenceEqual(MetadataValues(accepted?[field]))
            )
            {
                return $"Current metadata.{field} changed from the accepted observation; retain the accepted assignment for review.";
            }
        }
        return null;
    }

    private static string[] MetadataValues(JToken? value)
    {
        var values =
            value is JArray array ? array.ToArray()
            : value == null ? []
            : new[] { value };
        return values
            .Where(token => token.Type is not (JTokenType.Null or JTokenType.Undefined))
            .Select(token => ArtistRegistryService.NormalizeIdentityKey(token.ToString()))
            .Where(text => text.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Records a bounded observation failure without changing accepted identities or performer decisions.</summary>
    public Task RecordFailureAsync(string itemIdentifier, string error)
    {
        if (string.IsNullOrWhiteSpace(itemIdentifier) || string.IsNullOrWhiteSpace(error))
        {
            throw new ArgumentException("An item identifier and failure reason are required.");
        }

        return db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                """
                INSERT INTO normalization.archive_item_resolutions(item_identifier,last_error,last_attempt_at)
                VALUES(@itemIdentifier,@error,now())
                ON CONFLICT(item_identifier) DO UPDATE SET last_error=EXCLUDED.last_error,last_attempt_at=now(),updated_at=now()
                """,
                new { itemIdentifier, error = error[..Math.Min(error.Length, 2048)] }
            )
        );
    }

    private async Task ValidateEvidenceAsync(ArchiveItemResolutionRequest request)
    {
        if (
            request.observation_id != null
            && !await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM normalization.archive_item_observations WHERE observation_id=@id AND item_identifier=@item)",
                    new { id = request.observation_id, item = request.item_identifier }
                )
            )
        )
        {
            throw new ArgumentException(
                "The observation must exist and belong to this Archive item."
            );
        }
        if (
            request.expected_artist_uuid != null
            && !await db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM artists WHERE uuid=@artistUuid)",
                    new { artistUuid = request.expected_artist_uuid }
                )
            )
        )
        {
            throw new ArgumentException("The selected performer must exist in the catalog.");
        }
    }

    private Task<ArchiveItemResolution> LockResolutionAsync(string itemIdentifier) =>
        db.WithWriteConnection(async connection =>
        {
            await connection.ExecuteAsync(
                "INSERT INTO normalization.archive_item_resolutions(item_identifier) VALUES(@itemIdentifier) ON CONFLICT DO NOTHING",
                new { itemIdentifier }
            );
            return await connection.QuerySingleAsync<ArchiveItemResolution>(
                "SELECT * FROM normalization.archive_item_resolutions WHERE item_identifier=@itemIdentifier FOR UPDATE",
                new { itemIdentifier }
            );
        });

    private sealed class OwnedPublication
    {
        public Guid? published_observation_id { get; set; }
        public Guid? published_artist_uuid { get; set; }
        public Guid? published_source_uuid { get; set; }
        public Guid? cohort_artist_uuid { get; set; }
        public bool approved { get; set; }
    }

    private sealed class CanonicalMatch
    {
        public Guid source_uuid { get; set; }
        public Guid artist_uuid { get; set; }
    }

    private sealed class ObservationEvidence
    {
        public Guid observation_id { get; set; }
        public string payload { get; set; } = "";
    }
}
