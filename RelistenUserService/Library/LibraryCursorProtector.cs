using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace RelistenUserService.Library;

public sealed class LibraryCursorProtector
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(180);
    private readonly ITimeLimitedDataProtector _protector;

    public LibraryCursorProtector(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider
            .CreateProtector("Relisten.UserService.LibraryCursor.typed.v3")
            .ToTimeLimitedDataProtector();
    }

    public string Protect(UserId userId, long revision, string historyGeneration)
    {
        var payload = Encoding.UTF8.GetBytes($"{userId.Value}\n{revision}\n{historyGeneration}");
        return WebEncoders.Base64UrlEncode(_protector.Protect(payload, Lifetime));
    }

    public bool TryUnprotect(
        string? cursor,
        UserId expectedUserId,
        out long revision,
        out string historyGeneration
    )
    {
        revision = 0;
        historyGeneration = "";
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        try
        {
            var payload = _protector.Unprotect(WebEncoders.Base64UrlDecode(cursor), out _);
            var fields = Encoding.UTF8.GetString(payload).Split('\n');
            historyGeneration = fields.Length == 3 ? fields[2] : "";
            return fields.Length == 3
                && UserId.TryParse(fields[0], out var userId)
                && userId == expectedUserId
                && long.TryParse(
                    fields[1],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out revision
                )
                && revision >= 0;
        }
        catch (Exception exception)
            when (exception is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }
}
