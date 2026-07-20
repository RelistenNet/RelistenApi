using System.Text;
using System.Text.RegularExpressions;

namespace RelistenUserService.Identity.Usernames;

public sealed partial class UsernamePolicy
{
    public static readonly TimeSpan ChangeCooldown = TimeSpan.FromDays(30);

    public bool TryNormalize(string? value, out string username)
    {
        username = value?.ToLowerInvariant() ?? "";
        return UsernamePattern().IsMatch(username)
            && !UsernameDenylist.Contains(username);
    }

    public string? CandidateFromEmail(string? email)
    {
        var separator = email?.IndexOf('@') ?? -1;
        if (separator <= 0)
        {
            return null;
        }

        var builder = new StringBuilder(separator);
        var previousWasSeparator = false;
        foreach (var character in email![..separator].ToLowerInvariant())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(character);
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator)
            {
                builder.Append('_');
                previousWasSeparator = true;
            }
        }

        var candidate = builder.ToString().Trim('_');
        return TryNormalize(candidate, out var normalized) ? normalized : null;
    }

    [GeneratedRegex("^[a-z0-9_]{3,30}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();
}
