using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Relisten.Services.Notifications;

public interface IDiscordWebhookNotifier
{
    Task SendAsync(string message, CancellationToken cancellationToken = default);
}

public sealed class DiscordWebhookNotifier : IDiscordWebhookNotifier
{
    internal static readonly HttpRequestOptionsKey<bool> SuppressTracingKey =
        new("Relisten.SuppressHttpClientTracing");

    private readonly HttpClient httpClient;
    private readonly ILogger<DiscordWebhookNotifier> logger;
    private readonly string? webhookUrl;

    public DiscordWebhookNotifier(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<DiscordWebhookNotifier> logger)
    {
        this.httpClient = httpClient;
        this.logger = logger;
        webhookUrl = configuration["DISCORD_WEBHOOK_URL"];
    }

    public async Task SendAsync(string message, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var webhookUri) ||
            webhookUri.Scheme != Uri.UriSchemeHttps ||
            (!webhookUri.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase) &&
             !webhookUri.Host.Equals("discordapp.com", StringComparison.OrdinalIgnoreCase)) ||
            !webhookUri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal))
        {
            logger.LogWarning("Discord webhook notifications are not configured with a valid Discord HTTPS URL");
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, webhookUri)
        {
            Content = JsonContent.Create(new
            {
                content = message,
                allowed_mentions = new { parse = Array.Empty<string>() }
            })
        };
        request.Options.Set(SuppressTracingKey, true);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Discord webhook notification failed with HTTP status {StatusCode}",
                    (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Discord webhook notification failed");
        }
    }
}
