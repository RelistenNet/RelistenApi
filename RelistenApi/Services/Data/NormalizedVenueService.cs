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

/// <summary>Physical sites and rooms remain distinct from artist-scoped imported venue labels.</summary>
public sealed partial class NormalizedVenueService(DbService db, NormalizationWorkService work)
{
    public Task<NormalizationPublicationResult> PublishAsync(
        Guid workUuid,
        VenuePublication publication,
        CancellationToken cancellationToken = default
    ) =>
        work.TryPublishAsync(
            workUuid,
            async (c, t, ct) =>
            {
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "SELECT pg_advisory_xact_lock_shared(hashtextextended('normalization:venues:identity',0))",
                        transaction: t,
                        cancellationToken: ct
                    )
                );
                await ValidateVenuePublicationOwner(c, t, workUuid, publication, ct);
                var evidence = publication
                    .Names.Select(n => n.Evidence)
                    .Concat(publication.Locations.Select(l => l.Evidence))
                    .Concat(publication.ExternalIdentities.Select(i => i.Evidence))
                    .Concat(publication.ImportedVenues.SelectMany(a => a.Evidence))
                    .Concat(publication.Shows.SelectMany(a => a.Evidence))
                    .ToArray();
                await NormalizedPeopleService.ValidatePublicationEvidenceAsync(
                    c,
                    t,
                    workUuid,
                    "venues",
                    evidence,
                    ct
                );
                var created = new HashSet<Guid>();
                foreach (var venue in publication.Venues.OrderBy(v => v.ParentVenueUuid.HasValue))
                {
                    if (
                        !await c.ExecuteScalarAsync<bool>(
                            new CommandDefinition(
                                "SELECT EXISTS(SELECT 1 FROM normalization.canonical_venues WHERE uuid=@Uuid)",
                                venue,
                                t,
                                cancellationToken: ct
                            )
                        )
                        && !publication.Names.Any(n =>
                            n.VenueUuid == venue.Uuid
                            && NormalizedPeopleService.NameKey(n.Wording)
                                == NormalizedPeopleService.NameKey(venue.DisplayName)
                        )
                    )
                    {
                        throw new ArgumentException(
                            "Automatic venue creation requires an evidenced display-name assertion."
                        );
                    }
                    if (await InsertVenue(c, t, venue, ct))
                    {
                        created.Add(venue.Uuid);
                    }
                }
                var assertionChanges = await WriteNames(
                    c,
                    t,
                    workUuid,
                    publication.Names,
                    created,
                    ct
                );
                assertionChanges += await WriteLocations(
                    c,
                    t,
                    workUuid,
                    publication.Locations,
                    created,
                    ct
                );
                foreach (var identity in publication.ExternalIdentities)
                {
                    if (
                        !created.Contains(identity.VenueUuid)
                        && !await c.ExecuteScalarAsync<bool>(
                            new CommandDefinition(
                                "SELECT EXISTS(SELECT 1 FROM normalization.venue_external_identities WHERE provider=@Provider AND resource_type=@ResourceType AND provider_key=@ProviderKey)",
                                identity,
                                t,
                                cancellationToken: ct
                            )
                        )
                    )
                    {
                        await NormalizedPeopleService.ValidateWorkOwnerAsync(
                            c,
                            t,
                            workUuid,
                            "venues",
                            "venue",
                            identity.VenueUuid,
                            ct
                        );
                    }
                    assertionChanges += await WriteExternalIdentity(c, t, identity, ct);
                }
                if (assertionChanges > 0)
                {
                    var owner = await NormalizedPeopleService.ReadWorkContext(c, t, workUuid, ct);
                    if (!created.Contains(owner.EntityUuid))
                    {
                        await c.ExecuteAsync(
                            new CommandDefinition(
                                "UPDATE normalization.canonical_venues SET revision=revision+1,updated_at=clock_timestamp() WHERE uuid=@uuid",
                                new { uuid = owner.EntityUuid },
                                t,
                                cancellationToken: ct
                            )
                        );
                    }
                }
                foreach (var assignment in publication.ImportedVenues)
                {
                    await WriteAutomaticAssignment(c, t, assignment, false, workUuid, ct);
                }
                foreach (var assignment in publication.Shows)
                {
                    await WriteAutomaticAssignment(c, t, assignment, true, workUuid, ct);
                }
            },
            cancellationToken
        );

    private static async Task<int> WriteNames(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid workUuid,
        IReadOnlyList<VenueNameAssertion> assertions,
        HashSet<Guid> created,
        CancellationToken ct
    )
    {
        var inserted = 0;
        foreach (var originalName in assertions)
        {
            var name = originalName;
            NormalizedPeopleService.ValidateAutomaticMetadataBasis(name.Basis);
            await NormalizedPeopleService.ValidateNameEvidenceAsync(
                c,
                t,
                name.Wording,
                name.Evidence,
                ct
            );
            if (
                !created.Contains(name.VenueUuid)
                && !await c.ExecuteScalarAsync<bool>(
                    new CommandDefinition(
                        "SELECT EXISTS(SELECT 1 FROM normalization.venue_names WHERE uuid=@Uuid)",
                        name,
                        t,
                        cancellationToken: ct
                    )
                )
            )
            {
                await NormalizedPeopleService.ValidateWorkOwnerAsync(
                    c,
                    t,
                    workUuid,
                    "venues",
                    "venue",
                    name.VenueUuid,
                    ct
                );
            }
            var state = await NormalizedPeopleService.AutomaticMetadataState(
                c,
                t,
                "venue_names",
                "venue_uuid",
                name.VenueUuid,
                "(name_key=@key AND language IS NOT DISTINCT FROM @Language::text AND valid_from IS NOT DISTINCT FROM @ValidFrom::date AND valid_to IS NOT DISTINCT FROM @ValidTo::date) OR uuid=@Uuid",
                new
                {
                    key = NormalizedPeopleService.NameKey(name.Wording),
                    name.Language,
                    name.Uuid,
                    ValidFrom = name.ValidFrom?.ToDateTime(TimeOnly.MinValue),
                    ValidTo = name.ValidTo?.ToDateTime(TimeOnly.MinValue),
                },
                name.Evidence,
                ct
            );
            if (state == null)
            {
                continue;
            }
            if (state == "pending")
            {
                name = name with { Uuid = Guid.NewGuid() };
            }
            inserted += await c.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO normalization.venue_names(uuid,venue_uuid,wording,name_key,language,valid_from,valid_to,input_revision_uuid,json_pointer,start_code_point,end_code_point,quote,state,basis)
                    VALUES(@Uuid,@VenueUuid,@Wording,@key,@Language,@ValidFrom,@ValidTo,@InputRevisionUuid,@JsonPointer,@StartCodePoint,@EndCodePoint,@Quote,@state,@Basis) ON CONFLICT(uuid) DO NOTHING
                    """,
                    new
                    {
                        state,
                        name.Basis,
                        name.Uuid,
                        name.VenueUuid,
                        name.Wording,
                        key = NormalizedPeopleService.NameKey(name.Wording),
                        name.Language,
                        ValidFrom = name.ValidFrom?.ToDateTime(TimeOnly.MinValue),
                        ValidTo = name.ValidTo?.ToDateTime(TimeOnly.MinValue),
                        name.Evidence.InputRevisionUuid,
                        name.Evidence.JsonPointer,
                        name.Evidence.StartCodePoint,
                        name.Evidence.EndCodePoint,
                        name.Evidence.Quote,
                    },
                    t,
                    cancellationToken: ct
                )
            );
        }
        return inserted;
    }

    private static async Task<int> WriteLocations(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid workUuid,
        IReadOnlyList<VenueLocationAssertion> assertions,
        HashSet<Guid> created,
        CancellationToken ct
    )
    {
        var inserted = 0;
        foreach (var originalLocation in assertions)
        {
            var location = originalLocation;
            NormalizedPeopleService.ValidateAutomaticMetadataBasis(location.Basis);
            if (
                !created.Contains(location.VenueUuid)
                && !await c.ExecuteScalarAsync<bool>(
                    new CommandDefinition(
                        "SELECT EXISTS(SELECT 1 FROM normalization.venue_locations WHERE uuid=@Uuid)",
                        location,
                        t,
                        cancellationToken: ct
                    )
                )
            )
            {
                await NormalizedPeopleService.ValidateWorkOwnerAsync(
                    c,
                    t,
                    workUuid,
                    "venues",
                    "venue",
                    location.VenueUuid,
                    ct
                );
            }
            var state = await NormalizedPeopleService.AutomaticMetadataState(
                c,
                t,
                "venue_locations",
                "venue_uuid",
                location.VenueUuid,
                """
                (original_locality=@OriginalLocality AND country IS NOT DISTINCT FROM @Country::text
                    AND region IS NOT DISTINCT FROM @Region::text AND city IS NOT DISTINCT FROM @City::text
                    AND address IS NOT DISTINCT FROM @Address::text AND latitude IS NOT DISTINCT FROM @Latitude::double precision
                    AND longitude IS NOT DISTINCT FROM @Longitude::double precision AND coordinate_precision IS NOT DISTINCT FROM @CoordinatePrecision::text
                    AND valid_from IS NOT DISTINCT FROM @ValidFrom::date AND valid_to IS NOT DISTINCT FROM @ValidTo::date) OR uuid=@Uuid
                """,
                new
                {
                    location.Uuid,
                    location.OriginalLocality,
                    location.Country,
                    location.Region,
                    location.City,
                    location.Address,
                    location.Latitude,
                    location.Longitude,
                    location.CoordinatePrecision,
                    ValidFrom = location.ValidFrom?.ToDateTime(TimeOnly.MinValue),
                    ValidTo = location.ValidTo?.ToDateTime(TimeOnly.MinValue),
                },
                location.Evidence,
                ct
            );
            if (state == null)
            {
                continue;
            }
            if (state == "pending")
            {
                location = location with { Uuid = Guid.NewGuid() };
            }
            inserted += await c.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO normalization.venue_locations(uuid,venue_uuid,original_locality,country,region,city,address,latitude,longitude,coordinate_precision,valid_from,valid_to,input_revision_uuid,json_pointer,start_code_point,end_code_point,quote,state,basis)
                    VALUES(@Uuid,@VenueUuid,@OriginalLocality,@Country,@Region,@City,@Address,@Latitude,@Longitude,@CoordinatePrecision,@ValidFrom,@ValidTo,@InputRevisionUuid,@JsonPointer,@StartCodePoint,@EndCodePoint,@Quote,@state,@Basis) ON CONFLICT(uuid) DO NOTHING
                    """,
                    new
                    {
                        state,
                        location.Basis,
                        location.Uuid,
                        location.VenueUuid,
                        location.OriginalLocality,
                        location.Country,
                        location.Region,
                        location.City,
                        location.Address,
                        location.Latitude,
                        location.Longitude,
                        location.CoordinatePrecision,
                        ValidFrom = location.ValidFrom?.ToDateTime(TimeOnly.MinValue),
                        ValidTo = location.ValidTo?.ToDateTime(TimeOnly.MinValue),
                        location.Evidence.InputRevisionUuid,
                        location.Evidence.JsonPointer,
                        location.Evidence.StartCodePoint,
                        location.Evidence.EndCodePoint,
                        location.Evidence.Quote,
                    },
                    t,
                    cancellationToken: ct
                )
            );
        }
        return inserted;
    }

    public Task<NormalizationReviewResult> ApplyDecisionAsync(
        VenueAssignmentDecision command,
        string trustedOperator,
        CancellationToken cancellationToken = default
    ) =>
        work.ApplyReviewAsync(
            new NormalizationReviewCommand(
                command.OperationUuid,
                command.SubjectUuid,
                command.ExpectedEditorialRevision,
                command.Action,
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
                    command.IsShow ? "show" : "imported_venue",
                    command.Assignment.CatalogUuid,
                    ct
                );
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "SELECT pg_advisory_xact_lock_shared(hashtextextended('normalization:venues:identity',0))",
                        transaction: t,
                        cancellationToken: ct
                    )
                );
                await NormalizedPeopleService.ValidateEvidenceAsync(
                    c,
                    t,
                    command.Assignment.Evidence,
                    ct
                );
                var state = command.Action switch
                {
                    "accept" or "replace" => "accepted",
                    "reject" => "rejected",
                    "retract" => "superseded",
                    _ => throw new ArgumentException("Unsupported venue assignment action."),
                };
                await WriteAssignment(
                    c,
                    t,
                    command.Assignment with
                    {
                        State = state,
                        Basis = "reviewed",
                    },
                    command.IsShow,
                    command.OperationUuid,
                    true,
                    command.ExpectedAssignmentRevision,
                    ct
                );
            },
            cancellationToken
        );

    public Task<IReadOnlyList<CanonicalVenue>> FindCandidatesAsync(
        string name,
        CancellationToken cancellationToken = default
    ) =>
        db.WithWriteConnection<IReadOnlyList<CanonicalVenue>>(async c =>
            (
                await c.QueryAsync<CanonicalVenue>(
                    new CommandDefinition(
                        """
                        SELECT v.uuid AS "Uuid",v.display_name AS "DisplayName",v.kind AS "Kind",COALESCE((SELECT resolved_uuid FROM normalization.venue_identity_resolution WHERE original_uuid=v.parent_venue_uuid),v.parent_venue_uuid) AS "ParentVenueUuid" FROM normalization.canonical_venues v
                        WHERE NOT EXISTS(SELECT 1 FROM normalization.venue_redirects r WHERE r.retired_uuid=v.uuid)
                          AND (EXISTS(SELECT 1 FROM normalization.venue_identity_resolution r JOIN normalization.canonical_venues original ON original.uuid=r.original_uuid WHERE r.resolved_uuid=v.uuid AND original.name_key=@key) OR EXISTS(SELECT 1 FROM normalization.resolved_venue_names n WHERE n.resolved_venue_uuid=v.uuid AND n.name_key=@key))
                        ORDER BY v.uuid LIMIT 200
                        """,
                        new { key = NormalizedPeopleService.NameKey(name) },
                        cancellationToken: cancellationToken
                    )
                )
            ).ToArray()
        );

    public static async Task<Guid> ResolveVenueUuidAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid uuid,
        CancellationToken ct = default
    )
    {
        var resolved = await c.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                """
                WITH RECURSIVE chain(uuid) AS (SELECT uuid FROM normalization.canonical_venues WHERE uuid=@uuid
                    UNION ALL SELECT r.survivor_uuid FROM chain c JOIN normalization.venue_redirects r ON r.retired_uuid=c.uuid)
                SELECT uuid FROM chain c WHERE NOT EXISTS(SELECT 1 FROM normalization.venue_redirects r WHERE r.retired_uuid=c.uuid)
                """,
                new { uuid },
                t,
                cancellationToken: ct
            )
        );
        return resolved ?? throw new ArgumentException("Physical venue identity does not exist.");
    }

    private static async Task<bool> InsertVenue(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        CanonicalVenue venue,
        CancellationToken ct
    )
    {
        if (
            await c.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    "SELECT EXISTS(SELECT 1 FROM normalization.canonical_venues WHERE uuid=@Uuid AND metadata_reviewed)",
                    venue,
                    t,
                    cancellationToken: ct
                )
            )
        )
        {
            // A provider refresh may repeat the identity shape that the operator corrected.
            return false;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(venue.DisplayName);
        if (
            venue.ParentVenueUuid.HasValue
            && !await c.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    "SELECT EXISTS(SELECT 1 FROM normalization.canonical_venues WHERE uuid=@ParentVenueUuid AND kind='site')",
                    venue,
                    t,
                    cancellationToken: ct
                )
            )
        )
        {
            throw new ArgumentException("A room parent must be an existing physical site.");
        }
        var inserted = await c.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO normalization.canonical_venues(uuid,display_name,name_key,kind,parent_venue_uuid) VALUES(@Uuid,@DisplayName,@key,@Kind,@ParentVenueUuid) ON CONFLICT(uuid) DO NOTHING",
                new
                {
                    venue.Uuid,
                    venue.DisplayName,
                    key = NormalizedPeopleService.NameKey(venue.DisplayName),
                    venue.Kind,
                    venue.ParentVenueUuid,
                },
                t,
                cancellationToken: ct
            )
        );
        if (
            !await c.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    "SELECT metadata_reviewed OR (display_name=@DisplayName AND kind=@Kind AND parent_venue_uuid IS NOT DISTINCT FROM @ParentVenueUuid::uuid) FROM normalization.canonical_venues WHERE uuid=@Uuid",
                    venue,
                    t,
                    cancellationToken: ct
                )
            )
        )
        {
            throw new NormalizationConflictException("Venue UUID already has a different meaning.");
        }
        return inserted == 1;
    }

    private static async Task<int> WriteExternalIdentity(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        VenueExternalIdentity identity,
        CancellationToken ct
    )
    {
        NormalizedPeopleService.ValidateAutomaticMetadataBasis(identity.Basis);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ResourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ProviderKey);
        var state = await NormalizedPeopleService.AutomaticMetadataState(
            c,
            t,
            "venue_external_identities",
            "venue_uuid",
            identity.VenueUuid,
            "provider=@Provider AND resource_type=@ResourceType AND provider_key=@ProviderKey",
            identity,
            identity.Evidence,
            ct
        );
        if (state == null)
        {
            return 0;
        }
        var inserted = await c.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO normalization.venue_external_identities(uuid,venue_uuid,provider,resource_type,provider_key,input_revision_uuid,json_pointer,start_code_point,end_code_point,quote,state,basis)
                VALUES(@uuid,@VenueUuid,@Provider,@ResourceType,@ProviderKey,@InputRevisionUuid,@JsonPointer,@StartCodePoint,@EndCodePoint,@Quote,@state,@Basis) ON CONFLICT DO NOTHING
                """,
                new
                {
                    uuid = state == "pending" ? Guid.NewGuid() : identity.Uuid ?? Guid.NewGuid(),
                    identity.Basis,
                    identity.VenueUuid,
                    identity.Provider,
                    identity.ResourceType,
                    identity.ProviderKey,
                    identity.Evidence.InputRevisionUuid,
                    identity.Evidence.JsonPointer,
                    identity.Evidence.StartCodePoint,
                    identity.Evidence.EndCodePoint,
                    identity.Evidence.Quote,
                    state,
                },
                t,
                cancellationToken: ct
            )
        );
        var owner = await c.ExecuteScalarAsync<Guid?>(
            new CommandDefinition(
                "SELECT venue_uuid FROM normalization.venue_external_identities WHERE provider=@Provider AND resource_type=@ResourceType AND provider_key=@ProviderKey AND state='accepted'",
                identity,
                t,
                cancellationToken: ct
            )
        );
        if (
            state == "accepted"
            && owner.HasValue
            && await ResolveVenueUuidAsync(c, t, owner.Value, ct)
                != await ResolveVenueUuidAsync(c, t, identity.VenueUuid, ct)
        )
        {
            throw new NormalizationConflictException(
                "Provider identity already belongs to a different physical venue."
            );
        }
        return inserted;
    }

    private static Task WriteAutomaticAssignment(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        VenueAssignment assignment,
        bool isShow,
        Guid workUuid,
        CancellationToken ct
    )
    {
        if (assignment.Basis == "reviewed")
        {
            throw new ArgumentException("Automatic publication cannot claim reviewed authority.");
        }
        return WriteAssignment(c, t, assignment, isShow, workUuid, false, 0, ct);
    }

    private static async Task WriteAssignment(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        VenueAssignment assignment,
        bool isShow,
        Guid decisionUuid,
        bool reviewed,
        long expectedRevision,
        CancellationToken ct
    )
    {
        if (assignment.Evidence.Count == 0)
        {
            throw new ArgumentException("Venue assignments require evidence.");
        }
        assignment = assignment with
        {
            VenueUuid = await ResolveVenueUuidAsync(c, t, assignment.VenueUuid, ct),
        };
        // Only these two typed relations are selected here; callers never supply table or column names.
        var table = isShow ? "show_venue_assignments" : "imported_venue_assignments";
        var catalogColumn = isShow ? "show_uuid" : "imported_venue_uuid";
        var evidenceColumn = isShow ? "show_assignment_uuid" : "imported_assignment_uuid";
        await c.ExecuteAsync(
            new CommandDefinition(
                "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",
                new { key = $"normalization:{table}:{assignment.CatalogUuid}" },
                t,
                cancellationToken: ct
            )
        );
        var current = await c.QuerySingleOrDefaultAsync<AssignmentRow>(
            new CommandDefinition(
                $"SELECT uuid AS \"Uuid\",{catalogColumn} AS \"CatalogUuid\",revision AS \"Revision\",basis AS \"Basis\",state AS \"State\",canonical_venue_uuid AS \"VenueUuid\" FROM normalization.{table} WHERE uuid=@Uuid FOR UPDATE",
                assignment,
                t,
                cancellationToken: ct
            )
        );
        if (!reviewed)
        {
            var prior = await c.QuerySingleOrDefaultAsync<AssignmentRow>(
                new CommandDefinition(
                    $"""
                    SELECT uuid AS "Uuid",{catalogColumn} AS "CatalogUuid",revision AS "Revision",basis AS "Basis",state AS "State",canonical_venue_uuid AS "VenueUuid"
                    FROM normalization.{table} WHERE {catalogColumn}=@CatalogUuid AND canonical_venue_uuid=@VenueUuid AND state<>'superseded'
                    ORDER BY (basis='reviewed') DESC,(state='accepted') DESC,uuid LIMIT 1 FOR UPDATE
                    """,
                    assignment,
                    t,
                    cancellationToken: ct
                )
            );
            if (prior?.Basis == "reviewed" || current?.Basis == "reviewed")
            {
                prior ??= current;
                var sameEvidence = await c.ExecuteScalarAsync<bool>(
                    new CommandDefinition(
                        $"SELECT EXISTS(SELECT 1 FROM normalization.venue_assignment_evidence WHERE {evidenceColumn}=@uuid AND input_revision_uuid=ANY(@revisions))",
                        new
                        {
                            uuid = prior!.Uuid,
                            revisions = assignment
                                .Evidence.Select(e => e.InputRevisionUuid)
                                .ToArray(),
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                if (sameEvidence)
                {
                    return;
                }
                assignment = assignment with
                {
                    Uuid = Guid.NewGuid(),
                    State = "pending",
                    ReasonCode = "changed_evidence_reviewed_claim",
                };
                current = null;
            }
            else if (prior != null)
            {
                assignment = assignment with { Uuid = prior.Uuid };
                current = prior;
            }
        }
        if (reviewed && (current?.Revision ?? 0) != expectedRevision)
        {
            throw new NormalizationConflictException(
                "Venue assignment changed after review was prepared."
            );
        }
        if (current == null)
        {
            var catalogTable = isShow ? "shows" : "venues";
            if (
                !await c.ExecuteScalarAsync<bool>(
                    new CommandDefinition(
                        $"SELECT EXISTS(SELECT 1 FROM {catalogTable} WHERE uuid=@CatalogUuid)",
                        assignment,
                        t,
                        cancellationToken: ct
                    )
                )
            )
            {
                throw new ArgumentException("Venue assignment catalog reference is unavailable.");
            }
        }
        else if (current.CatalogUuid != assignment.CatalogUuid)
        {
            throw new NormalizationConflictException(
                "An assignment UUID cannot be rebound to a different catalog object."
            );
        }
        var accepted = await c.QuerySingleOrDefaultAsync<AssignmentRow>(
            new CommandDefinition(
                $"SELECT uuid AS \"Uuid\",{catalogColumn} AS \"CatalogUuid\",revision AS \"Revision\",basis AS \"Basis\",state AS \"State\",canonical_venue_uuid AS \"VenueUuid\" FROM normalization.{table} WHERE {catalogColumn}=@CatalogUuid AND state='accepted' AND uuid<>@Uuid FOR UPDATE",
                assignment,
                t,
                cancellationToken: ct
            )
        );
        if (accepted != null && assignment.State == "accepted")
        {
            if (reviewed)
            {
                // Superseding retains the old row and its evidence for inspection and replay.
                await c.ExecuteAsync(
                    new CommandDefinition(
                        $"INSERT INTO normalization.{table}_history SELECT *,@decisionUuid FROM normalization.{table} WHERE uuid=@uuid; UPDATE normalization.{table} SET state='superseded',revision=revision+1,updated_at=clock_timestamp() WHERE uuid=@uuid",
                        new { uuid = accepted.Uuid, decisionUuid },
                        t,
                        cancellationToken: ct
                    )
                );
            }
            else
            {
                if (
                    accepted.Basis == "reviewed"
                    && await c.ExecuteScalarAsync<bool>(
                        new CommandDefinition(
                            $"SELECT EXISTS(SELECT 1 FROM normalization.venue_assignment_evidence WHERE {evidenceColumn}=@uuid AND input_revision_uuid=ANY(@revisions))",
                            new
                            {
                                uuid = accepted.Uuid,
                                revisions = assignment
                                    .Evidence.Select(e => e.InputRevisionUuid)
                                    .ToArray(),
                            },
                            t,
                            cancellationToken: ct
                        )
                    )
                )
                {
                    return;
                }
                assignment = assignment with
                {
                    State = "pending",
                    ReasonCode = "conflicting_evidence",
                };
            }
        }
        if (
            !reviewed
            && assignment.State == "pending"
            && await c.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    $"""
                    SELECT EXISTS(SELECT 1 FROM normalization.{table} a
                        WHERE a.{catalogColumn}=@CatalogUuid AND a.canonical_venue_uuid=@VenueUuid AND a.state='pending'
                          AND (SELECT coalesce(array_agg(DISTINCT e.input_revision_uuid),ARRAY[]::uuid[]) FROM normalization.venue_assignment_evidence e
                               WHERE e.{evidenceColumn}=a.uuid AND e.assignment_revision=a.revision) @> @revisions::uuid[]
                          AND (SELECT coalesce(array_agg(DISTINCT e.input_revision_uuid),ARRAY[]::uuid[]) FROM normalization.venue_assignment_evidence e
                               WHERE e.{evidenceColumn}=a.uuid AND e.assignment_revision=a.revision) <@ @revisions::uuid[])
                    """,
                    new
                    {
                        assignment.CatalogUuid,
                        assignment.VenueUuid,
                        revisions = assignment
                            .Evidence.Select(e => e.InputRevisionUuid)
                            .Distinct()
                            .ToArray(),
                    },
                    t,
                    cancellationToken: ct
                )
            )
        )
        {
            return;
        }
        if (current != null)
        {
            await c.ExecuteAsync(
                new CommandDefinition(
                    $"INSERT INTO normalization.{table}_history SELECT *,@decisionUuid FROM normalization.{table} WHERE uuid=@Uuid",
                    new { assignment.Uuid, decisionUuid },
                    t,
                    cancellationToken: ct
                )
            );
        }
        await c.ExecuteAsync(
            new CommandDefinition(
                $"""
                INSERT INTO normalization.{table}(uuid,{catalogColumn},canonical_venue_uuid,state,basis,reason_code,decision_uuid)
                VALUES(@Uuid,@CatalogUuid,@VenueUuid,@State,@Basis,@ReasonCode,@decisionUuid)
                ON CONFLICT(uuid) DO UPDATE SET canonical_venue_uuid=EXCLUDED.canonical_venue_uuid,state=EXCLUDED.state,basis=EXCLUDED.basis,reason_code=EXCLUDED.reason_code,
                    decision_uuid=EXCLUDED.decision_uuid,revision={table}.revision+1,updated_at=clock_timestamp()
                """,
                new
                {
                    assignment.Uuid,
                    assignment.CatalogUuid,
                    assignment.VenueUuid,
                    assignment.State,
                    assignment.Basis,
                    assignment.ReasonCode,
                    decisionUuid,
                },
                t,
                cancellationToken: ct
            )
        );
        foreach (var e in assignment.Evidence)
        {
            await c.ExecuteAsync(
                new CommandDefinition(
                    $"INSERT INTO normalization.venue_assignment_evidence({evidenceColumn},input_revision_uuid,json_pointer,start_code_point,end_code_point,quote,assignment_revision) SELECT @Uuid,@InputRevisionUuid,@JsonPointer,@StartCodePoint,@EndCodePoint,@Quote,revision FROM normalization.{table} WHERE uuid=@Uuid",
                    new
                    {
                        assignment.Uuid,
                        e.InputRevisionUuid,
                        e.JsonPointer,
                        e.StartCodePoint,
                        e.EndCodePoint,
                        e.Quote,
                    },
                    t,
                    cancellationToken: ct
                )
            );
        }
    }

    private sealed class AssignmentRow
    {
        public Guid Uuid { get; init; }
        public Guid CatalogUuid { get; init; }
        public long Revision { get; init; }
        public Guid VenueUuid { get; init; }
        public string Basis { get; init; } = null!;
        public string State { get; init; } = null!;
    }
}
