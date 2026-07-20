using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using RelistenUserService.Persistence;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RelistenUserService.Authentication;

public sealed class RefreshTokenReplayMiddleware(RequestDelegate next)
{
    private const string TokenEndpointPath = "/connect/token";
    private static readonly TimeSpan RevocationDeadline = TimeSpan.FromSeconds(10);

    public async Task InvokeAsync(
        HttpContext context,
        IOpenIddictTokenManager tokenManager,
        IOpenIddictAuthorizationManager authorizationManager,
        AccountsDbContext dbContext,
        RefreshTokenLockProvider refreshTokenLocks,
        TimeProvider timeProvider,
        IHostApplicationLifetime applicationLifetime,
        ILogger<RefreshTokenReplayMiddleware> logger)
    {
        var refreshToken = await ReadRefreshTokenAsync(context);
        if (refreshToken is null)
        {
            await next(context);
            return;
        }

        // PostgreSQL serializes the same reference token across replicas. Without this lock,
        // two requests can both validate the active token before either one records redemption.
        using var lockDeadline = CreateSecurityDeadline(applicationLifetime);
        await using var refreshTokenLock = await refreshTokenLocks.AcquireAsync(
            refreshToken,
            lockDeadline.Token);
        var replayDetected = await RevokeIfRedeemedAsync(
            tokenManager,
            authorizationManager,
            dbContext,
            timeProvider,
            refreshToken,
            applicationLifetime);

        var requestAborted = context.RequestAborted;
        using var downstreamDeadline = CreateDownstreamDeadline(
            requestAborted,
            applicationLifetime);
        ExceptionDispatchInfo? downstreamFailure = null;
        try
        {
            // Pass the bounded token through ASP.NET instead of timing out the await. The
            // advisory-lock lease must stay alive until every downstream operation has really
            // stopped using the token and its OpenIddict/EF state.
            context.RequestAborted = downstreamDeadline.Token;
            await next(context);
        }
        catch (Exception exception)
        {
            downstreamFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            context.RequestAborted = requestAborted;
        }

        if (!replayDetected
            && (downstreamFailure is not null
                || context.Response.StatusCode >= StatusCodes.Status400BadRequest
                || downstreamDeadline.IsCancellationRequested))
        {
            try
            {
                // OpenIddict can mark a token redeemed while processing the request. Recheck
                // failed, timed-out, and disconnected requests without trusting the client to
                // stay present. Exceptions matter even when no error status reached the response.
                await RevokeIfRedeemedAsync(
                    tokenManager,
                    authorizationManager,
                    dbContext,
                    timeProvider,
                    refreshToken,
                    applicationLifetime);
            }
            catch (Exception exception) when (downstreamFailure is not null)
            {
                // The pipeline's exception remains the actionable cause for this request. The
                // recheck failure is still operationally urgent, but must not replace that cause.
                logger.LogError(
                    exception,
                    "Refresh-token safety recheck failed after the token pipeline threw.");
            }
        }

        downstreamFailure?.Throw();
    }

    private static async Task<string?> ReadRefreshTokenAsync(HttpContext context)
    {
        if (!IsTokenEndpoint(context.Request.Path)
            || !context.Request.HasFormContentType)
        {
            return null;
        }

        // ASP.NET caches the parsed form on HttpRequest, so this does not consume the body
        // before OpenIddict reads the same token request later in the pipeline.
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var refreshToken = form[Parameters.RefreshToken].ToString();
        return form[Parameters.GrantType] == GrantTypes.RefreshToken
            && !string.IsNullOrWhiteSpace(refreshToken)
                ? refreshToken
                : null;
    }

    internal static bool IsTokenEndpoint(PathString path) =>
        string.Equals(path.Value, TokenEndpointPath, StringComparison.OrdinalIgnoreCase)
        || string.Equals(
            path.Value,
            $"{TokenEndpointPath}/",
            StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> IsRedeemedAsync(
        IOpenIddictTokenManager tokenManager,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var token = await tokenManager.FindByReferenceIdAsync(refreshToken, cancellationToken);
        return token is not null
            && await tokenManager.HasStatusAsync(token, Statuses.Redeemed, cancellationToken);
    }

    private static async Task RevokeFamilyAsync(
        IOpenIddictTokenManager tokenManager,
        IOpenIddictAuthorizationManager authorizationManager,
        AccountsDbContext dbContext,
        TimeProvider timeProvider,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var token = await tokenManager.FindByReferenceIdAsync(refreshToken, cancellationToken);
        if (token is null)
        {
            return;
        }

        var authorizationId = await tokenManager.GetAuthorizationIdAsync(token, cancellationToken);
        if (!Guid.TryParse(authorizationId, out var authorizationUuid))
        {
            return;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var session = await dbContext.NativeSessions.SingleOrDefaultAsync(
            item => item.AuthorizationId == authorizationUuid,
            cancellationToken);
        if (session is not null)
        {
            var now = timeProvider.GetUtcNow();
            session.RevokedAt ??= now;
            session.UpdatedAt = now;
        }

        var authorization = await authorizationManager.FindByIdAsync(
            authorizationId,
            cancellationToken);
        if (authorization is not null)
        {
            await authorizationManager.TryRevokeAsync(authorization, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<bool> RevokeIfRedeemedAsync(
        IOpenIddictTokenManager tokenManager,
        IOpenIddictAuthorizationManager authorizationManager,
        AccountsDbContext dbContext,
        TimeProvider timeProvider,
        string refreshToken,
        IHostApplicationLifetime applicationLifetime)
    {
        // Parsing the request remains client-cancellable. Once a complete reference token is
        // available, replay detection and revocation become a bounded server-side operation.
        using var deadline = CreateSecurityDeadline(applicationLifetime);
        if (!await IsRedeemedAsync(tokenManager, refreshToken, deadline.Token))
        {
            return false;
        }

        // A redeemed reference token presented again is evidence that the token family
        // escaped its original client. Revoke the server-side session, not only this token.
        await RevokeFamilyAsync(
            tokenManager,
            authorizationManager,
            dbContext,
            timeProvider,
            refreshToken,
            deadline.Token);
        return true;
    }

    private static CancellationTokenSource CreateSecurityDeadline(
        IHostApplicationLifetime applicationLifetime)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            applicationLifetime.ApplicationStopping);
        deadline.CancelAfter(RevocationDeadline);
        return deadline;
    }

    private static CancellationTokenSource CreateDownstreamDeadline(
        CancellationToken requestAborted,
        IHostApplicationLifetime applicationLifetime)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            requestAborted,
            applicationLifetime.ApplicationStopping);
        deadline.CancelAfter(RevocationDeadline);
        return deadline;
    }
}
