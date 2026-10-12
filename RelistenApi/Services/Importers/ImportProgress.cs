using System;
using System.Globalization;
using Hangfire.Console;
using Hangfire.Server;
using Microsoft.Extensions.Logging;

namespace Relisten.Import;

/// <summary>Writes shared importer diagnostics to worker logs and, when present, the Hangfire console.</summary>
internal static class ImportProgress
{
    internal static void WriteLine(
        ILogger logger,
        PerformContext? context,
        string format,
        params object?[] arguments
    )
    {
        var message =
            arguments.Length == 0
                ? format
                : string.Format(CultureInfo.InvariantCulture, format, arguments);
        logger.LogInformation("{ImportMessage}", message);
        context?.WriteLine(message);
    }

    internal static void LogException(ILogger logger, PerformContext? context, Exception error)
    {
        logger.LogError(error, "Importer operation failed");
        context?.LogException(error);
    }
}
