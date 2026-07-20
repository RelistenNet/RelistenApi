using System.Text;

namespace RelistenUserService.Identity.Usernames;

internal static class UsernameDenylist
{
    private static readonly HashSet<string> ExactNames = new(StringComparer.Ordinal)
    {
        "about", "account", "accounts", "admin", "administrator", "anonymous",
        "api", "app", "apple", "auth", "billing", "blog", "callback", "catalog",
        "community", "contact", "dashboard", "deleted", "developer", "developers",
        "discover", "download", "email", "explore", "favorites", "google", "help",
        "home", "info", "legal", "library", "listener", "login", "logout", "mail",
        "me", "moderator", "music", "news", "null", "oauth", "official", "owner",
        "playlist", "playlists", "privacy", "profile", "recommendations", "register",
        "relisten", "root", "search", "security", "settings", "signup", "sonos",
        "staff", "status", "support", "system", "terms", "undefined", "user",
        "users", "webmaster", "www"
    };

    private static readonly string[] ImpersonationEdges =
    [
        "admin", "moderator", "official", "security"
    ];

    private static readonly HashSet<string> BoundaryTerms = new(StringComparer.Ordinal)
    {
        "rape", "rapist", "support"
    };

    // These are intentionally checked after removing underscores and common digit swaps.
    // The list is launch-sized and reviewable; moderation can expand it without changing
    // username storage or the public API.
    private static readonly string[] AbuseFragments =
    [
        "asshole", "bastard", "bitch", "chink", "cocksucker", "cunt", "dickhead",
        "faggot", "fuck", "gook", "kike", "motherfucker", "nazi", "nigger",
        "pedophile", "pedo", "porn", "retard", "shit", "slut",
        "spic", "tranny", "whore"
    ];

    public static bool Contains(string username)
    {
        if (ExactNames.Contains(username))
        {
            return true;
        }

        var folded = Fold(username);
        if (folded.Contains("relisten", StringComparison.Ordinal)
            || ImpersonationEdges.Any(term =>
                folded.StartsWith(term, StringComparison.Ordinal)
                || folded.EndsWith(term, StringComparison.Ordinal)))
        {
            return true;
        }


        // A few short terms occur inside ordinary words (for example, supporter,
        // grapefruit, and therapist). Treat them as complete underscore-delimited
        // tokens while still catching their exact folded/leet forms.
        if (BoundaryTerms.Contains(folded)
            || username.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(Fold)
                .Any(BoundaryTerms.Contains))
        {
            return true;
        }

        return AbuseFragments.Any(folded.Contains);
    }

    private static string Fold(string username)
    {
        var folded = new StringBuilder(username.Length);
        foreach (var character in username)
        {
            if (character == '_')
            {
                continue;
            }

            folded.Append(character switch
            {
                '0' => 'o',
                '1' => 'i',
                '3' => 'e',
                '4' => 'a',
                '5' => 's',
                '7' => 't',
                '8' => 'b',
                '9' => 'g',
                _ => character
            });
        }

        return folded.ToString();
    }
}
