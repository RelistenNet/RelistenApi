using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Accepted music writes share the work store's publication and editorial fences. Helpers use
/// its transaction; no caller can write a current relationship without a durable decision receipt.
/// </summary>
public sealed partial class NormalizedMusicService(DbService db, NormalizationWorkService work)
{
    public Task<NormalizationPublicationResult> PublishAsync(
        Guid workUuid,
        MusicPublication publication,
        CancellationToken cancellationToken = default
    ) =>
        work.TryPublishAsync(
            workUuid,
            async (c, t, ct) =>
            {
                await LockReferencedPeople(c, t, publication, ct);
                var subject = await c.QuerySingleAsync<Guid>(
                    new CommandDefinition(
                        "SELECT subject_uuid FROM normalization.work_runs WHERE uuid=@workUuid",
                        new { workUuid },
                        t,
                        cancellationToken: ct
                    )
                );
                await ValidateOwnership(c, t, subject, publication, ct);
                var ticketJson = await c.QuerySingleAsync<string>(
                    new CommandDefinition(
                        "SELECT ticket::text FROM normalization.work_runs WHERE uuid=@workUuid",
                        new { workUuid },
                        t,
                        cancellationToken: ct
                    )
                );
                var ticket = JsonSerializer.Deserialize<NormalizationWorkTicket>(ticketJson)!;
                var consumed = ticket.Inputs.Select(i => i.RevisionUuid).ToHashSet();
                if (
                    Changes(publication)
                        .SelectMany(x => x.Evidence)
                        .Any(e => !consumed.Contains(e.InputRevisionUuid))
                )
                {
                    throw new ArgumentException(
                        "Publication cites evidence outside its consumed read set."
                    );
                }
                await ValidateDependencies(c, t, ticket, publication, ct);
                await Apply(c, t, publication, workUuid, false, ct);
            },
            cancellationToken
        );

    public async Task<NormalizationReviewResult> ApplyDecisionAsync(
        Guid operationUuid,
        Guid subjectUuid,
        long expectedEditorialRevision,
        string action,
        MusicPublication publication,
        string reason,
        string trustedOperator,
        CancellationToken cancellationToken = default
    )
    {
        if (action is not ("accept" or "reject" or "replace" or "retract" or "merge" or "split"))
        {
            throw new ArgumentException(
                "Music decisions support accept, reject, replace, retract, merge and split.",
                nameof(action)
            );
        }
        if (action == "merge" && publication.Merges.Count == 0)
        {
            throw new ArgumentException(
                "A merge requires explicit retired and survivor identities."
            );
        }
        if ((action == "split") != (publication.Splits.Count > 0))
        {
            throw new ArgumentException(
                "A split requires explicit retained identity correspondence."
            );
        }
        if (action != "merge" && publication.Merges.Count > 0)
        {
            throw new ArgumentException("Identity merges require the merge action.");
        }
        // These owners must already exist. The shared store locks this complete set before
        // invoking the callback, so target invalidation cannot race a consumer publication.
        var additionalSubjectUuids = await FindAdditionalReviewSubjects(
            publication,
            cancellationToken
        );
        var command = new NormalizationReviewCommand(
            operationUuid,
            subjectUuid,
            expectedEditorialRevision,
            action,
            JsonSerializer.SerializeToElement(publication),
            reason,
            trustedOperator,
            AdditionalSubjectUuids: additionalSubjectUuids
        );
        return await work.ApplyReviewAsync(
            command,
            async (c, t, ct) =>
            {
                await LockReferencedPeople(c, t, publication, ct);
                await ValidateOwnership(c, t, subjectUuid, publication, ct);
                await ValidateReviewIdentitySnapshots(c, t, publication, ct);
                if (action == "reject")
                {
                    await Reject(c, t, publication, operationUuid, ct);
                }
                else
                {
                    await Apply(c, t, publication, operationUuid, true, ct);
                }
            },
            cancellationToken
        );
    }

