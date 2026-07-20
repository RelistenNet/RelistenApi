namespace RelistenUserService.Library.Entities;

public sealed class FavoriteMutationReceipt
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string CatalogType { get; set; } = "";
    public Guid CatalogUuid { get; set; }
    public string DesiredState { get; set; } = "";
    public Guid? SubmittedFavoriteId { get; set; }
    public byte[] PayloadHash { get; set; } = [];
    public bool Changed { get; set; }
    public Guid? CanonicalFavoriteId { get; set; }
    public long LibraryRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
