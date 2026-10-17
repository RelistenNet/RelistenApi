using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedVenueService
{
    /// <summary>Corrects physical-venue metadata under its original owner, retaining prior claims and exact input evidence.</summary>
    public async Task<NormalizationReviewResult> ApplyMetadataDecisionAsync(
        VenueMetadataDecision command,
        string trustedOperator,
        CancellationToken cancellationToken = default
    )
    {
        var additionalOwners = await NormalizedPeopleService.FindMetadataReviewOwners(
            db,
            "venues",
            command.VenueUuid,
            command.OperationUuid,
            cancellationToken
        );
        var effectiveOwner =
            additionalOwners.Count == 0 ? command.SubjectUuid : additionalOwners[0];
        return await work.ApplyReviewAsync(
            new NormalizationReviewCommand(
                command.OperationUuid,
                command.SubjectUuid,
                command.ExpectedEditorialRevision,
                command.Action,
                JsonSerializer.SerializeToElement(command),
                command.Reason,
                trustedOperator,
                AdditionalSubjectUuids: additionalOwners
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
                await NormalizedPeopleService.LockMetadataIdentity(
                    c,
                    t,
                    "venues",
                    command.VenueUuid,
                    command.ExpectedVenueRevision,
                    effectiveOwner,
                    ct
                );
                await NormalizedPeopleService.ValidateMetadataReviewInputs(
                    c,
                    t,
                    command.SubjectUuid,
                    command.Action,
                    new[]
                    {
                        command.Name?.Evidence,
                        command.Location?.Evidence,
                        command.ExternalIdentity?.Evidence,
                        command.IdentityEvidence,
                    }
                        .OfType<NormalizedEvidenceReference>()
                        .ToArray(),
                    ct
                );
                await ApplyVenueMetadata(c, t, command, ct);
                await NormalizedPeopleService.BumpMetadataIdentity(
                    c,
                    t,
                    "venues",
                    command.VenueUuid,
                    ct
                );
            },
            cancellationToken,
            candidateWriteScopes: IdentityCandidateWriteScopes
        );
    }

    private static async Task ApplyVenueMetadata(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        VenueMetadataDecision command,
        CancellationToken ct
    )
    {
        if (
            new object?[]
            {
                command.Name,
                command.Location,
                command.ExternalIdentity,
                command.Identity,
            }.Count(x => x != null) != 1
        )
        {
            throw new ArgumentException(
                "A venue metadata decision changes exactly one typed claim."
            );
        }
        if (command.Name is { } name)
        {
            await ReviewVenueName(c, t, command, name, ct);
        }
        else if (command.Location is { } location)
        {
            await ReviewVenueLocation(c, t, command, location, ct);
        }
        else if (command.ExternalIdentity is { } external)
        {
            await ReviewVenueExternalIdentity(c, t, command, external, ct);
        }
        else
        {
            await ReviewVenueDisplayIdentity(c, t, command, ct);
        }
    }

    private static async Task ReviewVenueName(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        VenueMetadataDecision command,
        VenueNameAssertion name,
        CancellationToken ct
    )
    {
        if (name.VenueUuid != command.VenueUuid)
        {
            throw new ArgumentException("Name belongs to another venue.");
        }
        await NormalizedPeopleService.ValidateEvidenceAsync(c, t, [name.Evidence], ct);
        if (command.Action is not ("retract" or "reject"))
        {
            await NormalizedPeopleService.ValidateNameEvidenceAsync(
                c,
                t,
                name.Wording,
                name.Evidence,
                ct
            );
        }
        await NormalizedPeopleService.ReviewMetadataAssertion(
            c,
            t,
            "venue_names",
            "venue_uuid",
            command.VenueUuid,
            name.Uuid,
            "venue_uuid,wording,name_key,language,valid_from,valid_to",
            "@VenueUuid,@Wording,@key,@Language,@ValidFrom,@ValidTo",
            new DynamicParameters(
                new
                {
                    name.VenueUuid,
                    name.Wording,
                    key = NormalizedPeopleService.NameKey(name.Wording),
                    name.Language,
                    ValidFrom = name.ValidFrom?.ToDateTime(TimeOnly.MinValue),
                    ValidTo = name.ValidTo?.ToDateTime(TimeOnly.MinValue),
                }
            ),
            name.Evidence,
            command.Action,
            command.ExpectedAssertionRevision,
            command.OperationUuid,
            ct
        );
    }

    private static async Task ReviewVenueLocation(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        VenueMetadataDecision command,
        VenueLocationAssertion location,
        CancellationToken ct
    )
    {
        if (location.VenueUuid != command.VenueUuid)
        {
            throw new ArgumentException("Location belongs to another venue.");
        }
        await NormalizedPeopleService.ValidateEvidenceAsync(c, t, [location.Evidence], ct);
        if (command.Action is not ("retract" or "reject"))
        {
            await ValidateLocationEvidence(c, t, location, ct);
        }
        await NormalizedPeopleService.ReviewMetadataAssertion(
            c,
            t,
            "venue_locations",
            "venue_uuid",
            command.VenueUuid,
            location.Uuid,
            "venue_uuid,original_locality,country,region,city,address,latitude,longitude,coordinate_precision,valid_from,valid_to",
            "@VenueUuid,@OriginalLocality,@Country,@Region,@City,@Address,@Latitude,@Longitude,@CoordinatePrecision,@ValidFrom,@ValidTo",
            new DynamicParameters(
                new
                {
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
                }
            ),
            location.Evidence,
            command.Action,
            command.ExpectedAssertionRevision,
            command.OperationUuid,
            ct
        );
    }

    private static async Task ReviewVenueExternalIdentity(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        VenueMetadataDecision command,
        VenueExternalIdentity external,
        CancellationToken ct
    )
    {
        if (external.VenueUuid != command.VenueUuid || external.Uuid == null)
        {
            throw new ArgumentException(
                "External identity requires its assertion UUID and original venue owner."
            );
        }
        await NormalizedPeopleService.ValidateEvidenceAsync(c, t, [external.Evidence], ct);
        await NormalizedPeopleService.ReviewMetadataAssertion(
            c,
            t,
            "venue_external_identities",
            "venue_uuid",
            command.VenueUuid,
            external.Uuid.Value,
            "venue_uuid,provider,resource_type,provider_key",
            "@VenueUuid,@Provider,@ResourceType,@ProviderKey",
            new DynamicParameters(external),
            external.Evidence,
            command.Action,
            command.ExpectedAssertionRevision,
            command.OperationUuid,
            ct
        );
    }

    private static async Task ReviewVenueDisplayIdentity(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        VenueMetadataDecision command,
        CancellationToken ct
    )
    {
        var venue = command.Identity!;
        if (
            command.Action != "replace"
            || venue.Uuid != command.VenueUuid
            || command.IdentityEvidence == null
        )
        {
            throw new ArgumentException(
                "Identity display correction requires replace, the original UUID and name evidence."
            );
        }
        await NormalizedPeopleService.ValidateEvidenceAsync(c, t, [command.IdentityEvidence], ct);
        await NormalizedPeopleService.ValidateNameEvidenceAsync(
            c,
            t,
            venue.DisplayName,
            command.IdentityEvidence,
            ct
        );
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
        if (
            venue.Kind == "room"
            && await c.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    "SELECT EXISTS(SELECT 1 FROM normalization.canonical_venues WHERE parent_venue_uuid=@Uuid)",
                    venue,
                    t,
                    cancellationToken: ct
                )
            )
        )
        {
            throw new ArgumentException("A site with rooms cannot itself become a room.");
        }
        await c.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO normalization.canonical_venues_metadata_history SELECT *,@OperationUuid,@InputRevisionUuid,@JsonPointer FROM normalization.canonical_venues WHERE uuid=@Uuid;
                UPDATE normalization.canonical_venues SET display_name=@DisplayName,name_key=@key,kind=@Kind,parent_venue_uuid=@ParentVenueUuid,metadata_reviewed=true WHERE uuid=@Uuid
                """,
                new
                {
                    venue.Uuid,
                    venue.DisplayName,
                    venue.Kind,
                    venue.ParentVenueUuid,
                    key = NormalizedPeopleService.NameKey(venue.DisplayName),
                    command.OperationUuid,
                    command.IdentityEvidence.InputRevisionUuid,
                    command.IdentityEvidence.JsonPointer,
                },
                t,
                cancellationToken: ct
            )
        );
    }

    private static async Task ValidateLocationEvidence(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        VenueLocationAssertion location,
        CancellationToken ct
    )
    {
        var input = await c.QuerySingleAsync<NormalizationInputRevision>(
            new CommandDefinition(
                "SELECT uuid, payload, media_type AS \"MediaType\" FROM normalization.input_revisions WHERE uuid=@InputRevisionUuid",
                location.Evidence,
                t,
                cancellationToken: ct
            )
        );
        using var document = NormalizationRetainedDocument.Open(input, ct);
        var value = document.Resolve(location.Evidence.JsonPointer);
        if (
            value.ValueKind == JsonValueKind.String
            && location.Country == null
            && location.Region == null
            && location.City == null
            && location.Address == null
            && location.Latitude == null
            && location.Longitude == null
            && location.CoordinatePrecision == null
            && location.ValidFrom == null
            && location.ValidTo == null
        )
        {
            await NormalizedPeopleService.ValidateNameEvidenceAsync(
                c,
                t,
                location.OriginalLocality,
                location.Evidence,
                ct
            );
            return;
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "Structured location fields require an evidenced object with matching fields."
            );
        }
        foreach (
            var field in new[]
            {
                ("original_locality", location.OriginalLocality),
                ("country", location.Country),
                ("region", location.Region),
                ("city", location.City),
                ("address", location.Address),
                ("coordinate_precision", location.CoordinatePrecision),
                ("valid_from", location.ValidFrom?.ToString("yyyy-MM-dd")),
                ("valid_to", location.ValidTo?.ToString("yyyy-MM-dd")),
            }
        )
        {
            if (
                field.Item2 != null
                && (
                    !value.TryGetProperty(field.Item1, out var raw)
                    || raw.ValueKind != JsonValueKind.String
                    || raw.GetString() != field.Item2
                )
            )
            {
                throw new ArgumentException($"Location evidence does not support {field.Item1}.");
            }
        }
        foreach (
            var field in new[]
            {
                ("latitude", location.Latitude),
                ("longitude", location.Longitude),
            }
        )
        {
            if (
                field.Item2.HasValue
                && (
                    !value.TryGetProperty(field.Item1, out var raw)
                    || !raw.TryGetDouble(out var number)
                    || number != field.Item2.Value
                )
            )
            {
                throw new ArgumentException($"Location evidence does not support {field.Item1}.");
            }
        }
    }
}
