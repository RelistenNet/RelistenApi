using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api.Models.Api;
using Relisten.Controllers;
using Relisten.Services.Live;

namespace RelistenApiTests.Live;

[TestFixture]
public class TestLiveSummaryContract
{
    private static readonly DateTime Now = new(2025, 3, 15, 18, 30, 20, DateTimeKind.Utc);

    [Test]
    public void Summary_ShouldDeclareV3RouteAndLiveSummaryContract()
    {
        typeof(LiveSummaryController)
            .GetCustomAttribute<RouteAttribute>()!
            .Template.Should()
            .Be("api/v3");

        var method = typeof(LiveSummaryController).GetMethod(
            nameof(LiveSummaryController.Summary)
        )!;
        method.GetCustomAttribute<HttpGetAttribute>()!.Template.Should().Be("live/summary");
        method
            .GetCustomAttributes<ProducesResponseTypeAttribute>()
            .Single(attribute => attribute.StatusCode == 200)
            .Type.Should()
            .Be(typeof(LiveSummary));
    }

    [TestCase("3-15")]
    [TestCase("13-01")]
    [TestCase("02-30")]
    [TestCase("03-15-2025")]
    [TestCase("march")]
    public async Task Summary_ShouldRejectMalformedMonthDay(string monthDay)
    {
        var result = await NewController().Summary(month_day: monthDay);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Test]
    public async Task Summary_ShouldRejectUnknownSort()
    {
        var result = await NewController().Summary(sort: "listeners");

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Test]
    public void Request_ShouldAcceptLeapDay()
    {
        Create(monthDay: "02-29").MonthDay.Should().Be("02-29");
    }

    [Test]
    public void Request_ShouldDefaultMonthDayToTheUtcDateAndSortToSurprise()
    {
        var request = Create();

        request.MonthDay.Should().Be("03-15");
        request.Sort.Should().Be(LiveSummarySort.Surprise);
        request.Minutes.Should().Be(15);
        request.ShowsLimit.Should().Be(20);
        request.ArtistsLimit.Should().Be(10);
        request.MaxPerArtist.Should().Be(2);
        request.FreshDays.Should().Be(7);
    }

    [Test]
    public void Request_ShouldClampNumericOptionsToTheirRanges()
    {
        var low = Create(
            minutes: 1,
            showsLimit: 0,
            artistsLimit: -3,
            maxPerArtist: -1,
            freshDays: 0
        );
        low.Should()
            .BeEquivalentTo(
                new
                {
                    Minutes = 5,
                    ShowsLimit = 1,
                    ArtistsLimit = 1,
                    MaxPerArtist = 0,
                    FreshDays = 1,
                }
            );

        var high = Create(
            minutes: 600,
            showsLimit: 500,
            artistsLimit: 99,
            maxPerArtist: 99,
            freshDays: 365
        );
        high.Should()
            .BeEquivalentTo(
                new
                {
                    Minutes = 60,
                    ShowsLimit = 50,
                    ArtistsLimit = 25,
                    MaxPerArtist = 10,
                    FreshDays = 30,
                }
            );
    }

    [Test]
    public void CacheKey_ShouldChangeWithEveryOptionAndEachRefreshInterval()
    {
        var window = LiveWindow.EndingBefore(Now, 15);
        var baseline = Create().CacheKey(window);

        baseline.Should().StartWith("live:summary:v1:");
        new[]
        {
            Create(minutes: 30).CacheKey(window),
            Create(showsLimit: 10).CacheKey(window),
            Create(artistsLimit: 5).CacheKey(window),
            Create(monthDay: "11-03").CacheKey(window),
            Create(sort: "plays").CacheKey(window),
            Create(maxPerArtist: 0).CacheKey(window),
            Create(freshDays: 3).CacheKey(window),
            Create().CacheKey(LiveWindow.EndingBefore(Now.AddMinutes(1), 15)),
        }
            .Should()
            .OnlyHaveUniqueItems()
            .And.NotContain(baseline);
        Create().CacheKey(LiveWindow.EndingBefore(Now.AddSeconds(30), 15)).Should().Be(baseline);
    }

    [Test]
    public void Window_ShouldEndOnTheLastCompletedRefreshBoundary()
    {
        var window = LiveWindow.EndingBefore(Now, 15);

        window.End.Should().Be(new DateTime(2025, 3, 15, 18, 30, 0, DateTimeKind.Utc));
        window.Start.Should().Be(new DateTime(2025, 3, 15, 18, 15, 0, DateTimeKind.Utc));
        window.NextRefreshAt.Should().Be(window.End + LiveWindow.RefreshInterval);
    }

    private static LiveSummaryRequest Create(
        int minutes = LiveSummaryRequest.DefaultMinutes,
        int showsLimit = LiveSummaryRequest.DefaultShowsLimit,
        int artistsLimit = LiveSummaryRequest.DefaultArtistsLimit,
        string? monthDay = null,
        string? sort = null,
        int maxPerArtist = LiveSummaryRequest.DefaultMaxPerArtist,
        int freshDays = LiveSummaryRequest.DefaultFreshDays
    )
    {
        LiveSummaryRequest
            .TryCreate(
                minutes,
                showsLimit,
                artistsLimit,
                monthDay,
                sort,
                maxPerArtist,
                freshDays,
                Now,
                out var request,
                out var error
            )
            .Should()
            .BeTrue(error);
        return request;
    }

    // Validation runs before any service is used, so none are supplied.
    private static LiveSummaryController NewController() =>
        new(null!, null!, null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
}
