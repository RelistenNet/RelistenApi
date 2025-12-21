namespace Relisten.Services.Auth
{
    public class ApplicationRole
    {
        public string RoleId { get; set; } = null!;
        public string? RoleName { get; set; }
        public string? RoleNameNormalized => RoleName?.ToUpperInvariant();
    }
}
