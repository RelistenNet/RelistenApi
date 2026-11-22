using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

namespace RelistenUserService.Authentication.Authorization;

/// <summary>Validates supplied native credentials on public account API routes.</summary>
internal sealed class OptionalNativeBearerMiddleware(
    RequestDelegate next,
    ILogger<OptionalNativeBearerMiddleware> logger
)
{
    public async Task InvokeAsync(
        HttpContext context,
        NativeSessionAuthenticator sessions,
        CurrentAccountContext account
    )
    {
        if (
            !context.Request.Path.StartsWithSegments("/v1")
            || context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is null
            || string.IsNullOrWhiteSpace(context.Request.Headers.Authorization)
        )
        {
            await next(context);
            return;
        }

        // Protected routes validate their session through their account policies.
        // A public route must also reject an invalid bearer when one was supplied.
        var credential = await context.AuthenticateAsync(
            AuthenticationConstants.AccountCredentialScheme
        );
        if (!credential.Succeeded)
        {
            await context.ChallengeAsync();
            return;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted
        );
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        AuthenticatedNativeSession? session;
        try
        {
            session = await sessions.AuthenticateAsync(credential.Principal!, deadline.Token);
        }
        catch (Exception error)
            when (!context.RequestAborted.IsCancellationRequested
                && error is (NpgsqlException or TimeoutException or OperationCanceledException)
            )
        {
            // An unavailable session store cannot tell the client to refresh its token.
            logger.LogWarning("Native session lookup failed ({ErrorType})", error.GetType().Name);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (session is null)
        {
            NativeSessionAuthorizationResultHandler.RejectInvalidSession(context);
            return;
        }

        context.User = credential.Principal!;
        account.SetNative(session.User, session.SessionId);
        await next(context);
    }
}