    private static async Task Apply(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        MusicPublication publication,
        Guid decisionUuid,
        bool reviewed,
        CancellationToken ct
    )
    {
        await LockAndValidateChanges(c, t, publication, reviewed, ct);
        await RestoreSplitIdentities(c, t, publication, reviewed, ct);
        foreach (var song in publication.RepertoireSongs)
        {
            await SaveRepertoire(c, t, song, decisionUuid, reviewed, ct);
        }
        foreach (var composition in publication.Compositions)
        {
            await SaveComposition(c, t, composition, decisionUuid, reviewed, ct);
        }
        foreach (var identity in publication.ExternalIdentities.OrderByDescending(x => x.Retracted))
        {
            await SaveExternalIdentity(c, t, identity, decisionUuid, reviewed, ct);
        }
        foreach (var assignment in publication.ImportedSongs.OrderByDescending(x => x.Retracted))
        {
            await SaveImportedSong(c, t, assignment, decisionUuid, reviewed, ct);
        }
        foreach (var credit in publication.WorkCredits)
        {
            await SaveWorkCredit(c, t, credit, decisionUuid, reviewed, ct);
        }
        foreach (var link in publication.CompositionLinks.OrderByDescending(x => x.Retracted))
        {
            await SaveCompositionLink(c, t, link, decisionUuid, reviewed, ct);
        }
        foreach (var edition in publication.Editions.OrderBy(x => x.Selected))
        {
            await SaveEdition(c, t, edition, decisionUuid, reviewed, ct);
        }
        foreach (var occurrence in publication.Occurrences)
        {
            await SaveOccurrence(c, t, occurrence, decisionUuid, reviewed, ct);
        }
        foreach (var content in publication.TrackContents)
        {
            await SaveContent(c, t, content, decisionUuid, reviewed, ct);
        }
        if (!reviewed && publication.Merges.Count > 0)
        {
            throw new ArgumentException(
                "Identity merges require an explicit reviewed correspondence."
            );
        }
        foreach (var merge in publication.Merges)
        {
            await ApplyMerge(c, t, merge, decisionUuid, ct);
        }
        await ValidateEditionOrdering(c, t, publication, ct);
    }

    private static async Task LockAndValidateChanges(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        MusicPublication publication,
        bool reviewed,
        CancellationToken ct
    )
    {
        // Lock by identity before checking revisions. Distinct work subjects may address the same
        // canonical entity; ordered advisory locks prevent two first inserts from racing.
        var changes = Changes(publication).ToArray();
        if (changes.Select(x => x.Uuid).Distinct().Count() != changes.Length)
        {
            throw new ArgumentException(
                "A publication must give each changed identity exactly once."
            );
        }
        foreach (
            var uuid in changes
                .Select(x => x.Uuid)
                .Concat(ReferencedIdentities(publication))
                .Concat(publication.Merges.SelectMany(x => new[] { x.RetiredUuid, x.SurvivorUuid }))
                .Distinct()
                .OrderBy(x => x)
        )
        {
            await c.ExecuteAsync(
                new CommandDefinition(
                    "SELECT pg_advisory_xact_lock(hashtextextended(@key,21))",
                    new { key = uuid.ToString() },
                    t,
                    cancellationToken: ct
                )
            );
        }
        foreach (var change in changes)
        {
            if (change.Uuid == Guid.Empty || change.ExpectedRevision < 0)
            {
                throw new ArgumentException(
                    "Music identities require a UUID and a nonnegative expected revision."
                );
            }
            if (
                change.Basis
                    is not (
                        "provider_assertion"
                        or "validated_match"
                        or "title_derived"
                        or "reviewed"
                    )
                || (!reviewed && change.Basis == "reviewed")
            )
            {
                throw new ArgumentException(
                    "The assertion basis does not match this decision authority."
                );
            }
            if (change.Evidence.Count == 0)
            {
                throw new ArgumentException("Accepted music assertions require retained evidence.");
            }
            await NormalizedPeopleService.ValidateEvidenceAsync(c, t, change.Evidence, ct);
            if (!reviewed)
            {
                await Require(
                    c,
                    t,
                    "SELECT NOT EXISTS(SELECT 1 FROM normalization.music_exclusions WHERE claim_hash=@hash)",
                    new { hash = ClaimHash(change) },
                    "This claim and its evidence were rejected by review.",
                    ct
                );
            }
        }
    }

