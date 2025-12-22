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
        var ratio = PopularityService.ComputeTrendRatio(plays30d: 300, plays48h: 20);
        ratio.Should().BeApproximately(1.0, 0.0001);
    }

    [Test]
    public void ComputeTrendRatio_ShouldReturnZeroWhenNoBaseline()
    {
        PopularityService.ComputeTrendRatio(plays30d: 0, plays48h: 20).Should().Be(0);
    }

    [Test]
    public void CreateMetrics_ShouldUseSqrtForHotScore()
    {
        var metrics = PopularityService.CreateMetrics(plays30d: 100, plays48h: 10);
        metrics.hot_score.Should().BeApproximately(10.0, 0.0001);
    }

    [Test]
    public void ApplyMomentumScores_ShouldNormalizeBetweenZeroAndOne()
    {
        var metrics = new List<PopularityMetrics>
        {
            new() { trend_ratio = 1, hot_score = 10 },
            new() { trend_ratio = 2, hot_score = 20 },
            new() { trend_ratio = 3, hot_score = 30 }
        };

        PopularityService.ApplyMomentumScores(metrics);

        metrics[0].momentum_score.Should().BeApproximately(0.0, 0.0001);
        metrics[2].momentum_score.Should().BeApproximately(1.0, 0.0001);
        metrics[1].momentum_score.Should().BeInRange(0.0, 1.0);
    }
}
