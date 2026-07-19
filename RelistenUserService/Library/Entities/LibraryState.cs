namespace RelistenUserService.Library.Entities;

public sealed class LibraryState
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
