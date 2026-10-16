using Newtonsoft.Json;

namespace RelistenApiTests.Ingestion;

// Small complete upstream documents. Each setlist provider has two dates in one
// tour so tests detect state lost when the importer is recreated for every item.
internal static class ProviderFixtures
{
    private static readonly int[] Days = [2, 1];
    private const string Updated = "2026-01-01T00:00:00Z";

    public static string Response(string provider, Uri uri) =>
        provider switch
        {
            "local" => Local(uri),
            "phish.in" => Phishin(uri),
            "setlist.fm" => Json(
                new
                {
                    page = 1,
                    total = 2,
                    itemsPerPage = 20,
                    setlist = Days.Select(day => new
                    {
                        id = "setlist-" + day,
                        eventDate = $"0{day}-01-2000",
                        lastUpdated = Updated,
                        venue = new
                        {
                            id = "venue",
                            name = "Fixture Venue",
                            city = new { name = "City", state = "State" },
                        },
                        tour = new { name = "Fixture Tour" },
                        sets = new
                        {
                            set = new[] { new { song = new[] { new { name = "Fixture Song" } } } },
                        },
                    }),
                }
            ),
            "phantasytour.com" => Phantasy(uri),
            "jerrygarcia.com" => uri.AbsolutePath.EndsWith("show_pages.json")
                ? Json(Days.Select(day => $"2000-01-0{day}-fixture.html"))
                : """<p class="venue-name">Fixture Venue</p><p class="venue-name">Band</p><p class="venue-name">Fixture Tour</p><p class="venue-address">City, State</p><p class="venue-country">USA</p><ol><li>Fixture Song</li></ol>""",
            "phish.net" => uri.AbsolutePath.Contains("/reviews/")
                ? Json(
                    new
                    {
                        error = false,
                        data = new[]
                        {
                            new
                            {
                                reviewid = 1,
                                username = "Fixture",
                                review_text = "Fixture review",
                                score = 4,
                                posted_at = "2026-01-01 00:00:00",
                            },
                        },
                    }
                )
            : uri.AbsolutePath.StartsWith("/v5/shows")
                ? Json(
                    new
                    {
                        error = false,
                        data = new[]
                        {
                            new
                            {
                                showdate = "2000-01-01",
                                artist_name = "Phish",
                                setlist_notes = "Fixture notes",
                            },
                        },
                    }
                )
            : "Overall: 4.2/5 (10 ratings)<div class='tpc-comment review'>Review</div>",
            _ => throw new ArgumentException(provider),
        };

    private static string Local(Uri uri)
    {
        if (uri.AbsolutePath == "/Fixture/metadata.json")
            return Json(
                new
                {
                    success = true,
                    data = new Dictionary<string, object>
                    {
                        ["2000-01-01"] = new[] { "part-one", "part-two" }.Select(dir => new
                        {
                            date = "2000-01-01",
                            dir,
                            venue = "Fixture Venue",
                            city = "City",
                            state = "State",
                            updated_at = Updated,
                        }),
                    },
                }
            );
        var part = uri.AbsolutePath.Contains("part-one") ? "First" : "Second";
        return Json(
            new
            {
                txts = new[] { part + " notes" },
                tracks = new[]
                {
                    new
                    {
                        mp3 = "track.mp3",
                        tags = new
                        {
                            title = part + " track",
                            duration = 120,
                            track = new { no = 1, of = 1 },
                        },
                    },
                },
            }
        );
    }

    private static string Phantasy(Uri uri)
    {
        if (uri.AbsolutePath.Contains("/bands/"))
            return Json(
                Days.Select(day => new
                {
                    id = day,
                    dateTime = $"2000-01-0{day}T00:00:00Z",
                    hasSetlist = true,
                })
            );
        var id = int.Parse(uri.Segments[^2].Trim('/'));
        return Json(
            new
            {
                data = new
                {
                    id,
                    dateTime = $"2000-01-0{id}T20:00:00-05:00",
                    dateTimeUtc = $"2000-01-0{id + 1}T01:00:00Z",
                    venue = new
                    {
                        id = 1,
                        name = "Fixture Venue",
                        city = "City",
                        state = "State",
                        country = "USA",
                    },
                    tour = new { id = 1, name = "Fixture Tour" },
                    sets = new[] { new { songs = new[] { new { name = "Fixture Song" } } } },
                },
            }
        );
    }

    private static string Phishin(Uri uri)
    {
        object data = Path.GetFileNameWithoutExtension(uri.AbsolutePath) switch
        {
            "eras" => new Dictionary<string, string[]> { ["1.0"] = ["1983-1987", "2000"] },
            "tours" => new[]
            {
                new
                {
                    id = 1,
                    name = "Fixture Tour",
                    starts_on = "2000-01-01",
                    ends_on = "2000-01-02",
                    updated_at = Updated,
                },
            },
            "songs" => new[]
            {
                new
                {
                    id = 1,
                    title = "Fixture Song",
                    slug = "fixture-song",
                    updated_at = Updated,
                },
            },
            "venues" => new[]
            {
                new
                {
                    id = 1,
                    name = "Fixture Venue",
                    location = "City, State",
                    updated_at = Updated,
                },
            },
            "shows" => Days.Select(day => new
            {
                id = day,
                date = $"2000-01-0{day}",
                updated_at = Updated,
                venue = new { id = 1 },
                tour_id = 1,
                taper_notes = "Fixture notes",
                tags = Array.Empty<object>(),
                tracks = new[]
                {
                    new
                    {
                        id = day,
                        title = "Fixture Song",
                        duration = 120000,
                        position = 1,
                        set = "1",
                        set_name = "Set 1",
                        mp3 = $"https://audio.invalid/{day}.mp3",
                        song_ids = new[] { 1 },
                    },
                },
            }),
            _ => throw new InvalidOperationException(
                "Unexpected fixture request: " + uri.AbsolutePath
            ),
        };
        return Json(
            new
            {
                success = true,
                total_pages = 1,
                total_entries = 2,
                page = 1,
                data,
            }
        );
    }

    private static string Json(object value) => JsonConvert.SerializeObject(value);
}
