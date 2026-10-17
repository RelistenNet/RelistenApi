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

public sealed partial class NormalizedPeopleService
{
    private static async Task ValidatePeoplePublicationOwner(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid workUuid,
        PeoplePublication publication,
        CancellationToken ct
    )
    {
        var context = await ReadWorkContext(c, t, workUuid, ct);
        if (context.Domain != "people")
        {
            throw new ArgumentException("People publication requires a people owner.");
        }
        if (context.Kind == "person")
        {
            var identities = publication
                .People.Select(p => p.Uuid)
                .Concat(publication.Aliases.Select(a => a.PersonUuid))
                .Concat(publication.ExternalIdentities.Select(i => i.PersonUuid));
            if (
                publication.Credits.Count != 0
                || publication.People.Count > 1
                || identities.Any(id => id != context.EntityUuid)
            )
            {
                throw new ArgumentException(
                    "A person owner publishes only its own identity, aliases and provider identities."
                );
            }
        }
        else if (context.Kind == "recording")
        {
            if (
                publication.People.Count != 0
                || publication.Aliases.Count != 0
                || publication.ExternalIdentities.Count != 0
                || publication.Credits.Any(c => c.SourceUuid != context.EntityUuid)
            )
            {
                throw new ArgumentException(
                    "A recording owner publishes only credits for its recording; publish people first."
                );
            }
            foreach (
                var personUuid in publication
                    .Credits.Where(c => c.PersonUuid.HasValue)
                    .Select(c => c.PersonUuid!.Value)
                    .Distinct()
            )
            {
                var resolved = await ResolvePersonUuidAsync(c, t, personUuid, ct);
                await ValidateIdentityDependency(
                    c,
                    t,
                    context.Ticket,
                    "people",
                    "person",
                    resolved,
                    ct
                );
            }
        }
        else
        {
            throw new ArgumentException("People publication owner must be person or recording.");
        }
    }

    internal static async Task<DomainWorkContext> ReadWorkContext(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid workUuid,
        CancellationToken ct
    )
    {
        var row = await c.QuerySingleAsync<WorkContextRow>(
            new CommandDefinition(
                """
                SELECT s.domain AS "Domain",s.subject_kind AS "Kind",s.subject_uuid AS "EntityUuid",r.ticket::text AS "TicketJson"
                FROM normalization.work_runs r JOIN normalization.work_subjects s ON s.uuid=r.subject_uuid WHERE r.uuid=@workUuid
                """,
                new { workUuid },
                t,
                cancellationToken: ct
            )
        );
        return new(
            row.Domain,
            row.Kind,
            row.EntityUuid,
            JsonSerializer.Deserialize<NormalizationWorkTicket>(row.TicketJson)!
        );
    }

    internal static async Task ValidateIdentityDependency(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationWorkTicket ticket,
        string domain,
        string kind,
        Guid identityUuid,
        CancellationToken ct
    )
    {
        var owner = await FindIdentityOwner(c, t, domain, kind, identityUuid, ct);
        if (!ticket.Dependencies.Any(d => d.SubjectUuid == owner))
        {
            throw new ArgumentException(
                $"Publication must consume the registered {kind} owner's accepted-state dependency."
            );
        }
    }

    internal static async Task<Guid> FindIdentityOwner(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        string domain,
        string kind,
        Guid identityUuid,
        CancellationToken ct
    )
    {
        var owner = await c.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                "SELECT uuid FROM normalization.work_subjects WHERE domain=@domain AND subject_kind=@kind AND subject_uuid=@identityUuid",
                new
                {
                    domain,
                    kind,
                    identityUuid,
                },
                t,
                cancellationToken: ct
            )
        );
        return owner
            ?? throw new ArgumentException(
                $"Register the {kind} owner with BeginWorkAsync before publishing or correcting this identity."
            );
    }

    private sealed class WorkContextRow
    {
        public string Domain { get; init; } = null!;
        public string Kind { get; init; } = null!;
        public Guid EntityUuid { get; init; }
        public string TicketJson { get; init; } = null!;
    }

    internal sealed record DomainWorkContext(
        string Domain,
        string Kind,
        Guid EntityUuid,
        NormalizationWorkTicket Ticket
    );
}
