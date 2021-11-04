using Microsoft.AspNetCore.Identity;

namespace Relisten.Services.Auth
{
    public class PlaintextHasher : IPasswordHasher<ApplicationUser>
    {
        public string HashPassword(ApplicationUser user, string password)
        {
            return password;
        }

        public PasswordVerificationResult VerifyHashedPassword(ApplicationUser user, string hashedPassword,
            string providedPassword)
        {
            return hashedPassword == providedPassword
                ? PasswordVerificationResult.Success
                : PasswordVerificationResult.Failed;
        }
    }
}
