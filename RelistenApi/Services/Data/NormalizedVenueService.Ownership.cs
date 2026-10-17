using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedVenueService
{
    private static async Task ValidateVenuePublicationOwner(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid workUuid,
        VenuePublication publication,
        CancellationToken ct
    )
    {
        var context = await NormalizedPeopleService.ReadWorkContext(c, t, workUuid, ct);
        if (context.Domain != "venues")
        {
            throw new ArgumentException("Venue publication requires a venues owner.");
        }
        if (context.Kind == "venue")
        {
            var identities = publication
                .Venues.Select(v => v.Uuid)
                .Concat(publication.Names.Select(n => n.VenueUuid))
                .Concat(publication.Locations.Select(l => l.VenueUuid))
                .Concat(publication.ExternalIdentities.Select(i => i.VenueUuid));
            if (
                publication.ImportedVenues.Count != 0
                || publication.Shows.Count != 0
                || publication.Venues.Count > 1
                || identities.Any(id => id != context.EntityUuid)
            )
            {
                throw new ArgumentException(
                    "A venue owner publishes only its own identity, names, locations and provider identities."
                );
            }
            foreach (
                var parent in publication
                    .Venues.Where(v => v.ParentVenueUuid.HasValue)
                    .Select(v => v.ParentVenueUuid!.Value)
            )
            {
                await NormalizedPeopleService.ValidateIdentityDependency(
                    c,
                    t,
                    context.Ticket,
                    "venues",
                    "venue",
                    await ResolveVenueUuidAsync(c, t, parent, ct),
                    ct
                );
            }
        }
        else if (context.Kind is "imported_venue" or "show")
        {
            var assignments =
                context.Kind == "show" ? publication.Shows : publication.ImportedVenues;
            var otherAssignments =
                context.Kind == "show" ? publication.ImportedVenues : publication.Shows;
            if (
                publication.Venues.Count != 0
                || publication.Names.Count != 0
                || publication.Locations.Count != 0
                || publication.ExternalIdentities.Count != 0
                || otherAssignments.Count != 0
                || assignments.Any(a => a.CatalogUuid != context.EntityUuid)
            )
            {
                throw new ArgumentException(
                    "An imported-venue/show owner publishes only its catalog assignments; publish canonical venues first."
                );
            }
            foreach (var venue in assignments.Select(a => a.VenueUuid).Distinct())
            {
                await NormalizedPeopleService.ValidateIdentityDependency(
                    c,
                    t,
                    context.Ticket,
                    "venues",
                    "venue",
                    await ResolveVenueUuidAsync(c, t, venue, ct),
                    ct
                );
            }
        }
        else
        {
            throw new ArgumentException(
                "Venue publication owner must be venue, imported_venue or show."
            );
        }
    }
}