    private static async Task RestoreSplitIdentities(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        MusicPublication publication,
        bool reviewed,
        CancellationToken ct
    )
    {
        foreach (var split in publication.Splits)
        {
            if (!reviewed || split.Domain is not ("songs" or "compositions"))
            {
                throw new ArgumentException("Splits require a reviewed music domain.");
            }
            MusicChange? retained =
                split.Domain == "songs"
                    ? publication.RepertoireSongs.SingleOrDefault(x => x.Uuid == split.RetainedUuid)
                    : publication.Compositions.SingleOrDefault(x => x.Uuid == split.RetainedUuid);
            if (retained == null || retained.ExpectedRevision != split.ExpectedRevision)
            {
                throw new ArgumentException(
                    "A split must version its retained identity in the same publication."
                );
            }
            var table = split.Domain == "songs" ? "repertoire_songs" : "compositions";
            await c.ExecuteAsync(
                new CommandDefinition(
                    $"UPDATE normalization.{table} SET redirect_uuid=NULL WHERE uuid=@RetainedUuid",
                    split,
                    t,
                    cancellationToken: ct
                )
            );
        }
    }

    private static async Task Reject(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        MusicPublication publication,
        Guid decision,
        CancellationToken ct
    )
    {
        var changes = Changes(publication).ToArray();
        if (changes.Length == 0)
        {
            throw new ArgumentException("A rejection requires at least one typed claim.");
        }
        foreach (var change in changes)
        {
            await NormalizedPeopleService.ValidateEvidenceAsync(c, t, change.Evidence, ct);
            var hash = ClaimHash(change);
            await c.ExecuteAsync(
                new CommandDefinition(
                    "INSERT INTO normalization.music_exclusions(claim_hash,decision_uuid,payload) VALUES(@hash,@decision,@payload::jsonb)",
                    new
                    {
                        hash,
                        decision,
                        payload = JsonSerializer.Serialize(change, change.GetType()),
                    },
                    t,
                    cancellationToken: ct
                )
            );
            foreach (var input in change.Evidence.Select(e => e.InputRevisionUuid).Distinct())
            {
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "INSERT INTO normalization.music_exclusion_evidence(claim_hash,decision_uuid,input_revision_uuid) VALUES(@hash,@decision,@input)",
                        new
                        {
                            hash,
                            decision,
                            input,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
            }
        }
    }

    private static string ClaimHash(MusicChange change)
    {
        // Proposal UUIDs and revision bookkeeping cannot turn a rejected semantic claim into new work.
        var properties = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (
            var property in JsonSerializer
                .SerializeToElement(change, change.GetType())
                .EnumerateObject()
        )
        {
            if (property.Name is not ("Uuid" or "ExpectedRevision" or "Basis"))
            {
                properties.Add(property.Name, property.Value);
            }
        }
        return Convert
            .ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        change.GetType().Name + JsonSerializer.Serialize(properties)
                    )
                )
            )
            .ToLowerInvariant();
    }

    private static IEnumerable<MusicChange> Changes(MusicPublication publication) =>
        publication
            .RepertoireSongs.Cast<MusicChange>()
            .Concat(publication.Compositions)
            .Concat(publication.ExternalIdentities)
            .Concat(publication.ImportedSongs)
            .Concat(publication.WorkCredits)
            .Concat(publication.CompositionLinks)
            .Concat(publication.Editions)
            .Concat(publication.Occurrences)
            .Concat(publication.TrackContents);

    private static async Task SaveRepertoire(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        RepertoireSongChange song,
        Guid decision,
        bool reviewed,
        CancellationToken ct
    )
    {
        var artist = await c.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                "SELECT artist_uuid FROM normalization.repertoire_songs WHERE uuid=@Uuid",
                song,
                t,
                cancellationToken: ct
            )
        );
        if (artist.HasValue && artist.Value != song.ArtistUuid)
        {
            throw new NormalizationConflictException(
                "A repertoire identity cannot move to another artist."
            );
        }
        if (!artist.HasValue)
        {
            await Require(
                c,
                t,
                "SELECT EXISTS(SELECT 1 FROM artists WHERE uuid=@ArtistUuid)",
                song,
                "The repertoire artist is unavailable.",
                ct
            );
        }
        if (song.Basis is not ("provider_assertion" or "reviewed" or "title_derived"))
        {
            throw new ArgumentException(
                "Repertoire identity basis must be provider_assertion, reviewed or title_derived."
            );
        }
        await Persist(
            c,
            t,
            "repertoire_songs",
            song,
            "artist_uuid,display_title,aliases",
            "@ArtistUuid,@DisplayTitle,@Aliases",
            decision,
            reviewed,
            ct
        );
    }

    private static async Task SaveImportedSong(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        ImportedSongAssignmentChange assignment,
        Guid decision,
        bool reviewed,
        CancellationToken ct
    )
    {
        await Require(
            c,
            t,
            "SELECT NOT EXISTS(SELECT 1 FROM normalization.imported_song_assignments WHERE uuid=@Uuid AND imported_song_uuid<>@ImportedSongUuid)",
            assignment,
            "An imported song assignment cannot move to another imported identity.",
            ct
        );
        await RequireUnredirected(c, t, "repertoire_songs", assignment.RepertoireSongUuid, ct);
        await Require(
            c,
            t,
            """
            SELECT EXISTS(SELECT 1 FROM setlist_songs imported JOIN artists a ON a.id=imported.artist_id
                JOIN normalization.repertoire_songs song ON song.artist_uuid=a.uuid
                WHERE imported.uuid=@ImportedSongUuid AND song.uuid=@RepertoireSongUuid)
            OR EXISTS(SELECT 1 FROM normalization.imported_song_assignments WHERE uuid=@Uuid
                AND imported_song_uuid=@ImportedSongUuid AND repertoire_song_uuid=@RepertoireSongUuid)
            """,
            assignment,
            "The imported song and repertoire song must belong to the same available artist.",
            ct
        );
        await Persist(
            c,
            t,
            "imported_song_assignments",
            assignment,
            "imported_song_uuid,repertoire_song_uuid",
            "@ImportedSongUuid,@RepertoireSongUuid",
            decision,
            reviewed,
            ct
        );
    }

    private static async Task SaveExternalIdentity(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        MusicExternalIdentityChange identity,
        Guid decision,
        bool reviewed,
        CancellationToken ct
    )
    {
        if (identity.RepertoireSongUuid.HasValue == identity.CompositionUuid.HasValue)
        {
            throw new ArgumentException(
                "A music external identity belongs to exactly one repertoire song or composition."
            );
        }
        await Require(
            c,
            t,
            """
            SELECT NOT EXISTS(SELECT 1 FROM normalization.music_external_identities WHERE uuid=@Uuid
                AND (repertoire_song_uuid IS DISTINCT FROM @RepertoireSongUuid OR composition_uuid IS DISTINCT FROM @CompositionUuid
                    OR provider<>@Provider OR resource_type<>@ResourceType OR provider_key<>@ProviderKey))
            """,
            identity,
            "A qualified external identity cannot silently change owner or provider key; retain and explicitly replace it.",
            ct
        );
        Guid? artist = null;
        if (identity.RepertoireSongUuid is { } song)
        {
            await RequireUnredirected(c, t, "repertoire_songs", song, ct);
            artist = await c.QuerySingleAsync<Guid>(
                new CommandDefinition(
                    "SELECT artist_uuid FROM normalization.repertoire_songs WHERE uuid=@song",
                    new { song },
                    t,
                    cancellationToken: ct
                )
            );
        }
        else
        {
            await RequireUnredirected(c, t, "compositions", identity.CompositionUuid!.Value, ct);
        }
        await Persist(
            c,
            t,
            "music_external_identities",
            identity,
            "repertoire_song_uuid,composition_uuid,artist_uuid,provider,resource_type,provider_key",
            "@RepertoireSongUuid,@CompositionUuid,@artist,@Provider,@ResourceType,@ProviderKey",
            decision,
            reviewed,
            ct,
            new { artist }
        );
    }

    private static async Task Persist<T>(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        string table,
        T change,
        string columns,
        string values,
        Guid decision,
        bool reviewed,
        CancellationToken ct,
        object? extra = null
    )
        where T : MusicChange
    {
        var current = await c.QuerySingleOrDefaultAsync<CurrentRevision>(
            new CommandDefinition(
                $"""
                SELECT r.revision AS Revision,r.basis AS Basis,x.revision_uuid AS RevisionUuid
                FROM normalization.{table} x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid WHERE x.uuid=@Uuid FOR UPDATE OF x
                """,
                change,
                t,
                cancellationToken: ct
            )
        );
        if ((current?.Revision ?? 0) != change.ExpectedRevision)
        {
            throw new NormalizationConflictException(
                $"The {table} assertion changed after it was read."
            );
        }
        if (!reviewed && current?.Basis == "reviewed")
        {
            throw new NormalizationConflictException(
                "An automatic publication cannot replace a reviewed music assertion."
            );
        }
        var revisionUuid = Guid.NewGuid();
        var revision = (current?.Revision ?? 0) + 1;
        var basis = reviewed ? "reviewed" : change.Basis;
        var effective = reviewed ? change with { Basis = "reviewed" } : change;
        await c.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO normalization.music_revisions(uuid,entity_uuid,entity_kind,revision,decision_uuid,basis,payload,evidence)
                VALUES(@revisionUuid,@Uuid,@table,@revision,@decision,@basis,@payload::jsonb,@evidence::jsonb)
                """,
                new
                {
                    revisionUuid,
                    change.Uuid,
                    table,
                    revision,
                    decision,
                    basis,
                    payload = JsonSerializer.Serialize(effective, effective.GetType()),
                    evidence = JsonSerializer.Serialize(change.Evidence),
                },
                t,
                cancellationToken: ct
            )
        );
        for (var ordinal = 0; ordinal < change.Evidence.Count; ordinal++)
        {
            var evidence = change.Evidence[ordinal];
            await c.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO normalization.music_evidence_references(revision_uuid,ordinal,input_revision_uuid,json_pointer,start_code_point,end_code_point,quote)
                    VALUES(@revisionUuid,@ordinal,@InputRevisionUuid,@JsonPointer,@StartCodePoint,@EndCodePoint,@Quote)
                    """,
                    new
                    {
                        revisionUuid,
                        ordinal,
                        evidence.InputRevisionUuid,
                        evidence.JsonPointer,
                        evidence.StartCodePoint,
                        evidence.EndCodePoint,
                        evidence.Quote,
                    },
                    t,
                    cancellationToken: ct
                )
            );
        }
        var parameters = new DynamicParameters(change);
        if (extra != null)
        {
            parameters.AddDynamicParams(extra);
        }
        parameters.Add("revisionUuid", revisionUuid);
        var assignments = string.Join(
            ",",
            columns.Split(',').Select(column => $"{column}=EXCLUDED.{column}")
        );
        await c.ExecuteAsync(
            new CommandDefinition(
                $"""
                INSERT INTO normalization.{table}(uuid,{columns},retracted,revision_uuid)
                VALUES(@Uuid,{values},@Retracted,@revisionUuid)
                ON CONFLICT(uuid) DO UPDATE SET {assignments},retracted=EXCLUDED.retracted,revision_uuid=EXCLUDED.revision_uuid
                """,
                parameters,
                t,
                cancellationToken: ct
            )
        );
    }

    private sealed record CurrentRevision(long Revision, string Basis, Guid RevisionUuid);

    private static Task RequireUnredirected(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        string table,
        Guid uuid,
        CancellationToken ct
    ) =>
        Require(
            c,
            t,
            $"SELECT EXISTS(SELECT 1 FROM normalization.{table} WHERE uuid=@uuid AND NOT retracted AND redirect_uuid IS NULL)",
            new { uuid },
            "The target identity is unavailable or merged.",
            ct
        );

    private static async Task Require(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        string sql,
        object parameters,
        string message,
        CancellationToken ct
    )
    {
        if (
            !await c.QuerySingleAsync<bool>(
                new CommandDefinition(sql, parameters, t, cancellationToken: ct)
            )
        )
        {
            throw new ArgumentException(message);
        }
    }

    private static Task RequireCatalogBinding(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        string table,
        Guid uuid,
        string column,
        Guid target,
        string catalog,
        CancellationToken ct
    ) =>
        Require(
            c,
            t,
            $"SELECT EXISTS(SELECT 1 FROM {catalog} WHERE uuid=@target) OR EXISTS(SELECT 1 FROM normalization.{table} WHERE uuid=@uuid AND {column}=@target)",
            new { uuid, target },
            "The new catalog binding is unavailable.",
            ct
        );
}
