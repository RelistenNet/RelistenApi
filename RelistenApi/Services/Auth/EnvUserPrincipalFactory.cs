using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Relisten.Services.Auth
{
    public class EnvUserPrincipalFactory : IUserClaimsPrincipalFactory<ApplicationUser>
    {
        private readonly IdentityOptions _options;

        public EnvUserPrincipalFactory(IOptions<IdentityOptions> optionsAccessor)
        {
            _options = optionsAccessor?.Value ?? new IdentityOptions();
        }

        public Task<ClaimsPrincipal> CreateAsync(ApplicationUser user)
        {
            var identity = new ClaimsIdentity(
                CookieAuthenticationDefaults.AuthenticationScheme,
                _options.ClaimsIdentity.UserNameClaimType,
                _options.ClaimsIdentity.RoleClaimType);

            identity.AddClaim(new Claim(_options.ClaimsIdentity.UserIdClaimType, user.Username));

            var principal = new ClaimsPrincipal(identity);

            return Task.FromResult(principal);
        }
    }
}
