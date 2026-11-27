using System.Net;
using Microsoft.EntityFrameworkCore;
using RelistenUserService.Persistence;
using Temporalio.Activities;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace RelistenUserService.Sonos;

public sealed class SonosGroupEvents(
    AccountsDbContext database,
    SonosProtocol protocol,
    SonosWorkflowStarter workflows,
    TimeProvider clock,
    ILogger<SonosGroupEvents> logger
)
{
    internal async Task SubscribeAsync(
        string token,
        IEnumerable<string> households,
        CancellationToken ct
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            foreach (var household in households.Distinct())
            {
                await protocol.ControlAsync(
                    token,
                    $"households/{Uri.EscapeDataString(household)}/groups/subscription",
                    new { },
                    deadline.Token
                );
            }
        }
        catch (Exception error)
            when (!ct.IsCancellationRequested
                && error
                    is (
                        SonosRequestException
                        or HttpRequestException
                        or OperationCanceledException
                        or System.Text.Json.JsonException
                    )
            )
        {
            logger.LogWarning(error, "Could not subscribe to Sonos household changes.");
        }
    }

    internal async Task<bool> ChangedAsync(string target, string id, CancellationToken ct)
    {
        if (id.Length > 128)
        {
            return true;
        }

        var household = target switch
        {
            "householdId" => id,
            "groupId" => await HouseholdForGroupAsync(id, ct),
            _ => null,
        };
        return household is null || await workflows.TrySignalHouseholdAsync(household, ct);
    }

    private async Task<string?> HouseholdForGroupAsync(string groupId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var households = await database
            .Set<SonosQueue>()
            .Where(row =>
                row.GroupId == groupId
                && row.ExpiresAt > now
                && (row.Status == "loaded" || row.Status == "dispatching")
            )
            .Select(row => row.HouseholdId)
            .Distinct()
            .Take(2)
            .ToArrayAsync(ct);
        return households.Length == 1 ? households[0] : null;
    }
}

[Workflow]
internal sealed class SonosHouseholdWorkflow
{
    private bool _changed;

    [WorkflowSignal]
    public Task GroupsChanged()
    {
        _changed = true;
        return Task.CompletedTask;
    }

    [WorkflowRun]
    public async Task RunAsync(string household)
    {
        do
        {
            _changed = false;
            try
            {
                await Workflow.ExecuteActivityAsync(
                    (SonosHouseholdActivities activities) =>
                        activities.RefreshNamesAsync(household),
                    new ActivityOptions
                    {
                        StartToCloseTimeout = TimeSpan.FromMinutes(1),
                        RetryPolicy = new RetryPolicy
                        {
                            InitialInterval = TimeSpan.FromSeconds(2),
                            MaximumAttempts = 3,
                        },
                    }
                );
            }
            catch (ActivityFailureException error) when (_changed)
            {
                // The exhausted refresh does not consume an event accepted while it ran.
                Workflow.Logger.LogWarning(
                    error,
                    "Another room-name refresh is pending for household {HouseholdId}.",
                    household
                );
            }
        } while (_changed);
    }
}

internal sealed class SonosHouseholdActivities(
    IServiceScopeFactory scopes,
    SonosIntegration integration,
    TimeProvider clock
)
{
    [Activity]
    public async Task RefreshNamesAsync(string household)
    {
        if (integration.Configuration is not { } config)
        {
            return;
        }
        var ct = ActivityExecutionContext.Current.CancellationToken;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        var link = scope.ServiceProvider.GetRequiredService<SonosAccountLink>();
        var handoff = scope.ServiceProvider.GetRequiredService<SonosHandoff>();
        var now = clock.GetUtcNow();
        var sessions = await db.Set<SonosQueue>()
            .Where(row =>
                row.HouseholdId == household
                && row.ExpiresAt > now
                && (row.Status == "loaded" || row.Status == "dispatching")
            )
            .Select(row => new { row.UserId, row.SessionId })
            .Distinct()
            .ToArrayAsync(ct);

        Exception? failed = null;
        foreach (var account in sessions.GroupBy(session => session.UserId))
        {
            try
            {
                foreach (var session in account)
                {
                    if (await link.SessionActiveAsync(session.UserId, session.SessionId, ct))
                    {
                        await RefreshAccountAsync(
                            link,
                            handoff,
                            config,
                            session.UserId,
                            household,
                            ct
                        );
                        break;
                    }
                }
            }
            catch (Exception error)
                when (!ct.IsCancellationRequested
                    && error
                        is (
                            SonosRequestException
                            or HttpRequestException
                            or OperationCanceledException
                            or System.Text.Json.JsonException
                        )
                )
            {
                // Try the remaining accounts before Temporal retries the household refresh.
                failed ??= error;
            }
        }

        if (failed is not null)
        {
            throw new ApplicationFailureException(
                "A linked account could not refresh its Sonos room names.",
                failed
            );
        }
    }

    private static async Task RefreshAccountAsync(
        SonosAccountLink link,
        SonosHandoff handoff,
        SonosConfiguration config,
        UserId user,
        string household,
        CancellationToken ct
    )
    {
        try
        {
            if (await link.AccessAsync(config, user, ct) is { } access)
            {
                await handoff.ReadAndRefreshGroupNamesAsync(user, access.Token, ct, household);
            }
        }
        catch (SonosRequestException error)
            when (error.Status
                    is HttpStatusCode.NotFound
                        or HttpStatusCode.Forbidden
                        or HttpStatusCode.Unauthorized
            )
        {
            // A linked user can lose access to a household while another still has it.
        }
    }
}
