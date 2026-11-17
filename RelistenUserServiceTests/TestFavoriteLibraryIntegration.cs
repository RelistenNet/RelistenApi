using System.Data.Common;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;
using Relisten.Accounts.Contracts.Library;
using RelistenUserService.Library;
using RelistenUserService.Persistence;

namespace RelistenUserServiceTests;

[TestFixture]
[NonParallelizable]
public sealed class TestFavoriteLibraryIntegration
{
    private readonly PostgresIntegrationDatabase _database = new();
    private IDataProtectionProvider _dataProtection = null!;
    private DirectoryInfo _dataProtectionDirectory = null!;
    private UserId _userA;
    private UserId _userB;
    private string _catalogId = "";

    [OneTimeSetUp]
    public async Task SetUp()
    {
        await _database.StartAsync();
        _catalogId = global::Relisten.Identifiers.UuidTypeId.Format("artist", Guid.NewGuid());
        _dataProtectionDirectory = new(
            Path.Combine(Path.GetTempPath(), $"relisten-cursors-{Guid.NewGuid():N}")
        );
        _dataProtection = DataProtectionProvider.Create(_dataProtectionDirectory);
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        await _database.StopAsync();
        if (_dataProtectionDirectory.Exists)
        {
            _dataProtectionDirectory.Delete(recursive: true);
        }
    }

    [SetUp]
    public async Task CreateUsers()
    {
        _userA = await _database.CreateUserAsync(UniqueUsername());
        _userB = await _database.CreateUserAsync(UniqueUsername());
    }

    [Test]
    public async Task Missing_catalog_id_can_be_favorited_and_unfavorited_through_sync()
    {
        var missingCatalogId = Relisten.Identifiers.UuidTypeId.Format("artist", Guid.NewGuid());
        var cursorProtector = new LibraryCursorProtector(_dataProtection);
        var before = await SnapshotAsync(_userA, cursorProtector);
        var add = Add(missingCatalogId);

        var addExecution = await ExecuteAsync(_userA, [add]);
        var addedSnapshot = await SnapshotAsync(_userA, cursorProtector);
        var removeExecution = await ExecuteAsync(_userA, [Remove(missingCatalogId)]);
        var currentSnapshot = await SnapshotAsync(_userA, cursorProtector);
        var changes = await ChangesAsync(_userA, before.NextCursor, cursorProtector);

        addExecution
            .Response!.Results.Should()
            .ContainSingle()
            .Which.Should()
            .Match<FavoriteMutationResult>(result =>
                result.Changed
                && result.CanonicalFavoriteId == add.FavoriteId
                && result.Target.Id == missingCatalogId
            );
        addedSnapshot
            .Favorites.Should()
            .ContainSingle()
            .Which.Should()
            .Match<FavoriteSnapshotItem>(favorite =>
                favorite.FavoriteId == add.FavoriteId && favorite.Target.Id == missingCatalogId
            );
        removeExecution
            .Response!.Results.Should()
            .ContainSingle()
            .Which.Should()
            .Match<FavoriteMutationResult>(result =>
                result.Changed
                && result.CanonicalFavoriteId == add.FavoriteId
                && result.Target.Id == missingCatalogId
            );
        currentSnapshot.Favorites.Should().BeEmpty();
        changes
            .Response!.Changes.Should()
            .SatisfyRespectively(
                added =>
                    added
                        .Should()
                        .Match<FavoriteLibraryChange>(change =>
                            change.ChangeType == FavoriteChangeTypes.Added
                            && change.FavoriteId == add.FavoriteId
                            && change.Target!.Id == missingCatalogId
                        ),
                removed =>
                    removed
                        .Should()
                        .Match<FavoriteLibraryChange>(change =>
                            change.ChangeType == FavoriteChangeTypes.Removed
                            && change.FavoriteId == add.FavoriteId
                            && change.Target!.Id == missingCatalogId
                        )
            );
    }

    [Test]
    public async Task Retry_is_exact_and_the_first_membership_uuid_is_canonical()
    {
        var first = Add(_catalogId);
        var firstExecution = await ExecuteAsync(_userA, [first]);
        var second = Add(_catalogId);
        var secondExecution = await ExecuteAsync(_userA, [second]);
        var replay = await ExecuteAsync(_userA, [second]);

        var canonicalUuid = first.FavoriteId!.Value;
        firstExecution
            .Response!.Results.Single()
            .Should()
            .Match<FavoriteMutationResult>(result =>
                result.Changed && result.CanonicalFavoriteId == canonicalUuid
            );
        secondExecution
            .Response!.Results.Single()
            .Should()
            .Match<FavoriteMutationResult>(result =>
                !result.Changed
                && result.SubmittedFavoriteId == second.FavoriteId
                && result.CanonicalFavoriteId == canonicalUuid
                && result.LibraryRevision == 1
            );
        replay.Response!.Should().BeEquivalentTo(secondExecution.Response);

        var changedReuse = await ExecuteAsync(
            _userA,
            [
                new(
                    second.MutationId,
                    second.CatalogType,
                    second.CatalogId,
                    FavoriteDesiredStates.NotFavorite,
                    null
                ),
            ]
        );
        changedReuse.Failure!.Kind.Should().Be(FavoriteMutationFailureKind.IdempotencyConflict);

        await using var dbContext = _database.CreateContext();
        (await dbContext.Favorites.CountAsync(favorite => favorite.UserId == _userA))
            .Should()
            .Be(1);
        (await dbContext.LibraryChanges.CountAsync(change => change.UserId == _userA))
            .Should()
            .Be(1);
        (await dbContext.FavoriteMutationReceipts.CountAsync(receipt => receipt.UserId == _userA))
            .Should()
            .Be(2);
    }

