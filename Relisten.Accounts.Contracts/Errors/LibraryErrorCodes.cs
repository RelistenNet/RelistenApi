namespace Relisten.Accounts.Contracts.Errors;

public static class LibraryErrorCodes
{
    public const string FavoriteIdConflict = "favorite_id_conflict";
    public const string InvalidFavoriteMutation = "invalid_favorite_mutation";
    public const string LimitExceeded = "limit_exceeded";
    public const string QuotaExceeded = "quota_exceeded";
    public const string SyncCursorExpired = "sync_cursor_expired";
}
