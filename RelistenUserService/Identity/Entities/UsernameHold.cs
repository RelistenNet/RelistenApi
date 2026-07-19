namespace RelistenUserService.Identity.Entities;

public sealed class UsernameHold
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Username { get; set; } = "";
    public DateTimeOffset ReleaseAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
