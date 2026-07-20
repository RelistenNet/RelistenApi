using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
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
    private Guid _userA;
    private Guid _userB;
    private Guid _catalogUuid;

    [OneTimeSetUp]
    public async Task SetUp()
    {
        await _database.StartAsync();
        _catalogUuid = Guid.NewGuid();
        _dataProtectionDirectory = new(
            Path.Combine(Path.GetTempPath(), $"relisten-cursors-{Guid.NewGuid():N}"));
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
    public async Task Missing_catalog_uuid_can_be_favorited_and_unfavorited_through_sync()
    {
        var missingCatalogUuid = Guid.NewGuid();
        var cursorProtector = new LibraryCursorProtector(_dataProtection);
        var before = await SnapshotAsync(_userA, cursorProtector);
        var add = Add(missingCatalogUuid);

        var addExecution = await ExecuteAsync(_userA, [add]);
        var addedSnapshot = await SnapshotAsync(_userA, cursorProtector);
        var removeExecution = await ExecuteAsync(_userA, [Remove(missingCatalogUuid)]);
        var currentSnapshot = await SnapshotAsync(_userA, cursorProtector);
        var changes = await ChangesAsync(_userA, before.NextCursor, cursorProtector);

        addExecution.Response!.Results.Should().ContainSingle().Which.Should()
            .Match<FavoriteMutationResult>(result =>
                result.Changed
                && result.CanonicalFavoriteUuid == add.FavoriteUuid
                && result.CatalogUuid == missingCatalogUuid);
        addedSnapshot.Favorites.Should().ContainSingle().Which.Should()
            .Match<FavoriteSnapshotItem>(favorite =>
                favorite.FavoriteUuid == add.FavoriteUuid
                && favorite.CatalogUuid == missingCatalogUuid);
        removeExecution.Response!.Results.Should().ContainSingle().Which.Should()
            .Match<FavoriteMutationResult>(result =>
                result.Changed
                && result.CanonicalFavoriteUuid == add.FavoriteUuid
                && result.CatalogUuid == missingCatalogUuid);
        currentSnapshot.Favorites.Should().BeEmpty();
        changes.Response!.Changes.Should().SatisfyRespectively(
            added => added.Should().Match<FavoriteLibraryChange>(change =>
                change.ChangeType == FavoriteChangeTypes.Added
                && change.FavoriteUuid == add.FavoriteUuid
                && change.CatalogUuid == missingCatalogUuid),
            removed => removed.Should().Match<FavoriteLibraryChange>(change =>
                change.ChangeType == FavoriteChangeTypes.Removed
                && change.FavoriteUuid == add.FavoriteUuid
                && change.CatalogUuid == missingCatalogUuid));
    }

    [Test]
    public async Task Retry_is_exact_and_the_first_membership_uuid_is_canonical()
    {
        var first = Add(_catalogUuid);
        var firstExecution = await ExecuteAsync(_userA, [first]);
        var second = Add(_catalogUuid);
        var secondExecution = await ExecuteAsync(_userA, [second]);
        var replay = await ExecuteAsync(_userA, [second]);

        var canonicalUuid = first.FavoriteUuid!.Value;
        firstExecution.Response!.Results.Single().Should().Match<FavoriteMutationResult>(result =>
            result.Changed && result.CanonicalFavoriteUuid == canonicalUuid);
        secondExecution.Response!.Results.Single().Should().Match<FavoriteMutationResult>(result =>
            !result.Changed
            && result.SubmittedFavoriteUuid == second.FavoriteUuid
            && result.CanonicalFavoriteUuid == canonicalUuid
            && result.LibraryRevision == 1);
        replay.Response!.Should().BeEquivalentTo(secondExecution.Response);

        var changedReuse = await ExecuteAsync(_userA,
        [
            new(
                second.MutationUuid,
                second.CatalogType,
                second.CatalogUuid,
                FavoriteDesiredStates.NotFavorite,
                null)
        ]);
        changedReuse.Failure!.Kind.Should().Be(FavoriteMutationFailureKind.IdempotencyConflict);

        await using var dbContext = _database.CreateContext();
        (await dbContext.Favorites.CountAsync(favorite => favorite.UserId == _userA))
            .Should().Be(1);
        (await dbContext.LibraryChanges.CountAsync(change => change.UserId == _userA))
            .Should().Be(1);
        (await dbContext.FavoriteMutationReceipts.CountAsync(receipt => receipt.UserId == _userA))
            .Should().Be(2);
    }

    [Test]
    public async Task Retry_preserves_its_original_response_after_an_intervening_mutation()
    {
        var add = Add(_catalogUuid);
        var original = await ExecuteAsync(_userA, [add]);
        var removal = await ExecuteAsync(_userA, [Remove(_catalogUuid)]);

        var retry = await ExecuteAsync(_userA, [add]);

        original.Response!.LibraryRevision.Should().Be(1);
        removal.Response!.LibraryRevision.Should().Be(2);
        retry.Response.Should().BeEquivalentTo(original.Response);

        await using var dbContext = _database.CreateContext();
        (await dbContext.Favorites.CountAsync(favorite => favorite.UserId == _userA))
            .Should().Be(0, "replaying a receipt must not reapply an obsolete desired state");
        (await dbContext.LibraryChanges.CountAsync(change => change.UserId == _userA))
            .Should().Be(2);
    }

    [Test]
    public async Task Snapshot_delta_and_cursor_are_bound_to_one_account()
    {
        var cursorProtector = new LibraryCursorProtector(_dataProtection);
        var before = await SnapshotAsync(_userA, cursorProtector);
        var mutation = Add(_catalogUuid);
        var mutationResult = await ExecuteAsync(_userA, [mutation]);

        var changes = await ChangesAsync(_userA, before.NextCursor, cursorProtector);
        changes.CursorExpired.Should().BeFalse();
        changes.Response!.LibraryRevision.Should().Be(mutationResult.Response!.LibraryRevision);
        changes.Response.Changes.Should().ContainSingle().Which.Should()
            .Match<FavoriteLibraryChange>(change =>
                change.ChangeType == FavoriteChangeTypes.Added
                && change.FavoriteUuid == mutation.FavoriteUuid
                && change.CatalogUuid == _catalogUuid);

        var crossAccountCursor = await ChangesAsync(_userB, before.NextCursor, cursorProtector);
        crossAccountCursor.CursorExpired.Should().BeTrue();

        var crossAccountMutation = await ExecuteAsync(_userB, [mutation]);
        crossAccountMutation.Failure!.Kind.Should()
            .Be(FavoriteMutationFailureKind.IdempotencyConflict);
        var userBSnapshot = await SnapshotAsync(_userB, cursorProtector);
        userBSnapshot.Favorites.Should().BeEmpty();
    }

    [Test]
    public async Task Snapshot_lock_prevents_membership_from_advancing_past_its_revision()
    {
        await using var snapshotContext = _database.CreateContext();
        await using var snapshotTransaction = await snapshotContext.Database.BeginTransactionAsync();
        var snapshotState = await new LibraryStateStore(snapshotContext).GetOrCreateAsync(
            _userA,
            DateTimeOffset.UtcNow,
            LibraryStateLockMode.SharedRead,
            CancellationToken.None);

        var mutationTask = ExecuteAsync(_userA, [Add(_catalogUuid)]);
        await Task.Delay(100);

        mutationTask.IsCompleted.Should().BeFalse(
            "the snapshot holds a share lock on the same state row that mutations update");
        snapshotState.Revision.Should().Be(0);
        (await snapshotContext.Favorites.CountAsync(favorite => favorite.UserId == _userA))
            .Should().Be(0);

        await snapshotTransaction.CommitAsync();
        var mutation = await mutationTask.WaitAsync(TimeSpan.FromSeconds(5));
        mutation.Response!.LibraryRevision.Should().Be(1);

        var current = await SnapshotAsync(_userA, new LibraryCursorProtector(_dataProtection));
        current.LibraryRevision.Should().Be(1);
        current.Favorites.Should().ContainSingle();
    }

    private async Task<FavoriteMutationExecution> ExecuteAsync(
        Guid userId,
        IReadOnlyList<FavoriteMutationCommand> mutations)
    {
        await using var dbContext = _database.CreateContext();
        var service = new FavoriteMutationService(
            dbContext,
            new AdvisoryLockService(dbContext),
            new LibraryStateStore(dbContext),
            TimeProvider.System);
        return await service.ExecuteAsync(userId, mutations, CancellationToken.None);
    }

    private async Task<FavoriteLibrarySnapshot> SnapshotAsync(
        Guid userId,
        LibraryCursorProtector cursorProtector)
    {
        await using var dbContext = _database.CreateContext();
        return await ReadService(dbContext, cursorProtector)
            .GetSnapshotAsync(userId, CancellationToken.None);
    }

    private async Task<LibraryChangesExecution> ChangesAsync(
        Guid userId,
        string cursor,
        LibraryCursorProtector cursorProtector)
    {
        await using var dbContext = _database.CreateContext();
        return await ReadService(dbContext, cursorProtector)
            .GetChangesAsync(userId, cursor, CancellationToken.None);
    }

    private static LibraryReadService ReadService(
        AccountsDbContext dbContext,
        LibraryCursorProtector cursorProtector) => new(
            dbContext,
            new LibraryStateStore(dbContext),
            cursorProtector,
            TimeProvider.System);

    private static FavoriteMutationCommand Add(Guid catalogUuid) => new(
        Guid.CreateVersion7(),
        FavoriteCatalogTypes.Artist,
        catalogUuid,
        FavoriteDesiredStates.Favorite,
        Guid.CreateVersion7());

    private static FavoriteMutationCommand Remove(Guid catalogUuid) => new(
        Guid.CreateVersion7(),
        FavoriteCatalogTypes.Artist,
        catalogUuid,
        FavoriteDesiredStates.NotFavorite,
        null);

    private static string UniqueUsername() => $"f_{Guid.NewGuid():N}"[..30];
}
