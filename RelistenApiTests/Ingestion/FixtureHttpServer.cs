using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RelistenApiTests.Ingestion;

// A loopback server exercises HTTP responses, redirects, and retry headers without real upstreams.
internal sealed class FixtureHttpServer : IDisposable
{
    private readonly HttpListener listener = new();
    public Uri Address { get; }
    public Func<Uri, string> Respond { get; set; } = _ => "{}";
    public Dictionary<string, string> Redirects { get; } = new();
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public TimeSpan? RetryAfter { get; set; }
    public int Requests { get; private set; }

    public FixtureHttpServer()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        Address = new Uri($"http://127.0.0.1:{port}/");
        listener.Prefixes.Add(Address.ToString());
        listener.Start();
        _ = Serve();
    }

    private async Task Serve()
    {
        try
        {
            while (listener.IsListening)
            {
                var context = await listener.GetContextAsync();
                Requests++;
                var redirect = Redirects.GetValueOrDefault(context.Request.Url!.AbsolutePath);
                var bytes = Encoding.UTF8.GetBytes(
                    redirect == null ? Respond(context.Request.Url) : ""
                );
                context.Response.StatusCode = redirect == null ? (int)Status : 302;
                if (redirect != null)
                    context.Response.RedirectLocation = redirect;
                if (RetryAfter.HasValue)
                    context.Response.Headers["Retry-After"] = (
                        (int)RetryAfter.Value.TotalSeconds
                    ).ToString();
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { }
    }

    public void Dispose() => listener.Close();
}
