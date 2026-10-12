using System;
using System.Threading;
using Temporalio.Activities;

namespace Relisten.Ingestion;

/// <summary>Delivers cancellation while an activity is waiting on HTTP or database I/O.</summary>
internal sealed class ActivityHeartbeat : IDisposable
{
    private readonly Timer timer;

    public ActivityHeartbeat()
    {
        var activity = ActivityExecutionContext.Current;
        // The SDK throttles these calls. A timer keeps cancellation available even
        // during a slow GET; an activity with no heartbeats cannot receive it.
        timer = new Timer(_ => activity.Heartbeat(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    public void Dispose() => timer.Dispose();
}
