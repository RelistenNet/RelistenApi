using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Relisten.Services.Popularity;

namespace RelistenApiTests.Popularity;

internal sealed class RecordingPopularityJobs : IBackgroundJobClient
{
    public List<PopularityPopulation[]> Batches { get; } = [];
    public bool FailNext { get; set; }

    public string Create(Job job, IState state)
    {
        if (FailNext)
        {
            FailNext = false;
            throw new BackgroundJobClientException(
                "Fixture queue unavailable",
                new InvalidOperationException()
            );
        }

        Batches.Add((PopularityPopulation[])job.Args[0]);
        return "fixture";
    }

    public bool ChangeState(string jobId, IState state, string expectedState) =>
        throw new NotSupportedException();
}
