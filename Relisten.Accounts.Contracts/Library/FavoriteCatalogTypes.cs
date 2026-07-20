namespace Relisten.Accounts.Contracts.Library;

public static class FavoriteCatalogTypes
{
    public const string Artist = "artist";
    public const string Show = "show";
    public const string Source = "source";
    public const string SourceTrack = "source_track";
    public const string Song = "song";
    public const string Tour = "tour";
    public const string Venue = "venue";

    public static bool IsSupported(string? value) => value is
        Artist or Show or Source or SourceTrack or Song or Tour or Venue;
}

public static class FavoriteDesiredStates
{
    public const string Favorite = "favorite";
    public const string NotFavorite = "not_favorite";

    public static bool IsSupported(string? value) => value is Favorite or NotFavorite;
}

public static class FavoriteChangeTypes
{
    public const string Added = "favorite_added";
    public const string Removed = "favorite_removed";
}
