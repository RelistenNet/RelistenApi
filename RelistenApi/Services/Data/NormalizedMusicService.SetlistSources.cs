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

public sealed partial class NormalizedMusicService
{
    /// <summary>Loads the exact saved source receipts and complete original binding clocks, never newer substitutes.</summary>
    public async Task<SetlistStructureReadSet> GetSetlistStructureReadSetAsync(
        SetlistStructureChange structure,
        CancellationToken cancellationToken = default
    )
    {
        var frozen = FreezeStructure(structure);
        if (frozen.Sources.Count is 0 or > 100)
        {
            throw new ArgumentException("A structure needs one to 100 source accounts.");
        }
        await using var c = db.CreateConnection(false, false);
        await c.OpenAsync(cancellationToken);
        await using var t = await c.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken
        );
        await c.ExecuteAsync(
            new CommandDefinition(
                "SET TRANSACTION READ ONLY",
                transaction: t,
                cancellationToken: cancellationToken
            )
        );
        var batches = (
            await ReadStructureProposals(c, t, frozen, cancellationToken)
        ).Values.ToArray();
        var inputs = batches
            .SelectMany(batch => batch.Ticket.Inputs)
            .Distinct()
            .OrderBy(input => input.ResourceUuid)
            .ToArray();
        var catalogs = batches
            .SelectMany(batch => batch.Ticket.CatalogInputs ?? [])
            .Distinct()
            .OrderBy(input => input.ArtistUuid)
            .ToArray();
        var proposals = batches
            .Select(batch => new NormalizationSubjectDependency(
                batch.Ticket.SubjectUuid,
                batch.Ticket.DesiredGeneration,
                batch.Ticket.EditorialRevision,
                batch.AttemptUuid
            ))
            .Distinct()
            .ToArray();
        var registries = batches.Select(batch => batch.Ticket.MappingRegistry).Distinct().ToArray();
        if (
            inputs.GroupBy(input => input.ResourceUuid).Any(group => group.Count() > 1)
            || catalogs.GroupBy(input => input.ArtistUuid).Any(group => group.Count() > 1)
            || proposals.GroupBy(input => input.SubjectUuid).Any(group => group.Count() > 1)
            || registries.Length != 1
        )
        {
            throw new NormalizationConflictException(
                "Source proposals have incompatible original read sets; refresh the complete proposal set."
            );
        }
        var result = new SetlistStructureReadSet(
            batches
                .SelectMany(batch => batch.Preparation.Inputs)
                .Select(input => input.Input)
                .Distinct()
                .ToArray(),
            inputs,
            proposals,
            catalogs,
            registries[0]
        );
        await t.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<
        Dictionary<Guid, NormalizationSetlistProposalBatch>
    > ReadStructureProposals(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistStructureChange structure,
        CancellationToken ct
    )
    {
        var attempts = structure
            .Sources.Select(source => source.ProposalAttemptUuid)
            .Distinct()
            .ToArray();
        var summary = await c.QuerySingleAsync<StructureProposalBound>(
            new CommandDefinition(
                """
                SELECT count(*)::integer AS Count,COALESCE(sum(octet_length(a.batch::text)),0)::bigint AS Bytes
                FROM normalization.setlist_processing_attempts a JOIN normalization.work_runs w ON w.uuid=a.extraction_work_uuid
                WHERE a.attempt_uuid=ANY(@attempts) AND a.disposition='published' AND w.state='published'
                """,
                new { attempts },
                t,
                cancellationToken: ct
            )
        );
        if (summary.Count != attempts.Length || summary.Bytes > 32 * 1024 * 1024)
        {
            throw new ArgumentException(
                "Structure sources require published proposal batches totaling at most 32 MiB."
            );
        }
        var rows = await c.QueryAsync<string>(
            new CommandDefinition(
                "SELECT batch::text FROM normalization.setlist_processing_attempts WHERE attempt_uuid=ANY(@attempts)",
                new { attempts },
                t,
                cancellationToken: ct
            )
        );
        var batches = rows.Select(json =>
                JsonSerializer.Deserialize<NormalizationSetlistProposalBatch>(json)!
            )
            .ToArray();
        if (
            batches.Any(batch =>
                batch.ProcessingVersion != NormalizationSetlistProcessor.Version
                || batch.Ticket.PolicyVersion != batch.ProcessingVersion
                || !NormalizationSetlistReadSet.IsComplete(batch.Ticket, batch.Preparation)
            )
        )
        {
            throw new NotSupportedException(
                "Source proposals require the current setlist processor and its complete supported read set; reprocess historical or richer proposals before acceptance."
            );
        }
        return batches.ToDictionary(batch => batch.AttemptUuid);
    }

