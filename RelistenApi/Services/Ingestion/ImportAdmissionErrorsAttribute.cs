using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Relisten.Ingestion;

/// <summary>Maps expected admission failures without changing successful import response bodies.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ImportAdmissionErrorsAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        context.Result = context.Exception switch
        {
            ImportAlreadyRunning conflict => new ConflictObjectResult(
                new
                {
                    code = "import_already_running",
                    workflowId = conflict.WorkflowId,
                    message = conflict.Message,
                }
            ),
            InvalidImportRequest invalid => new BadRequestObjectResult(
                new { code = "invalid_import_request", message = invalid.Message }
            ),
            UnsupportedImportOperation unsupported => new BadRequestObjectResult(
                new { code = "unsupported_import_operation", message = unsupported.Message }
            ),
            ArtistNotFound missing => new NotFoundObjectResult(
                new { code = "artist_not_found", message = missing.Message }
            ),
            _ => null,
        };
        context.ExceptionHandled = context.Result != null;
    }
}
