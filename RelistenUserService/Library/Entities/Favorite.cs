namespace RelistenUserService.Library.Entities;

public sealed class Favorite
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string CatalogType { get; set; } = "";
    public Guid CatalogUuid { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
