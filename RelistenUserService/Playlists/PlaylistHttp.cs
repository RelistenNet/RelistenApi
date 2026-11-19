using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.EntityFrameworkCore;
using RelistenUserService.Persistence;

namespace RelistenUserService.Playlists;

/// <summary>Strict playlist input rules without changing unrelated account endpoints.</summary>
public sealed class PlaylistInputFormatter : SystemTextJsonInputFormatter
{
    public PlaylistInputFormatter(ILogger<SystemTextJsonInputFormatter> logger)
        : base(new JsonOptions(), logger)
    {
        SerializerOptions.PropertyNamingPolicy = PlaylistJson.Options.PropertyNamingPolicy;
        SerializerOptions.UnmappedMemberHandling = PlaylistJson.Options.UnmappedMemberHandling;
        SerializerOptions.AllowDuplicateProperties = false;
        SerializerOptions.RespectRequiredConstructorParameters = true;
        SerializerOptions.PropertyNameCaseInsensitive = false;
        SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict;
    }

    public override bool CanRead(InputFormatterContext context) =>
        (
            context.HttpContext.Request.Path.StartsWithSegments("/v1/playlists")
            || context.HttpContext.Request.Path.StartsWithSegments("/v1/public-playlists")
        ) && base.CanRead(context);
}

/// <summary>Maps domain and body-validation problems to the same generation-aware wire envelope.</summary>
public sealed class PlaylistHttpFilter(AccountsDbContext dbContext)
    : IAsyncExceptionFilter,
        IAsyncAlwaysRunResultFilter
{
    public Task OnExceptionAsync(ExceptionContext context)
    {
        if (context.Exception is PlaylistException exception)
        {
            var problem = new ProblemDetails
            {
                Status = exception.Status,
                Title = exception.Code,
                Detail = exception.Message,
            };
            problem.Extensions["code"] = exception.Code;
            if (exception.ConflictingIds is not null)
            {
                problem.Extensions[
                    exception.Code == "idempotency_conflict"
                        ? "conflicting_operation_ids"
                        : "conflicting_ids"
                ] = exception.ConflictingIds;
            }
            context.Result = new ObjectResult(problem) { StatusCode = exception.Status };
            context.ExceptionHandled = true;
        }
        return Task.CompletedTask;
    }

    public async Task OnResultExecutionAsync(
        ResultExecutingContext context,
        ResultExecutionDelegate next
    )
    {
        if (
            context.Result is ObjectResult { Value: ProblemDetails problem }
            && problem.Status is not (401 or 403 or 413)
        )
        {
            problem.Extensions["history_generation"] = await dbContext
                .HistoryGenerations.AsNoTracking()
                .Select(row => row.Value)
                .SingleAsync(context.HttpContext.RequestAborted);
        }
        await next();
    }
}
