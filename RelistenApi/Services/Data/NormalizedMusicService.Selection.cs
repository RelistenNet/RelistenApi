using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Display selection has its own editorial owner. Source writers retain authority over edition
/// metadata; neither selecting an account nor clearing it rewrites its metadata revision or basis.
/// </summary>
public sealed partial class NormalizedMusicService
{
    /// <summary>Reads selection context without registering an owner or changing accepted data.</summary>
    public Task<SetlistSelectionContext?> ReadSelectionContextAsync(
        Guid showUuid,
        Guid? proposedEditionUuid = null,
        CancellationToken cancellationToken = default
    )
    {
        ValidateSelectionUuid(showUuid, nameof(showUuid));
        if (proposedEditionUuid.HasValue)
        {
            ValidateSelectionUuid(proposedEditionUuid.Value, nameof(proposedEditionUuid));
        }
        return db.WithWriteConnection<SetlistSelectionContext?>(async connection =>
        {
            var c = (NpgsqlConnection)connection;
            await using var t = await c.BeginTransactionAsync(
                System.Data.IsolationLevel.RepeatableRead,
                cancellationToken
            );
            await c.ExecuteAsync(
                new CommandDefinition(
                    "SET TRANSACTION READ ONLY",
                    transaction: t,
                    cancellationToken: cancellationToken
                )
            );
            var artist = await c.QuerySingleOrDefaultAsync<Guid?>(
                new CommandDefinition(
                    "SELECT a.uuid FROM shows sh JOIN artists a ON a.id=sh.artist_id WHERE sh.uuid=@showUuid",
                    new { showUuid },
                    t,
                    cancellationToken: cancellationToken
                )
            );
            var owner = await c.QuerySingleOrDefaultAsync<SelectionOwnerRow>(
                new CommandDefinition(
                    """
                    SELECT uuid AS SubjectUuid,desired_generation AS DesiredGeneration,
                        editorial_revision AS EditorialRevision,published_work_uuid AS PublishedWorkUuid
                    FROM normalization.work_subjects
                    WHERE domain='songs' AND subject_kind='show_setlist_selection' AND subject_uuid=@showUuid
                    """,
                    new { showUuid },
                    t,
                    cancellationToken: cancellationToken
                )
            );
            var rows = await ReadSelectionEditions(
                c,
                t,
                showUuid,
                proposedEditionUuid,
                false,
                cancellationToken
            );
            var selected = rows.SingleOrDefault(row => row.ShowUuid == showUuid && row.Selected);
            var exists =
                artist.HasValue
                || owner != null
                || await c.ExecuteScalarAsync<bool>(
                    new CommandDefinition(
                        "SELECT EXISTS(SELECT 1 FROM normalization.setlist_editions WHERE show_uuid=@showUuid)",
                        new { showUuid },
                        t,
                        cancellationToken: cancellationToken
                    )
                );
            var result = exists
                ? new SetlistSelectionContext(
                    showUuid,
                    artist,
                    owner == null
                        ? null
                        : new(
                            owner.SubjectUuid,
                            owner.DesiredGeneration,
                            owner.EditorialRevision,
                            owner.PublishedWorkUuid
                        ),
                    selected,
                    rows.SingleOrDefault(row => row.EditionUuid == proposedEditionUuid)
                )
                : null;
            await t.CommitAsync(cancellationToken);
            return result;
        });
    }

