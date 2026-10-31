namespace RelistenUserService.Library.Entities;

public sealed class LibraryChange
{
    public LibraryChangeId Id { get; set; }
    public UserId UserId { get; set; }
    public long Revision { get; set; }
    public string ChangeType { get; set; } = "";
    public FavoriteId FavoriteId { get; set; }
    public string CatalogType { get; set; } = "";
    public string CatalogId { get; set; } = "";
    public DateTimeOffset ChangedAt { get; set; }
}
