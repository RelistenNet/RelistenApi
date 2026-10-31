namespace RelistenUserService.Library.Entities;

public sealed class FavoriteMutationReceipt
{
    public MutationId Id { get; set; }
    public UserId UserId { get; set; }
    public string CatalogType { get; set; } = "";
    public string CatalogId { get; set; } = "";
    public string DesiredState { get; set; } = "";
    public FavoriteId? SubmittedFavoriteId { get; set; }
    public byte[] PayloadHash { get; set; } = [];
    public bool Changed { get; set; }
    public FavoriteId? CanonicalFavoriteId { get; set; }
    public long LibraryRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
