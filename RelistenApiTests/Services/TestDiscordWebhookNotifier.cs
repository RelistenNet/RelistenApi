using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Relisten.Services.Notifications;

namespace RelistenApiTests.Services;

[TestFixture]
public class TestDiscordWebhookNotifier
{
    [Test]
    public async Task SendAsyncShouldPostMessageWithoutMentionsOrTracing()
    {
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        using var httpClient = new HttpClient(handler);
        var notifier = CreateNotifier(httpClient, "https://discord.com/api/webhooks/id/token");

        await notifier.SendAsync("blocked @everyone");

        handler.RequestCount.Should().Be(1);
        handler.Method.Should().Be(HttpMethod.Post);
        handler.RequestUri.Should().Be(new Uri("https://discord.com/api/webhooks/id/token"));
        handler.SuppressTracing.Should().BeTrue();

        using var payload = JsonDocument.Parse(handler.Body!);
        payload.RootElement.GetProperty("content").GetString().Should().Be("blocked @everyone");
        payload.RootElement.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength().Should().Be(0);
    }

    [Test]
    public async Task SendAsyncShouldSkipRequestsWithoutAConfiguredWebhook()
    {
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        using var httpClient = new HttpClient(handler);
        var notifier = CreateNotifier(httpClient, null);

        await notifier.SendAsync("blocked");

        handler.RequestCount.Should().Be(0);
    }

    [TestCase("http://discord.com/api/webhooks/id/token")]
    [TestCase("https://example.com/api/webhooks/id/token")]
    public async Task SendAsyncShouldSkipInvalidWebhookUrls(string webhookUrl)
    {
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        using var httpClient = new HttpClient(handler);
        var notifier = CreateNotifier(httpClient, webhookUrl);

        await notifier.SendAsync("blocked");

        handler.RequestCount.Should().Be(0);
    }

    [Test]
    public async Task SendAsyncShouldNotThrowForDeliveryFailures()
    {
        var nonSuccessHandler = new RecordingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));
        using var nonSuccessClient = new HttpClient(nonSuccessHandler);
        var nonSuccessNotifier = CreateNotifier(nonSuccessClient, "https://discord.com/api/webhooks/id/token");

        var nonSuccessAct = () => nonSuccessNotifier.SendAsync("blocked");

        await nonSuccessAct.Should().NotThrowAsync();

        var exceptionHandler = new RecordingHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("network unavailable")));
        using var exceptionClient = new HttpClient(exceptionHandler);
        var exceptionNotifier = CreateNotifier(exceptionClient, "https://discord.com/api/webhooks/id/token");

        var exceptionAct = () => exceptionNotifier.SendAsync("blocked");

        await exceptionAct.Should().NotThrowAsync();
    }

    [Test]
    public async Task SendAsyncShouldPropagateCallerCancellation()
    {
        var handler = new RecordingHandler((_, cancellationToken) =>
            Task.FromCanceled<HttpResponseMessage>(cancellationToken));
        using var httpClient = new HttpClient(handler);
        var notifier = CreateNotifier(httpClient, "https://discord.com/api/webhooks/id/token");
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        var act = () => notifier.SendAsync("blocked", cancellationSource.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static DiscordWebhookNotifier CreateNotifier(HttpClient httpClient, string? webhookUrl)
    {
        var values = new Dictionary<string, string?>();
        if (webhookUrl != null)
        {
            values["DISCORD_WEBHOOK_URL"] = webhookUrl;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        return new DiscordWebhookNotifier(
            httpClient,
            configuration,
            NullLogger<DiscordWebhookNotifier>.Instance);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder;

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            this.responder = responder;
        }

        public int RequestCount { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? Body { get; private set; }
        public bool SuppressTracing { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            SuppressTracing = request.Options.TryGetValue(
                DiscordWebhookNotifier.SuppressTracingKey,
                out var suppressTracing) && suppressTracing;
            return await responder(request, cancellationToken);
        }
    }
}
