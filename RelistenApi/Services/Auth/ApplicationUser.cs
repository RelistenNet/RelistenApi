using System.Security.Claims;

namespace Relisten.Services.Auth
{
    public class ApplicationUser : ClaimsIdentity
    {
        public string Username { get; set; } = null!;
        public string Password { get; set; } = null!;
    }
}
