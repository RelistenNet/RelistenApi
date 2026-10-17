using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedMusicService
{
    private Task<IReadOnlyList<Guid>> FindAdditionalReviewSubjects(
        MusicPublication publication,
        CancellationToken cancellationToken
    ) =>
        db.WithWriteConnection<IReadOnlyList<Guid>>(async c =>
        {
            var targets = publication
                .Merges.Where(x => x.Domain == "compositions")
                .Select(x => x.SurvivorUuid)
                .Concat(
                    publication
                        .Splits.Where(x => x.Domain == "compositions")
                        .SelectMany(x => x.Targets ?? [])
                        .Select(x => x.Uuid)
                )
                .Distinct()
                .Order()
                .ToArray();
            var subjects = new List<Guid>();
            foreach (var target in targets)
            {
                var subject = await c.QuerySingleOrDefaultAsync<Guid?>(
                    new CommandDefinition(
                        """
                        SELECT uuid FROM normalization.work_subjects
                        WHERE domain='compositions' AND subject_kind='composition' AND subject_uuid=@target
                        """,
                        new { target },
                        cancellationToken: cancellationToken
                    )
                );
                subjects.Add(
                    subject
                        ?? throw new ArgumentException(
                            "Register every affected composition owner with BeginWorkAsync before preparing a merge or split."
                        )
                );
            }
            return subjects;
        });

    private static Task LockReferencedPeople(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        MusicPublication publication,
        CancellationToken ct
    ) =>
        publication.WorkCredits.Any(x => x.PersonUuid.HasValue)
            ? c.ExecuteAsync(
                new CommandDefinition(
                    "SELECT pg_advisory_xact_lock_shared(hashtextextended('normalization:people:identity',0))",
                    transaction: t,
                    cancellationToken: ct
                )
            )
            : Task.CompletedTask;

    private static async Task ValidateReviewIdentitySnapshots(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        MusicPublication publication,
        CancellationToken ct
    )
    {
        foreach (var merge in publication.Merges)
        {
            var table = merge.Domain == "songs" ? "repertoire_songs" : "compositions";
            await ValidateIdentitySnapshot(
                c,
                t,
                table,
                merge.RetiredUuid,
                merge.ExpectedRevision,
                ct
            );
            await ValidateIdentitySnapshot(
                c,
                t,
                table,
                merge.SurvivorUuid,
                merge.ExpectedSurvivorRevision,
                ct
            );
            if (
                Changes(publication)
                    .Any(x => x.Uuid == merge.RetiredUuid || x.Uuid == merge.SurvivorUuid)
            )
            {
                throw new ArgumentException(
                    "Prepare identity wording changes separately from a merge so its two revision snapshots remain explicit."
                );
            }
        }
        foreach (var split in publication.Splits)
        {
            var table = split.Domain == "songs" ? "repertoire_songs" : "compositions";
            await ValidateIdentitySnapshot(
                c,
                t,
                table,
                split.RetainedUuid,
                split.ExpectedRevision,
                ct
            );
            if (split.Domain != "compositions")
            {
                continue;
            }
            var targets = split.Targets ?? [];
            if (
                targets.Select(x => x.Uuid).Distinct().Count() != targets.Count
                || targets.Any(x => x.Uuid == split.RetainedUuid || x.Uuid == Guid.Empty)
            )
            {
                throw new ArgumentException(
                    "A composition split lists distinct result identities separately from the retained identity."
                );
            }
            foreach (var target in targets)
            {
                await ValidateIdentitySnapshot(
                    c,
                    t,
                    table,
                    target.Uuid,
                    target.ExpectedRevision,
                    ct
                );
                if (
                    target.ExpectedRevision == 0
                    && !publication.Compositions.Any(x =>
                        x.Uuid == target.Uuid && x.ExpectedRevision == 0
                    )
                )
                {
                    throw new ArgumentException(
                        "A new split target needs its initial composition assertion in the same review."
                    );
                }
            }
            // Removing one retired identity changes accepted-name discovery for every survivor
            // above it. All those owners must be in the up-front lock/invalidation set.
            var current = split.RetainedUuid;
            var visited = new HashSet<Guid> { current };
            while (true)
            {
                var redirect = await c.QuerySingleOrDefaultAsync<Guid?>(
                    new CommandDefinition(
                        "SELECT redirect_uuid FROM normalization.compositions WHERE uuid=@current",
                        new { current },
                        t,
                        cancellationToken: ct
                    )
                );
                if (redirect == null)
                {
                    break;
                }
                if (!visited.Add(redirect.Value))
                {
                    throw new NormalizationConflictException(
                        "Composition redirects contain a cycle."
                    );
                }
                if (!targets.Any(x => x.Uuid == redirect.Value))
                {
                    throw new ArgumentException(
                        "A composition split must include every current redirect survivor and its observed revision."
                    );
                }
                current = redirect.Value;
            }
        }
    }

    private static async Task ValidateIdentitySnapshot(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        string table,
        Guid uuid,
        long expectedRevision,
        CancellationToken ct
    )
    {
        if (expectedRevision < 0)
        {
            throw new ArgumentException("An identity revision cannot be negative.");
        }
        var revision = await c.QuerySingleOrDefaultAsync<long?>(
            new CommandDefinition(
                $"SELECT r.revision FROM normalization.{table} x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid WHERE x.uuid=@uuid",
                new { uuid },
                t,
                cancellationToken: ct
            )
        );
        if ((revision ?? 0) != expectedRevision)
        {
            throw new NormalizationConflictException(
                "An affected music identity changed after review was prepared."
            );
        }
    }
}
