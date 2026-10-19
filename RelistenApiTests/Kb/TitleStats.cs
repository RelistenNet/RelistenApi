using Relisten.Kb.Titles;

namespace RelistenApiTests.Kb;

/// Parser and grouper statistics over exported title tables (title, position, tracks, shows
/// per line, tab-separated), for sizing and spot checks. Run explicitly with TITLE_STATS_DIR.
[Explicit]
public class TitleStats
{
    [Test]
    public void Report()
    {
        var dir = Environment.GetEnvironmentVariable("TITLE_STATS_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            Assert.Ignore("TITLE_STATS_DIR not set");
        }

        foreach (var file in Directory.GetFiles(dir!, "*.tsv").Order())
        {
            var rows = File.ReadLines(file)
                .Select(l => l.Split('\t'))
                .Where(f => f.Length == 4)
                .Select(f =>
                    (
                        Title: f[0],
                        Position: int.Parse(f[1]),
                        Tracks: int.Parse(f[2]),
                        Shows: int.Parse(f[3])
                    )
                )
                .ToList();
            var parsed = rows.Select(r => (r, Parsed: TitleParser.Parse(r.Title, r.Position)))
                .ToList();
            var tracks = rows.Sum(r => r.Tracks);
            var withText = parsed.Where(p => p.Parsed.HasText).Sum(p => p.r.Tracks);
            var usages = parsed
                .SelectMany(p => p.Parsed.Parts.Select(part => (part.Key, p.r)))
                .GroupBy(x => x.Key)
                .Select(g => new KeyUsage(
                    g.Key,
                    g.Sum(x => x.r.Tracks),
                    g.Sum(x => x.r.Shows),
                    g.Select(x => x.r.Title).Take(2).ToList()
                ))
                .ToList();
            var groups = TitleGrouper.Group(usages);

            TestContext.Out.WriteLine($"== {Path.GetFileNameWithoutExtension(file)}");
            TestContext.Out.WriteLine(
                $"distinct titles {rows.Count}, tracks {tracks}, with song text {(double)withText / tracks:P1}"
            );
            TestContext.Out.WriteLine(
                $"distinct keys {usages.Count}, groups {groups.Count}, groups in >=5 shows {groups.Count(g => g.Shows >= 5)} covering {(double)groups.Where(g => g.Shows >= 5).Sum(g => g.Tracks) / usages.Sum(u => u.Tracks):P1} of part uses"
            );
            TestContext.Out.WriteLine($"chunks (50 groups / 150 keys): {Chunks(groups)}");
            TestContext.Out.WriteLine(
                "top: "
                    + string.Join(
                        "; ",
                        groups
                            .Take(12)
                            .Select(g =>
                                string.Join("/", g.Keys.Take(3).Select(k => k.Key))
                                + $" ({g.Shows})"
                            )
                    )
            );
            TestContext.Out.WriteLine(
                "multi-key: "
                    + string.Join(
                        "; ",
                        groups
                            .Where(g => g.Keys.Count > 2)
                            .Take(8)
                            .Select(g => string.Join("/", g.Keys.Take(5).Select(k => k.Key)))
                    )
            );
            TestContext.Out.WriteLine(
                "no text: "
                    + string.Join(
                        "; ",
                        parsed
                            .Where(p => !p.Parsed.HasText)
                            .OrderByDescending(p => p.r.Tracks)
                            .Take(10)
                            .Select(p => p.r.Title)
                    )
            );
        }
    }

    private static int Chunks(IReadOnlyList<TitleGroup> groups)
    {
        var count = 0;
        for (var rest = groups.AsEnumerable(); rest.Any(); count++)
        {
            rest = rest.Skip(TitleGrouper.NextChunk(rest, 50, 150).Count).ToList();
        }

        return count;
    }
}
