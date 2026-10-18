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
    private static readonly NormalizationCandidateUniverseKey[] IdentityCandidateWriteScopes =
    [
        NormalizationCandidateUniverseKey.Venues,
    ];

    public Task<NormalizationPublicationResult> PublishAsync(
        Guid workUuid,
        VenuePublication publication,
        CancellationToken cancellationToken = default
    ) =>
        work.TryPublishAsync(
            workUuid,
            (c, t, ct) => ApplyVenuePublicationAsync(c, t, workUuid, publication, ct),
            cancellationToken,
            candidateWriteScopes: IdentityCandidateWriteScopes
        );

    private static async Task ApplyVenuePublicationAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid workUuid,
        VenuePublication publication,
        CancellationToken ct,
        IReadOnlyList<NormalizedEvidenceReference>? authenticatedAcceptedEvidence = null
    )
    {
        await c.ExecuteAsync(
            new CommandDefinition(
                "SELECT pg_advisory_xact_lock_shared(hashtextextended('normalization:venues:identity',0))",
                transaction: t,
                cancellationToken: ct
            )
        );
        await ValidateVenuePublicationOwner(c, t, workUuid, publication, ct);
        await ValidateVenuePublicationEvidenceAsync(
            c,
            t,
            workUuid,
            publication,
            authenticatedAcceptedEvidence,
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
        var assertionChanges = await WriteNames(c, t, workUuid, publication.Names, created, ct);
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
    }

    private static async Task ValidateVenuePublicationEvidenceAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid workUuid,
        VenuePublication publication,
        IReadOnlyList<NormalizedEvidenceReference>? authenticatedAcceptedEvidence,
        CancellationToken ct
    )
    {
        var evidence = publication
            .Names.Select(n => n.Evidence)
            .Concat(publication.Locations.Select(l => l.Evidence))
            .Concat(publication.ExternalIdentities.Select(i => i.Evidence))
            .Concat(publication.ImportedVenues.SelectMany(a => a.Evidence))
            .Concat(publication.Shows.SelectMany(a => a.Evidence))
            .ToArray();
        if (authenticatedAcceptedEvidence == null)
        {
            await NormalizedPeopleService.ValidatePublicationEvidenceAsync(
                c,
                t,
                workUuid,
                "venues",
                evidence,
                ct
            );
        }
        else
        {
            // The dedicated resolver authenticates historical accepted candidate citations
            // through their owner clocks; they must not acquire a fabricated fresh input head.
            var context = await NormalizedPeopleService.ReadWorkContext(c, t, workUuid, ct);
            var sourceRevisions = context
                .Ticket.Inputs.Select(input => input.RevisionUuid)
                .ToHashSet();
            if (
                evidence.Any(citation =>
                    !sourceRevisions.Contains(citation.InputRevisionUuid)
                    && !authenticatedAcceptedEvidence.Contains(citation)
                )
            )
            {
                throw new NormalizationConflictException(
                    "Venue evidence is outside its consumed source inputs and authenticated candidate facts."
                );
            }
            await NormalizedPeopleService.ValidateEvidenceAsync(c, t, evidence, ct);
        }
    }

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
                    name.Wording,
                    name.Language,
                    name.Uuid,
                    ValidFrom = name.ValidFrom?.ToDateTime(TimeOnly.MinValue),
                    ValidTo = name.ValidTo?.ToDateTime(TimeOnly.MinValue),
                },
                name.Evidence,
                ct,
                exactFactPredicate: "wording=@Wording AND language IS NOT DISTINCT FROM @Language::text AND valid_from IS NOT DISTINCT FROM @ValidFrom::date AND valid_to IS NOT DISTINCT FROM @ValidTo::date",
                basis: name.Basis,
                assertionUuid: name.Uuid
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
        const string exactLocation = """
            original_locality=@OriginalLocality AND country IS NOT DISTINCT FROM @Country::text
                AND region IS NOT DISTINCT FROM @Region::text AND city IS NOT DISTINCT FROM @City::text
                AND address IS NOT DISTINCT FROM @Address::text AND latitude IS NOT DISTINCT FROM @Latitude::double precision
                AND longitude IS NOT DISTINCT FROM @Longitude::double precision AND coordinate_precision IS NOT DISTINCT FROM @CoordinatePrecision::text
                AND valid_from IS NOT DISTINCT FROM @ValidFrom::date AND valid_to IS NOT DISTINCT FROM @ValidTo::date
            """;
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
                $"({exactLocation}) OR uuid=@Uuid",
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
                ct,
                exactFactPredicate: exactLocation,
                basis: location.Basis,
                assertionUuid: location.Uuid
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
            cancellationToken,
            candidateWriteScopes: IdentityCandidateWriteScopes
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
            ct,
            exactFactPredicate: "provider=@Provider AND resource_type=@ResourceType AND provider_key=@ProviderKey",
            basis: identity.Basis,
            assertionUuid: identity.Uuid
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
        if (state == "accepted" && inserted == 0)
        {
            // One accepted row owns this qualified provider key. The exact replay was
            // handled above; silently ignoring this insert would discard different support.
            throw new NormalizationConflictException(
                "Provider identity already has different evidence or authority basis."
            );
        }
        return inserted;
    }
}
