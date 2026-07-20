namespace RelistenUserService.Identity.Entities;

public sealed class UsernameCommandReceipt
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public long ExpectedUsernameVersion { get; set; }
    public byte[] PayloadHash { get; set; } = [];
    public string StoredResult { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
