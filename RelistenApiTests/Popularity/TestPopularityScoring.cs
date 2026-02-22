using System;
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

    [Test]
    public void ShouldApplyHistoricalAnniversaryPenalty_ShouldNotPenalizeBeforeAnniversary()
    {
        var shouldPenalize = ShowMomentumScoring.ShouldApplyHistoricalAnniversaryPenalty(
            showDate: new DateTime(1977, 12, 30),
            playDay: new DateTime(2026, 12, 29));

        shouldPenalize.Should().BeFalse();
    }

    [Test]
    public void ShouldApplyHistoricalAnniversaryPenalty_ShouldPenalizeWithinWindowAndStopAfterDay7()
    {
        ShowMomentumScoring.ShouldApplyHistoricalAnniversaryPenalty(
                showDate: new DateTime(1977, 2, 17),
                playDay: new DateTime(2026, 2, 17))
            .Should()
            .BeTrue();
        ShowMomentumScoring.ShouldApplyHistoricalAnniversaryPenalty(
                showDate: new DateTime(1977, 2, 17),
                playDay: new DateTime(2026, 2, 24))
            .Should()
            .BeTrue();
        ShowMomentumScoring.ShouldApplyHistoricalAnniversaryPenalty(
                showDate: new DateTime(1977, 2, 17),
                playDay: new DateTime(2026, 2, 25))
            .Should()
            .BeFalse();
    }

    [Test]
    public void ShouldApplyHistoricalAnniversaryPenalty_ShouldNotPenalizeSameYearShows()
    {
        var shouldPenalize = ShowMomentumScoring.ShouldApplyHistoricalAnniversaryPenalty(
            showDate: new DateTime(2026, 2, 1),
            playDay: new DateTime(2026, 2, 2));

        shouldPenalize.Should().BeFalse();
    }

    [Test]
    public void ShouldApplyHistoricalAnniversaryPenalty_ShouldNotPenalizeAcrossYearBoundary()
    {
        var shouldPenalize = ShowMomentumScoring.ShouldApplyHistoricalAnniversaryPenalty(
            showDate: new DateTime(2025, 12, 31),
            playDay: new DateTime(2026, 1, 1));

        shouldPenalize.Should().BeFalse();
    }

    [Test]
    public void AnniversaryDateForPlayDay_ShouldClampLeapDayToFeb28OnNonLeapYears()
    {
        var anniversary = ShowMomentumScoring.AnniversaryDateForPlayDay(
            showDate: new DateTime(2024, 2, 29),
            playDay: new DateTime(2025, 2, 28));

        anniversary.Should().Be(new DateTime(2025, 2, 28));
        ShowMomentumScoring.AnniversaryDayOffset(new DateTime(2024, 2, 29), new DateTime(2025, 2, 28))
            .Should()
            .Be(0);
    }

    [Test]
    public void OtdPenaltyWeightForDayOffset_ShouldDecayAndHardStop()
    {
        ShowMomentumScoring.OtdPenaltyWeightForDayOffset(0).Should().BeApproximately(1.0, 0.0001);
        ShowMomentumScoring.OtdPenaltyWeightForDayOffset(2).Should().BeApproximately(0.5, 0.0001);
        ShowMomentumScoring.OtdPenaltyWeightForDayOffset(8).Should().BeApproximately(0.0, 0.0001);
    }

    [Test]
    public void ComputeOrganicMomentumScore_ShouldApplyMaxReductionAtFullPenalty()
    {
        var organic = ShowMomentumScoring.ComputeOrganicMomentumScore(rawMomentumScore: 0.8, otdPenaltyRatio7d: 1.0);

        organic.Should().BeApproximately(0.4, 0.0001);
    }

    [Test]
    public void RankingMomentumScore_ShouldUseOrganicWhenAvailableAndFallbackToRaw()
    {
        var withOrganic = new CachedShowPopularity
        {
            momentum_score = 0.7,
            momentum = new CachedShowMomentum
            {
                raw = 0.7,
                organic = 0.2
            }
        };
        var withoutOrganic = new CachedShowPopularity
        {
            momentum_score = 0.6
        };

        CachedShowPopularityMapper.GetRankingMomentumScore(withOrganic).Should().BeApproximately(0.2, 0.0001);
        CachedShowPopularityMapper.GetRawMomentumScore(withOrganic).Should().BeApproximately(0.7, 0.0001);
        CachedShowPopularityMapper.GetRankingMomentumScore(withoutOrganic).Should().BeApproximately(0.6, 0.0001);
        CachedShowPopularityMapper.GetRawMomentumScore(withoutOrganic).Should().BeApproximately(0.6, 0.0001);
    }

    [Test]
    public void ConvertLegacyShowPopularityMap_ShouldPreserveRawFields()
    {
        var showUuid = Guid.NewGuid();
        var legacy = new Dictionary<Guid, PopularityMetrics>
        {
            [showUuid] = new()
            {
                momentum_score = 0.42,
                trend_ratio = 1.23,
                plays_6h = 12,
                plays_90d = 34,
                windows = new PopularityWindows
                {
                    days_7d = new PopularityWindowMetrics { plays = 56, hot_score = 7.8 }
                }
            }
        };

        var converted = CachedShowPopularityMapper.ConvertLegacyShowPopularityMap(legacy);
        converted.Should().ContainKey(showUuid);
        var value = converted[showUuid];

        value.momentum_score.Should().BeApproximately(0.42, 0.0001);
        value.trend_ratio.Should().BeApproximately(1.23, 0.0001);
        value.plays_6h.Should().Be(12);
        value.plays_90d.Should().Be(34);
        value.windows.days_7d.plays.Should().Be(56);
        value.momentum.Should().BeNull();
    }
}
