using System;
using Relisten.Api.Models;

namespace Relisten.Services.Popularity;

/// <summary>Additive counts for disjoint files, with the same windows as v4 popularity.</summary>
internal readonly record struct PlayPopularity(
    long Plays48h,
    double Seconds48h,
    long Plays7d,
    double Seconds7d,
    long Plays30d,
    double Seconds30d,
    long Plays90d,
    long Plays1y,
    double Seconds1y,
    long Plays6h = 0
)
{
    public bool HasPlays => Plays1y > 0 || Plays30d > 0 || Plays7d > 0 || Plays48h > 0;

    public static PlayPopularity operator +(PlayPopularity left, PlayPopularity right) =>
        new(
            left.Plays48h + right.Plays48h,
            left.Seconds48h + right.Seconds48h,
            left.Plays7d + right.Plays7d,
            left.Seconds7d + right.Seconds7d,
            left.Plays30d + right.Plays30d,
            left.Seconds30d + right.Seconds30d,
            left.Plays90d + right.Plays90d,
            left.Plays1y + right.Plays1y,
            left.Seconds1y + right.Seconds1y,
            left.Plays6h + right.Plays6h
        );

    // A medley credits each song with a start, but each share accounts for
    // only its part of the file's listening time.
    public PlayPopularity SplitTime(int songs) =>
        this with
        {
            Seconds48h = Seconds48h / songs,
            Seconds7d = Seconds7d / songs,
            Seconds30d = Seconds30d / songs,
            Seconds1y = Seconds1y / songs,
        };

    public PopularityMetrics ToMetrics()
    {
        var metrics = PopularityService.CreateMetrics(
            Plays30d,
            Plays7d,
            Plays6h,
            Plays48h,
            Plays90d,
            Seconds30d,
            Seconds7d,
            Seconds48h
        );
        metrics.windows.Year = new()
        {
            plays = Plays1y,
            hours = Seconds1y / 3600.0,
            hot_score = Math.Sqrt(Plays1y),
        };
        return metrics;
    }
}

internal class PlayPopularityRow
{
    public Guid Id { get; init; }
    public bool IsRecording { get; init; }
    public long Plays48h { get; init; }
    public long Seconds48h { get; init; }
    public long Plays7d { get; init; }
    public long Seconds7d { get; init; }
    public long Plays30d { get; init; }
    public long Seconds30d { get; init; }
    public long Plays90d { get; init; }
    public long Plays1y { get; init; }
    public long Seconds1y { get; init; }
    public long Plays6h { get; init; }

    public PlayPopularity ToPopularity() =>
        new(
            Plays48h,
            Seconds48h,
            Plays7d,
            Seconds7d,
            Plays30d,
            Seconds30d,
            Plays90d,
            Plays1y,
            Seconds1y,
            Plays6h
        );
}
