using System;
using System.Threading;
using Temporalio.Activities;

namespace Relisten.Normalization.Temporal;

/// Heartbeats the current activity every two seconds while it runs, so a long database step
/// can still receive cancellation (the SDK throttles the calls).
internal sealed class ActivityHeartbeat : IDisposable
{
    private readonly Timer timer;

    public ActivityHeartbeat()
    {
        var activity = ActivityExecutionContext.Current;
        timer = new Timer(_ => activity.Heartbeat(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    public void Dispose() => timer.Dispose();
}
