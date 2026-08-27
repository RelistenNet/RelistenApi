using Microsoft.AspNetCore.Antiforgery;

namespace RelistenUserService.Authentication;

public sealed class WebSessionAntiforgeryAdditionalDataProvider
    : IAntiforgeryAdditionalDataProvider
{
    public string GetAdditionalData(HttpContext context)
    {
        var currentAccount = context.RequestServices
            .GetRequiredService<CurrentAccountContext>();
        return currentAccount.IsWeb
            ? currentAccount.WebSessionId.ToString("N")
            : "";
    }

    public bool ValidateAdditionalData(HttpContext context, string additionalData)
    {
        var currentAccount = context.RequestServices
            .GetRequiredService<CurrentAccountContext>();
        return currentAccount.IsWeb
            ? string.Equals(
                additionalData,
                currentAccount.WebSessionId.ToString("N"),
                StringComparison.Ordinal)
            : string.IsNullOrEmpty(additionalData);
    }
}
