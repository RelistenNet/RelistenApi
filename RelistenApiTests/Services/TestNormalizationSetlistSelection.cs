using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using DomainInput = RelistenApiTests.Services.NormalizationDomainFixture.DomainInput;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationSetlistSelection
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizedMusicService music = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        music = new(fixture.Db, fixture.Work);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task SelectSwitchAndClearChangeOnlyDisplayAuthority()
    {
        var show = await Show();
        var first = await Edition(show);
        var second = await Edition(show);
        await Register(show.ShowUuid);
        var beforeFirst = await music.GetEditionAsync(first.Value.Uuid);
        var beforeSecond = await music.GetEditionAsync(second.Value.Uuid);
        var sourceOwner = await fixture.Work.GetSubjectAsync(first.Ticket.SubjectUuid);
        var history = await MusicHistoryCount();

        await music.ApplySelectionAsync(await Command(show.ShowUuid, first.Value.Uuid), "operator");
        (await music.GetEditionAsync(first.Value.Uuid))!.Value.Selected.Should().BeTrue();
        await music.ApplySelectionAsync(
            await Command(show.ShowUuid, second.Value.Uuid),
            "operator"
        );
        (await music.GetEditionAsync(first.Value.Uuid))!.Value.Selected.Should().BeFalse();
        (await music.GetShowEditionsAsync(show.ShowUuid))
            .Count(row => row.Value.Selected)
            .Should()
            .Be(1);
        await music.ApplySelectionAsync(await Command(show.ShowUuid, null), "operator");

        (await music.GetEditionAsync(first.Value.Uuid))!
            .Revision.Should()
            .Be(beforeFirst!.Revision);
        (await music.GetEditionAsync(second.Value.Uuid))!
            .Revision.Should()
            .Be(beforeSecond!.Revision);
        (await music.GetEditionAsync(first.Value.Uuid))!
            .Value.Basis.Should()
            .Be("provider_assertion");
        (await fixture.Work.GetSubjectAsync(first.Ticket.SubjectUuid))
            .Should()
            .BeEquivalentTo(sourceOwner);
        (await MusicHistoryCount()).Should().Be(history);
        (await music.ReadSelectionContextAsync(show.ShowUuid))!.SelectedEdition.Should().BeNull();
    }

    [Test]
    public async Task ContextDoesNotRegisterAnOwnerAndWrongOwnerCannotSelect()
    {
        var show = await Show();
        var edition = await Edition(show);
        var context = (await music.ReadSelectionContextAsync(show.ShowUuid, edition.Value.Uuid))!;
        context.Owner.Should().BeNull();
        (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("songs", "show_setlist_selection", show.ShowUuid)
            )
        )
            .Should()
            .BeNull();
        var wrong = new SetlistSelectionDecision(
            Guid.NewGuid(),
            edition.Ticket.SubjectUuid,
            show.ShowUuid,
            0,
            null,
            edition.Value.Uuid,
            1,
            "Wrong authority"
        );
        var apply = async () => await music.ApplySelectionAsync(wrong, "operator");
        await apply.Should().ThrowAsync<ArgumentException>();
        (await music.GetEditionAsync(edition.Value.Uuid))!.Value.Selected.Should().BeFalse();
    }

    [Test]
    public async Task MetadataDefaultsPreserveSelectionWithoutBecomingReviewed()
    {
        var show = await Show();
        var edition = await Edition(show);
        await Register(show.ShowUuid);
        await music.ApplySelectionAsync(
            await Command(show.ShowUuid, edition.Value.Uuid),
            "operator"
        );

        await Update(
            edition,
            edition.Value with
            {
                ExpectedRevision = 1,
                LiteralDate = "October 17, 2026",
                Selected = false,
            }
        );

        var current = (await music.GetEditionAsync(edition.Value.Uuid))!;
        current.Value.Selected.Should().BeTrue();
        current.Value.LiteralDate.Should().Be("October 17, 2026");
        current.Value.Basis.Should().Be("provider_assertion");
        var reads = new NormalizationReadService(
            fixture.Db,
            Options.Create(new NormalizationReadOptions { Enabled = true })
        );
        (await reads.ListShowSetlistsAsync(show.ShowUuid))!
            .items.Single()
            .selected.Should()
            .BeTrue();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SourceMetadataCannotSelectAnAccount(bool reviewed)
    {
        var show = await Show();
        var edition = await Edition(show);
        var change = edition.Value with { ExpectedRevision = 1, Selected = true };
        var apply = async () =>
        {
            if (reviewed)
            {
                await music.ApplyDecisionAsync(
                    Guid.NewGuid(),
                    edition.Ticket.SubjectUuid,
                    0,
                    "replace",
                    new() { Editions = [change] },
                    "Source correction",
                    "operator"
                );
            }
            else
            {
                await Update(edition, change);
            }
        };
        await apply.Should().ThrowAsync<ArgumentException>();
        (await music.GetEditionAsync(edition.Value.Uuid))!.Value.Selected.Should().BeFalse();
        (await music.GetEditionAsync(edition.Value.Uuid))!.Revision.Should().Be(1);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SelectedAccountMustBeClearedBeforeRetractionOrReassignment(bool reassign)
    {
        var show = await Show();
        var other = await Show(show.ArtistId);
        var edition = await Edition(show);
        await Register(show.ShowUuid);
        await music.ApplySelectionAsync(
            await Command(show.ShowUuid, edition.Value.Uuid),
            "operator"
        );
        var correction = edition.Value with
        {
            ExpectedRevision = 1,
            Retracted = !reassign,
            ShowUuid = reassign ? other.ShowUuid : show.ShowUuid,
        };
        var update = async () => await Update(edition, correction);
        await update.Should().ThrowAsync<NormalizationConflictException>();
        (await music.GetEditionAsync(edition.Value.Uuid))!.Value.Selected.Should().BeTrue();

        await music.ApplySelectionAsync(await Command(show.ShowUuid, null), "operator");
        await Update(edition, correction);
        if (reassign)
        {
            (await music.GetEditionAsync(edition.Value.Uuid))!
                .Value.ShowUuid.Should()
                .Be(other.ShowUuid);
        }
        else
        {
            (await music.GetEditionAsync(edition.Value.Uuid)).Should().BeNull();
        }
    }

    [Test]
    public async Task StaleTargetAndStaleEditorialContextCannotChangeTheChoice()
    {
        var show = await Show();
        var first = await Edition(show);
        var second = await Edition(show);
        await Register(show.ShowUuid);
        var staleTarget = await Command(show.ShowUuid, first.Value.Uuid);
        await Update(first, first.Value with { ExpectedRevision = 1, LiteralDate = "corrected" });
        var selectChanged = async () => await music.ApplySelectionAsync(staleTarget, "operator");
        await selectChanged.Should().ThrowAsync<NormalizationConflictException>();
        var staleChoice = await Command(show.ShowUuid, first.Value.Uuid);
        await music.ApplySelectionAsync(
            await Command(show.ShowUuid, second.Value.Uuid),
            "operator"
        );
        var replace = async () => await music.ApplySelectionAsync(staleChoice, "operator");
        await replace.Should().ThrowAsync<NormalizationConflictException>();
        (await music.ReadSelectionContextAsync(show.ShowUuid))!
            .SelectedEdition!.EditionUuid.Should()
            .Be(second.Value.Uuid);
    }

    [Test]
    public async Task LegacySelectionIsVisibleAndItsFirstDecisionRecordsThePreviousChoice()
    {
        var show = await Show();
        var edition = await Edition(show);
        // Simulate the physical flag retained from before the selector existed. Its immutable
        // source payload remains false and must not be rewritten to invent an editorial review.
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE normalization.setlist_editions SET selected=true WHERE uuid=@Uuid",
                edition.Value
            )
        );
        (await music.ReadSelectionContextAsync(show.ShowUuid))!.Owner.Should().BeNull();
        (await music.GetEditionAsync(edition.Value.Uuid))!.Value.Selected.Should().BeTrue();
        await Register(show.ShowUuid);
        var clear = await Command(show.ShowUuid, null);
        clear.ExpectedSelectedEditionUuid.Should().Be(edition.Value.Uuid);
        await music.ApplySelectionAsync(clear, "operator");
        var recordedBefore = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "SELECT (payload->>'ExpectedSelectedEditionUuid')::uuid FROM normalization.review_operations WHERE uuid=@OperationUuid",
                clear
            )
        );
        recordedBefore.Should().Be(edition.Value.Uuid);
        (await music.GetEditionAsync(edition.Value.Uuid))!.Revision.Should().Be(1);
    }

    [Test]
    public async Task RetainedSelectionCanBeClearedAfterShowDeletionButCannotBeSelectedAgain()
    {
        var show = await Show();
        var edition = await Edition(show);
        await Register(show.ShowUuid);
        var original = await Command(show.ShowUuid, edition.Value.Uuid);
        await music.ApplySelectionAsync(original, "operator");
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync("DELETE FROM shows WHERE uuid=@ShowUuid", show)
        );
        var context = (await music.ReadSelectionContextAsync(show.ShowUuid))!;
        context.ArtistUuid.Should().BeNull();
        context.SelectedEdition!.EditionUuid.Should().Be(edition.Value.Uuid);
        (await music.ApplySelectionAsync(original, "operator")).Replayed.Should().BeTrue();
        await music.ApplySelectionAsync(await Command(show.ShowUuid, null), "operator");
        var select = async () =>
            await music.ApplySelectionAsync(
                await Command(show.ShowUuid, edition.Value.Uuid),
                "operator"
            );
        await select.Should().ThrowAsync<ArgumentException>();
        (await music.ReadSelectionContextAsync(show.ShowUuid))!.SelectedEdition.Should().BeNull();
    }

    [Test]
    public async Task TargetMustBeAnActiveAccountOfTheExactShow()
    {
        var show = await Show();
        var other = await Show();
        var foreign = await Edition(other);
        await Register(show.ShowUuid);
        var select = async () =>
            await music.ApplySelectionAsync(
                await Command(show.ShowUuid, foreign.Value.Uuid),
                "operator"
            );
        await select.Should().ThrowAsync<ArgumentException>();
        (await music.ReadSelectionContextAsync(show.ShowUuid))!.SelectedEdition.Should().BeNull();
    }

    [Test]
    public async Task ConcurrentCommandsFromOneContextHaveOnlyOneWinner()
    {
        var show = await Show();
        var first = await Edition(show);
        var second = await Edition(show);
        await Register(show.ShowUuid);
        var one = await Command(show.ShowUuid, first.Value.Uuid);
        var two = await Command(show.ShowUuid, second.Value.Uuid);
        async Task<bool> Try(SetlistSelectionDecision decision)
        {
            try
            {
                await music.ApplySelectionAsync(decision, "operator");
                return true;
            }
            catch (NormalizationConflictException)
            {
                return false;
            }
        }
        var results = await Task.WhenAll(Try(one), Try(two));
        results.Count(success => success).Should().Be(1);
        (await music.GetShowEditionsAsync(show.ShowUuid))
            .Count(row => row.Value.Selected)
            .Should()
            .Be(1);
    }

    [Test]
    public async Task RowLockedMetadataRetractionAndSelectionSerializeWithoutPublishingAnInvalidChoice()
    {
        var show = await Show();
        var edition = await Edition(show);
        await Register(show.ShowUuid);
        var command = await Command(show.ShowUuid, edition.Value.Uuid);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await fixture.Db.WithWriteConnection(async connection =>
        {
            var barrier = (NpgsqlConnection)connection;
            await using var transaction = await barrier.BeginTransactionAsync(timeout.Token);
            await barrier.ExecuteAsync(
                new CommandDefinition(
                    "SELECT uuid FROM normalization.setlist_editions WHERE uuid=@Uuid FOR UPDATE",
                    edition.Value,
                    transaction,
                    cancellationToken: timeout.Token
                )
            );
            var pid = barrier.ProcessID;
            var writer = Update(
                edition,
                edition.Value with
                {
                    ExpectedRevision = 1,
                    Retracted = true,
                },
                timeout.Token
            );
            Task<NormalizationReviewResult>? selector = null;
            try
            {
                await WaitForBlocked(pid, timeout.Token);
                selector = music.ApplySelectionAsync(command, "operator", timeout.Token);
                await WaitForBlocked(pid, timeout.Token, 2);
            }
            finally
            {
                await transaction.RollbackAsync();
                // Drain both calls even when a barrier assertion fails, so no database task
                // escapes this fixture and changes a later test's accepted state.
                try
                {
                    Task[] inFlight = selector == null ? [writer] : [writer, selector];
                    await Task.WhenAll(inFlight);
                }
                catch (NormalizationConflictException) { }
            }
            await writer;
            var select = async () => await selector!;
            await select.Should().ThrowAsync<NormalizationConflictException>();
        });
        (await music.GetEditionAsync(edition.Value.Uuid)).Should().BeNull();
        (await music.ReadSelectionContextAsync(show.ShowUuid))!.SelectedEdition.Should().BeNull();
    }

    [Test]
    public async Task FailedSwitchRollsBackTheOldChoiceAndCanReplayTheSameCommand()
    {
        var show = await Show();
        var first = await Edition(show);
        var second = await Edition(show);
        await Register(show.ShowUuid);
        await music.ApplySelectionAsync(await Command(show.ShowUuid, first.Value.Uuid), "operator");
        var command = await Command(show.ShowUuid, second.Value.Uuid);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                $"""
                CREATE FUNCTION normalization.fail_selection_switch() RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN
                    IF NEW.uuid='{second.Value.Uuid:D}'::uuid AND NEW.selected THEN
                        RAISE EXCEPTION 'Injected selection failure';
                    END IF;
                    RETURN NEW;
                END $body$;
                CREATE TRIGGER fail_selection_switch BEFORE UPDATE ON normalization.setlist_editions
                    FOR EACH ROW EXECUTE FUNCTION normalization.fail_selection_switch();
                """
            )
        );
        try
        {
            var select = async () => await music.ApplySelectionAsync(command, "operator");
            var failure = await select.Should().ThrowAsync<Exception>();
            failure.WithInnerException<PostgresException>().Which.SqlState.Should().Be("P0001");
            var context = (await music.ReadSelectionContextAsync(show.ShowUuid))!;
            context.SelectedEdition!.EditionUuid.Should().Be(first.Value.Uuid);
            context.Owner!.EditorialRevision.Should().Be(command.ExpectedEditorialRevision);
        }
        finally
        {
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    "DROP TRIGGER fail_selection_switch ON normalization.setlist_editions; DROP FUNCTION normalization.fail_selection_switch()"
                )
            );
        }
        var result = await music.ApplySelectionAsync(command, "operator");
        (await music.ApplySelectionAsync(command, "operator"))
            .Should()
            .Be(result with { Replayed = true });
        (await music.ReadSelectionContextAsync(show.ShowUuid))!
            .SelectedEdition!.EditionUuid.Should()
            .Be(second.Value.Uuid);
    }

    [Test]
    public async Task CancellationWhileWaitingLeavesFlagsAndEditorialRevisionUnchangedAndCanRetry()
    {
        var show = await Show();
        var edition = await Edition(show);
        var owner = await Register(show.ShowUuid);
        var command = await Command(show.ShowUuid, edition.Value.Uuid);
        await fixture.Db.WithWriteConnection(async connection =>
        {
            var barrier = (NpgsqlConnection)connection;
            await using var transaction = await barrier.BeginTransactionAsync();
            await barrier.ExecuteAsync(
                "SELECT uuid FROM normalization.setlist_editions WHERE uuid=@Uuid FOR UPDATE",
                edition.Value,
                transaction
            );
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var select = music.ApplySelectionAsync(command, "operator", cancellation.Token);
            try
            {
                await WaitForBlocked(barrier.ProcessID, cancellation.Token);
                cancellation.Cancel();
                var apply = async () => await select;
                await apply.Should().ThrowAsync<OperationCanceledException>();
            }
            finally
            {
                cancellation.Cancel();
                await transaction.RollbackAsync();
                try
                {
                    await select;
                }
                catch (OperationCanceledException) { }
            }
        });
        (await fixture.Work.GetSubjectAsync(owner.SubjectUuid))!.EditorialRevision.Should().Be(0);
        (await music.GetEditionAsync(edition.Value.Uuid))!.Value.Selected.Should().BeFalse();
        var result = await music.ApplySelectionAsync(command, "operator");
        (await music.ApplySelectionAsync(command, "operator"))
            .Should()
            .Be(result with { Replayed = true });
    }

    private async Task WaitForBlocked(int pid, CancellationToken ct, int count = 1)
    {
        while (
            !await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<bool>(
                    new CommandDefinition(
                        """
                        WITH RECURSIVE blocked AS (
                            SELECT pid FROM pg_stat_activity WHERE @pid=ANY(pg_blocking_pids(pid))
                            UNION
                            SELECT waiting.pid FROM pg_stat_activity waiting
                                JOIN blocked parent ON parent.pid=ANY(pg_blocking_pids(waiting.pid))
                        ) SELECT count(*)>=@count FROM blocked
                        """,
                        new { pid, count },
                        cancellationToken: ct
                    )
                )
            )
        )
        {
            await Task.Delay(10, ct);
        }
    }

    private async Task<SetlistSelectionDecision> Command(Guid showUuid, Guid? editionUuid)
    {
        var context = (await music.ReadSelectionContextAsync(showUuid, editionUuid))!;
        return new(
            Guid.NewGuid(),
            context.Owner!.SubjectUuid,
            showUuid,
            context.Owner.EditorialRevision,
            context.SelectedEdition?.EditionUuid,
            editionUuid,
            context.ProposedEdition?.Revision,
            "Choose display account"
        );
    }

    private Task<NormalizationWorkTicket> Register(Guid showUuid) =>
        fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("songs", "show_setlist_selection", showUuid),
            "display-selection-v1",
            [],
            []
        );

    private async Task<ShowFixture> Show(int? artistId = null)
    {
        var id = artistId ?? (await fixture.Source()).ArtistId;
        return await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<ShowFixture>(
                """
                INSERT INTO shows(artist_id,date,display_date,uuid,updated_at,performance_key,is_legacy_survivor)
                VALUES(@id,'2026-10-17','2026-10-17',@uuid,now(),@key,false)
                RETURNING uuid AS ShowUuid,artist_id AS ArtistId,(SELECT uuid FROM artists WHERE id=@id) AS ArtistUuid
                """,
                new
                {
                    id,
                    uuid = Guid.NewGuid(),
                    key = Guid.NewGuid().ToString(),
                }
            )
        );
    }

    private async Task<EditionFixture> Edition(ShowFixture show)
    {
        var input = await fixture.Capture("Retained provider account", "songs");
        var value = new SetlistEditionChange
        {
            Uuid = Guid.NewGuid(),
            ArtistUuid = show.ArtistUuid,
            ShowUuid = show.ShowUuid,
            LiteralDate = "2026-10-17",
            Provider = "fixture",
            ResourceType = "setlist",
            ProviderKey = Guid.NewGuid().ToString(),
            Evidence = [input.Reference],
        };
        var ticket = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("songs", "setlist", value.Uuid),
            "selection-source-fixture",
            [input.Resource],
            []
        );
        await music.PublishAsync(ticket.WorkUuid, new() { Editions = [value] });
        return new(value, input, ticket);
    }

    private async Task Update(
        EditionFixture edition,
        SetlistEditionChange change,
        CancellationToken ct = default
    )
    {
        var ticket = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("songs", "setlist", change.Uuid),
            "selection-source-fixture",
            [edition.Input.Resource],
            [],
            cancellationToken: ct
        );
        await music.PublishAsync(ticket.WorkUuid, new() { Editions = [change] }, ct);
    }

    private Task<long> MusicHistoryCount() =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>("SELECT count(*) FROM normalization.music_revisions")
        );

    private sealed record ShowFixture(Guid ShowUuid, int ArtistId, Guid ArtistUuid);

    private sealed record EditionFixture(
        SetlistEditionChange Value,
        DomainInput Input,
        NormalizationWorkTicket Ticket
    );
}
