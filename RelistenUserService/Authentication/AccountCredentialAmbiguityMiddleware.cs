namespace RelistenUserService.Authentication;

public sealed class AccountCredentialAmbiguityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (AccountCredentialSelector.IsAmbiguous(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context);
    }
}
