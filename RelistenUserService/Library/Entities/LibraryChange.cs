namespace RelistenUserService.Library.Entities;

public sealed class LibraryChange
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public long Revision { get; set; }
    public string ChangeType { get; set; } = "";
    public Guid FavoriteId { get; set; }
    public string CatalogType { get; set; } = "";
    public Guid CatalogUuid { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
}
