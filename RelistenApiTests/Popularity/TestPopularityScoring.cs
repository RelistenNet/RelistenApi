using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using Relisten.Api.Models;
using Relisten.Services.Popularity;

namespace RelistenApiTests.Popularity;

[TestFixture]
public class TestPopularityScoring
{
    [Test]
    public void ComputeTrendRatio_ShouldReturnExpectedRatio()
    {
        var ratio = PopularityService.ComputeTrendRatio(plays48h: 96, plays90d: 4320);
        ratio.Should().BeApproximately(1.0, 0.0001);
    }

    [Test]
    public void ComputeTrendRatio_ShouldReturnZeroWhenNoBaseline()
    {
        PopularityService.ComputeTrendRatio(plays48h: 20, plays90d: 0).Should().Be(0);
    }

    [Test]
    public void CreateMetrics_ShouldUseSqrtForHotScore()
    {
        var metrics = PopularityService.CreateMetrics(plays30d: 100, plays7d: 49, plays6h: 9, plays48h: 10,
            plays90d: 900, seconds30d: 3600, seconds7d: 1800, seconds48h: 900);
        metrics.windows.days_30d.hot_score.Should().BeApproximately(10.0, 0.0001);
        metrics.windows.days_7d.hot_score.Should().BeApproximately(7.0, 0.0001);
        metrics.windows.days_30d.hours.Should().BeApproximately(1.0, 0.0001);
        metrics.windows.days_7d.hours.Should().BeApproximately(0.5, 0.0001);
        metrics.windows.hours_48h.hours.Should().BeApproximately(0.25, 0.0001);
    }

    [Test]
    public void ApplyMomentumScores_ShouldNormalizeBetweenZeroAndOne()
    {
        var metrics = new List<PopularityMetrics>
        {
            new() { trend_ratio = 1, windows = new PopularityWindows { days_30d = new PopularityWindowMetrics { hot_score = 10 } } },
            new() { trend_ratio = 2, windows = new PopularityWindows { days_30d = new PopularityWindowMetrics { hot_score = 20 } } },
            new() { trend_ratio = 3, windows = new PopularityWindows { days_30d = new PopularityWindowMetrics { hot_score = 30 } } }
        };

        PopularityService.ApplyMomentumScores(metrics);

        metrics[0].momentum_score.Should().BeApproximately(0.1, 0.0001);
        metrics[2].momentum_score.Should().BeApproximately(0.9, 0.0001);
        metrics[1].momentum_score.Should().BeInRange(0.0, 1.0);
    }
}
