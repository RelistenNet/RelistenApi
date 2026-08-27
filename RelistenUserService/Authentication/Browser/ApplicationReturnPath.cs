namespace RelistenUserService.Authentication.Browser;

public static class ApplicationReturnPath
{
    public static bool TryValidate(string? value, out string path)
    {
        path = string.IsNullOrWhiteSpace(value) ? "/" : value;
        if (!path.StartsWith("/", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal)
            || path.StartsWith("/\\", StringComparison.Ordinal)
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
