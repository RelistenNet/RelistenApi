namespace Relisten.Accounts.Contracts.Errors;

public static class LibraryErrorCodes
{
    public const string CatalogUnavailable = "catalog_unavailable";
    public const string FavoriteUuidConflict = "favorite_uuid_conflict";
    public const string InvalidFavoriteMutation = "invalid_favorite_mutation";
    public const string LimitExceeded = "limit_exceeded";
    public const string QuotaExceeded = "quota_exceeded";
    public const string SyncCursorExpired = "sync_cursor_expired";
}
