using Microsoft.AspNetCore.Mvc;
using Relisten.Accounts.Contracts.Errors;

namespace RelistenUserService.Http;

public static class AccountApiBehavior
{
    public static void Configure(ApiBehaviorOptions options)
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            // Type-conversion failures never reach the controller. Preserve the same stable
            // wire codes that semantic validation returns for these two command fields.
            var invalidCommandId = HasErrorFor(context.ModelState.Keys, "command_id", "CommandId");
            var invalidContractVersion = HasErrorFor(
                context.ModelState.Keys,
                "contract_version",
                "ContractVersion"
            );
            if (context.HttpContext.Request.Path.StartsWithSegments("/v1/playlists"))
            {
                var playlistCode = invalidContractVersion
                    ? AccountErrorCodes.InvalidContractVersion
                    : "invalid_playlist_operation";
                var playlistProblem = new ValidationProblemDetails(context.ModelState)
                {
                    Status = StatusCodes.Status422UnprocessableEntity,
                    Title = playlistCode,
                };
                playlistProblem.Extensions["code"] = playlistCode;
                return new ObjectResult(playlistProblem)
                {
                    StatusCode = playlistProblem.Status,
                    ContentTypes = { "application/problem+json" },
                };
            }
            var (status, code) = (invalidCommandId, invalidContractVersion) switch
            {
                (true, _) => (
                    StatusCodes.Status422UnprocessableEntity,
                    AccountErrorCodes.InvalidCommandId
                ),
                (_, true) => (
                    StatusCodes.Status422UnprocessableEntity,
                    AccountErrorCodes.InvalidContractVersion
                ),
                _ => (StatusCodes.Status400BadRequest, AccountErrorCodes.InvalidRequest),
            };
            var problem = new ValidationProblemDetails(context.ModelState)
            {
                Status = status,
                Title = code,
            };
            problem.Extensions["code"] = code;

            return new ObjectResult(problem)
            {
                StatusCode = status,
                ContentTypes = { "application/problem+json" },
            };
        };
    }

    private static bool HasErrorFor(
        IEnumerable<string> keys,
        string jsonName,
        string propertyName
    ) =>
        keys.Any(key =>
            key.Contains(jsonName, StringComparison.OrdinalIgnoreCase)
            || key.Contains(propertyName, StringComparison.OrdinalIgnoreCase)
        );
}
