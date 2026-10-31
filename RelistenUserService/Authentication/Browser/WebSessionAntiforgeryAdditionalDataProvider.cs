using Microsoft.AspNetCore.Antiforgery;
using RelistenUserService.Authentication.Authorization;

namespace RelistenUserService.Authentication.Browser;

public sealed class WebSessionAntiforgeryAdditionalDataProvider : IAntiforgeryAdditionalDataProvider
{
    public string GetAdditionalData(HttpContext context)
    {
        var currentAccount = context.RequestServices.GetRequiredService<CurrentAccountContext>();
        return currentAccount.IsWeb ? currentAccount.WebSessionId.ToString() : "";
    }

    public bool ValidateAdditionalData(HttpContext context, string additionalData)
    {
        var currentAccount = context.RequestServices.GetRequiredService<CurrentAccountContext>();
        return currentAccount.IsWeb
            ? string.Equals(
                additionalData,
                currentAccount.WebSessionId.ToString(),
                StringComparison.Ordinal
            )
            : string.IsNullOrEmpty(additionalData);
    }
}
