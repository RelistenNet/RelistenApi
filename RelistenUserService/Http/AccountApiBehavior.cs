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
            var invalidCommandUuid = HasErrorFor(
                context.ModelState.Keys,
                "client_command_uuid",
                "ClientCommandUuid");
            var invalidContractVersion = HasErrorFor(
                context.ModelState.Keys,
                "contract_version",
                "ContractVersion");
            var (status, code) = (invalidCommandUuid, invalidContractVersion) switch
            {
                (true, _) => (
                    StatusCodes.Status422UnprocessableEntity,
                    AccountErrorCodes.InvalidCommandUuid),
                (_, true) => (
                    StatusCodes.Status422UnprocessableEntity,
                    AccountErrorCodes.InvalidContractVersion),
                _ => (StatusCodes.Status400BadRequest, AccountErrorCodes.InvalidRequest)
            };
            var problem = new ValidationProblemDetails(context.ModelState)
            {
                Status = status,
                Title = code
            };
            problem.Extensions["code"] = code;

            return new ObjectResult(problem)
            {
                StatusCode = status,
                ContentTypes = { "application/problem+json" }
            };
        };
    }

    private static bool HasErrorFor(
        IEnumerable<string> keys,
        string jsonName,
        string propertyName) => keys.Any(key =>
            key.Contains(jsonName, StringComparison.OrdinalIgnoreCase)
            || key.Contains(propertyName, StringComparison.OrdinalIgnoreCase));
}