    [Test]
    public async Task Retry_preserves_its_original_response_after_an_intervening_mutation()
    {
        var add = Add(_catalogId);
        var original = await ExecuteAsync(_userA, [add]);
        var removal = await ExecuteAsync(_userA, [Remove(_catalogId)]);

        var retry = await ExecuteAsync(_userA, [add]);

        original.Response!.LibraryRevision.Should().Be(1);
        removal.Response!.LibraryRevision.Should().Be(2);
        retry.Response.Should().BeEquivalentTo(original.Response);

        await using var dbContext = _database.CreateContext();
        (await dbContext.Favorites.CountAsync(favorite => favorite.UserId == _userA))
            .Should()
            .Be(0, "replaying a receipt must not reapply an obsolete desired state");
        (await dbContext.LibraryChanges.CountAsync(change => change.UserId == _userA))
            .Should()
            .Be(2);
    }

    [Test]
    public async Task Snapshot_delta_and_cursor_are_bound_to_one_account()
    {
        var cursorProtector = new LibraryCursorProtector(_dataProtection);
        var before = await SnapshotAsync(_userA, cursorProtector);
        var mutation = Add(_catalogId);
        var mutationResult = await ExecuteAsync(_userA, [mutation]);

        var changes = await ChangesAsync(_userA, before.NextCursor, cursorProtector);
        changes.CursorExpired.Should().BeFalse();
        changes.Response!.LibraryRevision.Should().Be(mutationResult.Response!.LibraryRevision);
        changes
            .Response.Changes.Should()
            .ContainSingle()
            .Which.Should()
            .Match<FavoriteLibraryChange>(change =>
                change.ChangeType == FavoriteChangeTypes.Added
                && change.FavoriteId == mutation.FavoriteId
                && change.Target!.Id == _catalogId
            );

        var crossAccountCursor = await ChangesAsync(_userB, before.NextCursor, cursorProtector);
        crossAccountCursor.CursorExpired.Should().BeTrue();

        var crossAccountMutation = await ExecuteAsync(_userB, [mutation]);
        crossAccountMutation
            .Failure!.Kind.Should()
            .Be(FavoriteMutationFailureKind.IdempotencyConflict);
        var userBSnapshot = await SnapshotAsync(_userB, cursorProtector);
        userBSnapshot.Favorites.Should().BeEmpty();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Snapshot_stays_coherent_while_a_writer_commits(bool hasExistingState)
    {
        if (hasExistingState)
        {
            await ExecuteAsync(_userA, [Add(_catalogId)]);
        }

        var beforeRevision = hasExistingState ? 1 : 0;
        var addedCatalogId = Relisten.Identifiers.UuidTypeId.Format("artist", Guid.NewGuid());
        var reader = new ReadObserver(async () =>
        {
            var mutation = await ExecuteAsync(_userA, [Add(addedCatalogId)])
                .WaitAsync(TimeSpan.FromSeconds(5));
            mutation.Response!.LibraryRevision.Should().Be(beforeRevision + 1);
        });
        await using var snapshotContext = ObservedContext(reader);
        var cursorProtector = new LibraryCursorProtector(_dataProtection);

        var snapshot = await ReadService(snapshotContext, cursorProtector)
            .GetSnapshotAsync(_userA, CancellationToken.None);

        snapshot.LibraryRevision.Should().Be(beforeRevision);
        snapshot
            .Favorites.Select(favorite => favorite.Target.Id)
            .Should()
            .Equal(hasExistingState ? [_catalogId] : Array.Empty<string>());
        var next = await ChangesAsync(_userA, snapshot.NextCursor, cursorProtector);
        next.Response!.LibraryRevision.Should().Be(beforeRevision + 1);
        next.Response.Changes.Should().ContainSingle().Which.Target!.Id.Should().Be(addedCatalogId);
    }

    [Test]
    public async Task Delta_head_and_changes_stay_coherent_while_a_writer_commits()
    {
        var cursorProtector = new LibraryCursorProtector(_dataProtection);
        var before = await SnapshotAsync(_userA, cursorProtector);
        var first = Add(_catalogId);
        await ExecuteAsync(_userA, [first]);
        var second = Add(Relisten.Identifiers.UuidTypeId.Format("artist", Guid.NewGuid()));
        var reader = new ReadObserver(async () =>
        {
            var mutation = await ExecuteAsync(_userA, [second]).WaitAsync(TimeSpan.FromSeconds(5));
            mutation.Response!.LibraryRevision.Should().Be(2);
        });
        await using var deltaContext = ObservedContext(reader);

        var page = await ReadService(deltaContext, cursorProtector)
            .GetChangesAsync(_userA, before.NextCursor, CancellationToken.None);

        page.Response!.LibraryRevision.Should().Be(1);
        page.Response.Changes.Should()
            .ContainSingle()
            .Which.FavoriteId.Should()
            .Be(first.FavoriteId);
        page.Response.HasMore.Should().BeFalse();
        var next = await ChangesAsync(_userA, page.Response.NextCursor, cursorProtector);
        next.Response!.LibraryRevision.Should().Be(2);
        next.Response.Changes.Should()
            .ContainSingle()
            .Which.FavoriteId.Should()
            .Be(second.FavoriteId);
    }

    [Test]
    public async Task First_snapshot_and_unchanged_poll_do_not_initialize_library_state()
    {
        var cursorProtector = new LibraryCursorProtector(_dataProtection);
        var snapshot = await SnapshotAsync(_userA, cursorProtector);
        var reader = new ReadObserver();
        await using var context = ObservedContext(reader);

        var poll = await ReadService(context, cursorProtector)
            .GetChangesAsync(_userA, snapshot.NextCursor, CancellationToken.None);

        snapshot.LibraryRevision.Should().Be(0);
        snapshot.Favorites.Should().BeEmpty();
        snapshot.Playlists.Should().BeEmpty();
        poll.Response!.LibraryRevision.Should().Be(0);
        poll.Response.Changes.Should().BeEmpty();
        poll.Response.HasMore.Should().BeFalse();
        reader.CommandCount.Should().Be(1, "an unchanged poll only needs its generation and head");
        (await context.LibraryStates.CountAsync(state => state.UserId == _userA)).Should().Be(0);
    }

    private AccountsDbContext ObservedContext(DbCommandInterceptor interceptor)
    {
        var options = new DbContextOptionsBuilder<AccountsDbContext>();
        options.UseNpgsql(_database.ConnectionString).UseOpenIddict<Guid>();
        options.AddInterceptors(interceptor);
        return new(options.Options);
    }

    private async Task<FavoriteMutationExecution> ExecuteAsync(
        UserId userId,
        IReadOnlyList<FavoriteMutationCommand> mutations
    )
    {
        await using var dbContext = _database.CreateContext();
        var service = new FavoriteMutationService(
            dbContext,
            new AdvisoryLockService(dbContext),
            new LibraryStateStore(dbContext),
            TimeProvider.System
        );
        return await service.ExecuteAsync(userId, mutations, CancellationToken.None);
    }

    private async Task<FavoriteLibrarySnapshot> SnapshotAsync(
        UserId userId,
        LibraryCursorProtector cursorProtector
    )
    {
        await using var dbContext = _database.CreateContext();
        return await ReadService(dbContext, cursorProtector)
            .GetSnapshotAsync(userId, CancellationToken.None);
    }

    private async Task<LibraryChangesExecution> ChangesAsync(
        UserId userId,
        string cursor,
        LibraryCursorProtector cursorProtector
    )
    {
        await using var dbContext = _database.CreateContext();
        return await ReadService(dbContext, cursorProtector)
            .GetChangesAsync(userId, cursor, CancellationToken.None);
    }

    private static LibraryReadService ReadService(
        AccountsDbContext dbContext,
        LibraryCursorProtector cursorProtector
    ) => new(dbContext, cursorProtector);

    private sealed class ReadObserver(Func<Task>? afterFirstRead = null) : DbCommandInterceptor
    {
        public int CommandCount { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default
        )
        {
            CommandCount++;
            if (CommandCount == 1 && afterFirstRead is not null)
            {
                await afterFirstRead();
            }
            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default
        )
        {
            CommandCount++;
            return ValueTask.FromResult(result);
        }
    }

    private static FavoriteMutationCommand Add(string catalogId) =>
        new(
            MutationId.New(),
            FavoriteCatalogTypes.Artist,
            catalogId,
            FavoriteDesiredStates.Favorite,
            FavoriteId.New()
        );

    private static FavoriteMutationCommand Remove(string catalogId) =>
        new(
            MutationId.New(),
            FavoriteCatalogTypes.Artist,
            catalogId,
            FavoriteDesiredStates.NotFavorite,
            null
        );

    private static string UniqueUsername() => $"f_{Guid.NewGuid():N}"[..30];
}
