using System.Text.Json;
using System.Text.Json.Serialization;
using RelistenUserService.Configuration;

namespace RelistenUserService.Authentication.OpenIdConnect;

/// <summary>
/// Polls an optional projected ConfigMap file, reopening it to follow Kubernetes
/// symlink replacement. Bad/missing input retains the last working origin set.
/// </summary>
public sealed class PreviewOriginFileLoader(
    PreviewOriginReload reload,
    AccountsRuntimeConfiguration runtime,
    ILogger<PreviewOriginFileLoader> logger
) : BackgroundService
{
    public const int MaximumSnapshotBytes = 16 * 1024;
    private volatile PreviewOriginSourceState _state = string.IsNullOrWhiteSpace(
        runtime.Options.PreviewOriginsFile
    )
        ? PreviewOriginSourceState.Disabled
        : PreviewOriginSourceState.Waiting;

    /// <summary>Contains no origins, paths, credentials, or auth state.</summary>
    public PreviewOriginFileStatus Status => new(runtime.BrowserOrigins.Current.Revision, _state);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(runtime.Options.PreviewOriginsFile))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await LoadOnceAsync(runtime.Options.PreviewOriginsFile, stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    /// <summary>Attempts one bounded read/reload; failures leave active authentication intact.</summary>
    public async Task LoadOnceAsync(string path, CancellationToken cancellationToken)
    {
        PreviewOriginSourceState state;
        try
        {
            var snapshot = await ReadAsync(path, cancellationToken);
            await reload.ApplyAsync(snapshot.Revision, snapshot.Origins, cancellationToken);
            state = PreviewOriginSourceState.Ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            state = PreviewOriginSourceState.Missing;
        }
        catch (Exception exception)
            when (exception is JsonException or InvalidDataException or ArgumentException)
        {
            state = PreviewOriginSourceState.Invalid;
        }
        catch (Exception)
        {
            // Includes database/options failures and stale revisions. The coordinator
            // preserves the previous policy; retry when projection/dependencies recover.
            state = PreviewOriginSourceState.Rejected;
        }

        if (state != _state)
        {
            // Never log parser/DB exception text: it can contain source values or credentials.
            logger.LogInformation(
                "Preview origin source is {State}; active revision {Revision}.",
                state,
                runtime.BrowserOrigins.Current.Revision
            );
            _state = state;
        }
    }

    /// <summary>Reads schema version 1 with strict property names and a fixed size limit.</summary>
    public static async Task<PreviewOriginFileSnapshot> ReadAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        await using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true
        );
        var bytes = new byte[MaximumSnapshotBytes + 1];
        var length = await file.ReadAtLeastAsync(
            bytes,
            bytes.Length,
            throwOnEndOfStream: false,
            cancellationToken
        );
        if (length > MaximumSnapshotBytes)
        {
            throw new InvalidDataException("The preview snapshot exceeds its size limit.");
        }
        var snapshot = JsonSerializer.Deserialize<PreviewOriginFileSnapshot>(
            bytes.AsSpan(0, length),
            SnapshotJson
        );
        if (snapshot is null || snapshot.Version != 1 || snapshot.Origins is null)
        {
            throw new InvalidDataException(
                "The preview snapshot must use schema version 1 with an origin array."
            );
        }
        // Validate before returning a parsed snapshot to any consumer.
        _ = WebOriginRegistry.Parse(snapshot.Revision, snapshot.Origins);
        return snapshot;
    }

    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        MaxDepth = 4,
    };
}

public sealed record PreviewOriginFileSnapshot
{
    public required int Version { get; init; }
    public required long Revision { get; init; }
    public required string[] Origins { get; init; }
}

public enum PreviewOriginSourceState
{
    Disabled,
    Waiting,
    Ready,
    Missing,
    Invalid,
    Rejected,
}

public sealed record PreviewOriginFileStatus(
    long AppliedRevision,
    PreviewOriginSourceState SourceState
);