    private static async Task ValidateStructureSources(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistStructureChange structure,
        bool reviewed,
        CancellationToken ct
    )
    {
        if (
            structure
                .Sources.Select(source =>
                    (source.ProposalAttemptUuid, source.InputRevisionUuid, source.AccountIndex)
                )
                .Distinct()
                .Count() != structure.Sources.Count
        )
        {
            throw new ArgumentException(
                "Each source account observation appears exactly once in a complete structure."
            );
        }
        var batches = await ReadStructureProposals(c, t, structure, ct);
        var identity = new NormalizationResourceKey(
            structure.Edition.Provider,
            structure.Edition.ResourceType,
            structure.Edition.ProviderKey
        );
        foreach (var batch in batches.Values)
        {
            foreach (var extraction in batch.Extractions)
            {
                for (var index = 0; index < extraction.Accounts.Count; index++)
                {
                    if (
                        extraction.Accounts[index].Account.Origin.Item == identity
                        && !structure.Sources.Any(source =>
                            source.ProposalAttemptUuid == batch.AttemptUuid
                            && source.InputRevisionUuid == extraction.InputRevisionUuid
                            && source.AccountIndex == index
                        )
                    )
                    {
                        throw new ArgumentException(
                            "Every account observation in a consumed proposal batch must remain a separate source assertion."
                        );
                    }
                }
            }
        }
        foreach (var source in structure.Sources)
        {
            var batch = batches[source.ProposalAttemptUuid];
            var extraction = batch.Extractions.SingleOrDefault(input =>
                input.InputRevisionUuid == source.InputRevisionUuid
            );
            if (
                extraction == null
                || source.AccountIndex < 0
                || source.AccountIndex >= extraction.Accounts.Count
                || !SameStructureJson(source.Account, extraction.Accounts[source.AccountIndex])
                || source.Account.Account.Origin.Item != identity
                || !structure.Evidence.Contains(source.Account.Account.Origin.Evidence)
            )
            {
                throw new ArgumentException(
                    "A source must reproduce its exact published account at the original input and account coordinates."
                );
            }
            var bindings = batch
                .Preparation.Inputs.Single(input =>
                    input.Input.InputRevisionUuid == source.InputRevisionUuid
                )
                .Accounts.Where(account =>
                    SameStructureJson(account.Account, source.Account.Account)
                )
                .Select(account => account.Binding)
                .ToArray();
            if (bindings.Length == 0)
            {
                throw new ArgumentException("The source account has no original prepared binding.");
            }
            if (
                !reviewed
                && structure.Edition.LiteralDate
                    != (source.Account.Account.LiteralDate?.Value ?? "")
            )
            {
                throw new ArgumentException(
                    "Automatic edition dates must retain the original source wording."
                );
            }
            if (
                !reviewed
                && bindings.Any(binding =>
                    binding.Artist.Status != NormalizationCatalogBindingStatus.Bound
                    || binding.Artist.Value?.ArtistUuid != structure.Edition.ArtistUuid
                )
            )
            {
                throw new ArgumentException(
                    "Automatic structure acceptance requires the original supported artist binding."
                );
            }
            if (
                !reviewed
                && structure.Edition.ShowUuid is { } show
                && bindings.Any(binding =>
                    binding.Performance.Status != NormalizationCatalogBindingStatus.Bound
                    || binding.Performance.Value?.ShowUuid != show
                )
            )
            {
                throw new ArgumentException(
                    "A performance unresolved by the source binding requires an explicit reviewed assignment."
                );
            }
            if (!reviewed && structure.Edition.Ordered && !source.Account.Account.Ordered)
            {
                throw new ArgumentException(
                    "An unordered account cannot establish accepted passage order."
                );
            }
        }
    }

    private static async Task ValidateStructureReadSet(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationWorkTicket ticket,
        SetlistStructureChange structure,
        CancellationToken ct
    )
    {
        var batches = await ReadStructureProposals(c, t, structure, ct);
        foreach (var batch in batches.Values)
        {
            var original = new NormalizationSubjectDependency(
                batch.Ticket.SubjectUuid,
                batch.Ticket.DesiredGeneration,
                batch.Ticket.EditorialRevision,
                batch.AttemptUuid
            );
            if (
                !ticket.Dependencies.Contains(original)
                || batch.Ticket.Inputs.Any(input => !ticket.Inputs.Contains(input))
                || (batch.Ticket.CatalogInputs ?? []).Any(input =>
                    !(ticket.CatalogInputs ?? []).Contains(input)
                )
                || (
                    batch.Ticket.MappingRegistry != null
                    && ticket.MappingRegistry != batch.Ticket.MappingRegistry
                )
            )
            {
                throw new NormalizationConflictException(
                    "Acceptance must consume the proposal's original publication receipt and complete original input/catalog read set."
                );
            }
        }
        var consumed = ticket.Inputs.Select(input => input.RevisionUuid).ToHashSet();
        if (
            structure
                .Sets.SelectMany(set => set.Evidence)
                .Any(evidence => !consumed.Contains(evidence.InputRevisionUuid))
        )
        {
            throw new ArgumentException(
                "Set evidence is outside the consumed original input read set."
            );
        }
    }

    private sealed record StructureProposalBound(int Count, long Bytes);
}
