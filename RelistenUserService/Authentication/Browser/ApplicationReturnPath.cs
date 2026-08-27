namespace RelistenUserService.Authentication.Browser;

/// <summary>
/// Validates <c>return_to</c> before OpenIddict stores it in protected properties.
/// The auth host later appends the path to an allowlisted web origin during cookie clearing.
/// Alternate URL forms and fragments must not let either redirect leave the application.
/// </summary>
public static class ApplicationReturnPath
{
    public static bool TryValidate(string? value, out string path)
    {
        path = string.IsNullOrWhiteSpace(value) ? "/" : value;
        if (!path.StartsWith("/", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal)
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Any(char.IsControl)
            || !Uri.TryCreate($"https://relisten.invalid{path}", UriKind.Absolute, out var uri)
            || uri.GetLeftPart(UriPartial.Authority) != "https://relisten.invalid"
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            path = "";
            return false;
        }

        return true;
    }
}
