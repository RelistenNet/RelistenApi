using System;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;

namespace Relisten.Services.Popularity
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class SuccessExpirationAttribute : JobFilterAttribute, IApplyStateFilter
    {
        public SuccessExpirationAttribute(int minutes)
        {
            ExpirationTimeout = TimeSpan.FromMinutes(minutes);
        }

        public TimeSpan ExpirationTimeout { get; }

        public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            if (context.NewState is SucceededState)
            {
                context.JobExpirationTimeout = ExpirationTimeout;
            }
        }

        public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
        }
    }
}
