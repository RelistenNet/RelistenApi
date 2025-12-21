using System.Text.RegularExpressions;

namespace Relisten.Import;

public static class SlugUtils
{
    public static string Slugify(string full)
    {
        var slug = Regex.Replace(full.ToLower().Normalize(), @"['.]", "");
        slug = Regex.Replace(slug, @"[^a-z0-9\s-]", " ");

        return Regex.Replace(slug, @"\s+", " ").Trim().Replace(" ", "-").Trim('-');
    }
}