    /// <summary>
    /// Atomically selects, switches or clears one show's display account. Register the selection
    /// owner first with BeginWorkAsync. Exact operation replay returns its original receipt even
    /// if the target metadata or catalog show subsequently changes.
    /// </summary>
    public Task<NormalizationReviewResult> ApplySelectionAsync(
        SetlistSelectionDecision command,
        string trustedOperator,
        CancellationToken cancellationToken = default
    )
    {
        ValidateSelectionUuid(command.ShowUuid, nameof(command.ShowUuid));
        if (
            command.EditionUuid.HasValue != command.ExpectedEditionRevision.HasValue
            || command.ExpectedEditionRevision is < 1
        )
        {
            throw new ArgumentException(
                "A selection target requires its positive metadata revision; clearing has no target revision."
            );
        }
        if (command.EditionUuid.HasValue)
        {
            ValidateSelectionUuid(command.EditionUuid.Value, nameof(command.EditionUuid));
        }
        if (command.ExpectedSelectedEditionUuid.HasValue)
        {
            ValidateSelectionUuid(
                command.ExpectedSelectedEditionUuid.Value,
                nameof(command.ExpectedSelectedEditionUuid)
            );
        }
        return work.ApplyReviewAsync(
            new(
                command.OperationUuid,
                command.SubjectUuid,
                command.ExpectedEditorialRevision,
                command.EditionUuid == null ? "retract"
                    : command.ExpectedSelectedEditionUuid == null ? "accept"
                    : "replace",
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
                    "songs",
                    "show_setlist_selection",
                    command.ShowUuid,
                    ct
                );
                // Metadata publishers lock their own edition row and never this selection owner.
                // Sorted row locks serialize switches without reviewing or invalidating source owners.
                var rows = await ReadSelectionEditions(
                    c,
                    t,
                    command.ShowUuid,
                    command.EditionUuid,
                    true,
                    ct
                );
                var selected = rows.SingleOrDefault(row =>
                    row.ShowUuid == command.ShowUuid && row.Selected
                );
                if (selected?.EditionUuid != command.ExpectedSelectedEditionUuid)
                {
                    throw new NormalizationConflictException(
                        "The display selection changed after it was read."
                    );
                }
                if (command.EditionUuid is { } targetUuid)
                {
                    var target = rows.SingleOrDefault(row => row.EditionUuid == targetUuid);
                    if (target == null || target.Revision != command.ExpectedEditionRevision)
                    {
                        throw new NormalizationConflictException(
                            "The proposed edition changed after it was read."
                        );
                    }
                    var artist = await c.QuerySingleOrDefaultAsync<Guid?>(
                        new CommandDefinition(
                            "SELECT a.uuid FROM shows sh JOIN artists a ON a.id=sh.artist_id WHERE sh.uuid=@ShowUuid",
                            command,
                            t,
                            cancellationToken: ct
                        )
                    );
                    if (
                        !artist.HasValue
                        || target.ShowUuid != command.ShowUuid
                        || target.ArtistUuid != artist.Value
                        || target.Retracted
                    )
                    {
                        throw new ArgumentException(
                            "Select an active edition assigned to the available show and its artist."
                        );
                    }
                }
                // Clear before setting the new target so the unique selected-show index holds
                // throughout a switch, including when the old and new UUIDs sort differently.
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE normalization.setlist_editions SET selected=false WHERE show_uuid=@ShowUuid AND selected",
                        command,
                        t,
                        cancellationToken: ct
                    )
                );
                if (command.EditionUuid.HasValue)
                {
                    await c.ExecuteAsync(
                        new CommandDefinition(
                            "UPDATE normalization.setlist_editions SET selected=true WHERE uuid=@EditionUuid",
                            command,
                            t,
                            cancellationToken: ct
                        )
                    );
                }
            },
            cancellationToken
        );
    }

    // Called before SaveEdition validates or persists metadata. The locked physical value is
    // authoritative: a provider's default false cannot erase a reviewed or legacy display choice.
    private static async Task<SetlistEditionChange> PreserveEditionSelectionAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistEditionChange edition,
        CancellationToken ct
    )
    {
        var current = await c.QuerySingleOrDefaultAsync<SelectionMetadataRow>(
            new CommandDefinition(
                "SELECT show_uuid AS ShowUuid,selected AS Selected FROM normalization.setlist_editions WHERE uuid=@Uuid FOR UPDATE",
                edition,
                t,
                cancellationToken: ct
            )
        );
        var selected = current?.Selected == true;
        if (edition.Selected && !selected)
        {
            throw new ArgumentException(
                "Display selection requires an explicit selection decision."
            );
        }
        if (selected && (edition.Retracted || edition.ShowUuid != current!.ShowUuid))
        {
            throw new NormalizationConflictException(
                "Clear the display selection before retracting or reassigning this edition."
            );
        }
        return edition with { Selected = selected };
    }

    private static async Task<SetlistSelectionEdition[]> ReadSelectionEditions(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid showUuid,
        Guid? proposedEditionUuid,
        bool forUpdate,
        CancellationToken ct
    ) =>
        (
            await c.QueryAsync<SetlistSelectionEdition>(
                new CommandDefinition(
                    """
                    SELECT e.uuid AS EditionUuid,e.artist_uuid AS ArtistUuid,e.show_uuid AS ShowUuid,
                        r.revision AS Revision,e.retracted AS Retracted,e.selected AS Selected
                    FROM normalization.setlist_editions e JOIN normalization.music_revisions r ON r.uuid=e.revision_uuid
                    WHERE (e.show_uuid=@showUuid AND e.selected) OR e.uuid=@proposedEditionUuid
                    ORDER BY e.uuid
                    """ + (forUpdate ? " FOR UPDATE OF e" : ""),
                    new { showUuid, proposedEditionUuid },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();

    private static void ValidateSelectionUuid(Guid uuid, string parameter)
    {
        if (uuid == Guid.Empty)
        {
            throw new ArgumentException("Selection identities must not be empty.", parameter);
        }
    }

    private sealed record SelectionMetadataRow(Guid? ShowUuid, bool Selected);

    private sealed record SelectionOwnerRow(
        Guid SubjectUuid,
        long DesiredGeneration,
        long EditorialRevision,
        Guid? PublishedWorkUuid
    );
}
