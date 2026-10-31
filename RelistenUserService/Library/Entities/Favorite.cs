namespace RelistenUserService.Library.Entities;

public sealed class Favorite
{
    public FavoriteId Id { get; set; }
    public UserId UserId { get; set; }
    public string CatalogType { get; set; } = "";
    public string CatalogId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
