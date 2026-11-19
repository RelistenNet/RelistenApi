using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Relisten.Accounts.Contracts.Errors;
using Relisten.Accounts.Contracts.History;
using RelistenUserService.Authentication;
using RelistenUserService.Authentication.Authorization;
using RelistenUserService.History;

namespace RelistenUserService.Controllers;

[ApiController]
[Authorize(Policy = AuthenticationConstants.LibraryAccessPolicy)]
public sealed class PlayHistoryController(CurrentAccountContext account, PlayHistoryUploads uploads)
    : ControllerBase
{
    [HttpPost("/v1/history/plays:batch")]
    [Consumes("application/json")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<ActionResult<PlayBatchUploaded>> Upload(CancellationToken cancellationToken)
    {
        using var document = await ReadBodyAsync(cancellationToken);
        var envelope = document is null ? null : ReadEnvelope(document.RootElement);
        if (envelope is null)
        {
            return InvalidEnvelope();
        }
        if (envelope.Value.Version != PlayHistoryUploads.ContractVersion)
        {
            return Problem(
                statusCode: 422,
                detail: "contract_version must be 1.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = AccountErrorCodes.InvalidContractVersion,
                }
            );
        }

        var result = await uploads.UploadAsync(
            account.User.Id,
            envelope.Value.Plays,
            cancellationToken
        );
        return result is null ? Unauthorized() : Ok(result);
    }

    private async Task<JsonDocument?> ReadBodyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await JsonDocument.ParseAsync(
                Request.Body,
                cancellationToken: cancellationToken
            );
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (int Version, JsonElement[] Plays)? ReadEnvelope(JsonElement root)
    {
        // Validate envelope fields here. Duplicate keys inside a play belong to its own result.
        try
        {
            if (
                root.ValueKind != JsonValueKind.Object
                || root.GetPropertyCount() != 2
                || !root.TryGetProperty("contract_version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var contractVersion)
                || !root.TryGetProperty("plays", out var plays)
                || plays.ValueKind != JsonValueKind.Array
                || plays.GetArrayLength() > PlayHistoryUploads.MaximumPlays
            )
            {
                return null;
            }
            return (contractVersion, plays.EnumerateArray().ToArray());
        }
        catch (InvalidOperationException)
        {
            // Property lookup can encounter a name with an invalid Unicode escape.
            return null;
        }
    }

    private ObjectResult InvalidEnvelope() =>
        Problem(
            statusCode: 422,
            detail: "Submit a contract version and an array of up to 500 plays.",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = AccountErrorCodes.InvalidRequest,
            }
        );
}
