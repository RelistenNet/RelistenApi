using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Relisten.Normalization.Batch;

public sealed partial class NormalizationReviewHost
{
    private sealed class ReviewTailnetAuthentication(ReviewHostConfiguration configuration)
    {
        private static readonly object UserKey = new();
        private readonly byte[] csrfSecret = RandomNumberGenerator.GetBytes(32);
        private readonly Uri[] origins = new[]
        {
            new Uri(configuration.BindUrl),
            new Uri(configuration.PublicUrl ?? configuration.BindUrl),
        };

        internal static ReviewUser User(HttpContext context) => (ReviewUser)context.Items[UserKey]!;

        internal string CsrfToken(string userId) =>
            Convert.ToHexStringLower(
                HMACSHA256.HashData(csrfSecret, Encoding.UTF8.GetBytes(userId))
            );

        internal async Task AuthorizeAsync(HttpContext context)
        {
            var origin = origins.FirstOrDefault(item =>
                string.Equals(
                    context.Request.Host.Value,
                    item.Authority,
                    StringComparison.OrdinalIgnoreCase
                )
            );
            if (origin == null)
            {
                throw new ReviewHttpException(403, "Unrecognized review host.");
            }
            var address = context.Connection.RemoteIpAddress;
            if (address == null)
            {
                throw new ReviewHttpException(403, "A verified Tailnet caller is required.");
            }
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }
            var user = await WhoIsAsync(address, context.RequestAborted);
            if (!configuration.AllowedTailnetUserIds.Contains(user.Id, StringComparer.Ordinal))
            {
                throw new ReviewHttpException(
                    403,
                    "This Tailnet user may not review normalization."
                );
            }
            context.Items[UserKey] = user;

            if (HttpMethods.IsPost(context.Request.Method))
            {
                var suppliedOrigin = context.Request.Headers.Origin.ToString();
                if (
                    !string.Equals(
                        suppliedOrigin,
                        origin.GetLeftPart(UriPartial.Authority),
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    throw new ReviewHttpException(
                        403,
                        "Review writes require the same browser origin."
                    );
                }
                var supplied = Encoding.UTF8.GetBytes(
                    context.Request.Headers["X-CSRF-Token"].ToString()
                );
                var expected = Encoding.UTF8.GetBytes(CsrfToken(user.Id));
                if (
                    supplied.Length != expected.Length
                    || !CryptographicOperations.FixedTimeEquals(supplied, expected)
                )
                {
                    throw new ReviewHttpException(403, "Reload the review page before saving.");
                }
                if (!context.Request.HasJsonContentType())
                {
                    throw new ReviewHttpException(400, "Review writes require JSON.");
                }
            }
        }

        private static async Task<ReviewUser> WhoIsAsync(IPAddress address, CancellationToken ct)
        {
            var start = new ProcessStartInfo("tailscale")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("whois");
            start.ArgumentList.Add("--json");
            start.ArgumentList.Add(address.ToString());
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                using var process =
                    Process.Start(start) ?? throw new IOException("Tailscale could not start.");
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                var text = await output;
                await error;
                if (process.ExitCode != 0 || text.Length > 256 * 1024)
                {
                    throw new ReviewHttpException(403, "Tailscale could not verify this caller.");
                }
                using var document = JsonDocument.Parse(text);
                var profile = document.RootElement.GetProperty("UserProfile");
                var id = profile.GetProperty("ID");
                // Tailscale IDs can exceed JavaScript's exact integer range. Never round them.
                var userId =
                    id.ValueKind == JsonValueKind.String ? id.GetString()! : id.GetRawText();
                var login = profile.GetProperty("LoginName").GetString();
                if (
                    userId.Length == 0
                    || !userId.All(char.IsAsciiDigit)
                    || string.IsNullOrWhiteSpace(login)
                )
                {
                    throw new ReviewHttpException(403, "Tailscale returned no user identity.");
                }
                return new(userId, login);
            }
            catch (Exception error)
                when (error
                        is IOException
                            or System.ComponentModel.Win32Exception
                            or JsonException
                            or KeyNotFoundException
                            or InvalidOperationException
                            or OperationCanceledException
                )
            {
                throw new ReviewHttpException(403, "Tailscale could not verify this caller.");
            }
        }
    }

    private sealed record ReviewUser(string Id, string LoginName);

    private sealed class ReviewHttpException(int statusCode, string message) : Exception(message)
    {
        internal int StatusCode { get; } = statusCode;
    }

    private sealed record ReviewHostConfiguration(
        string BindUrl,
        string[] AllowedTailnetUserIds,
        string EvidenceRoot,
        string VenueReader,
        string MusicReader,
        string PeopleReader,
        string? QueuePath = null,
        string? AssetsDirectory = null,
        string? PublicUrl = null,
        ReviewMusicConfiguration? MusicProjection = null
    )
    {
        internal static async Task<ReviewHostConfiguration> ReadAsync(
            string path,
            CancellationToken ct
        )
        {
            var fullPath = Path.GetFullPath(path);
            await using var stream = File.OpenRead(fullPath);
            var value =
                await JsonSerializer.DeserializeAsync<ReviewHostConfiguration>(stream, Json, ct)
                ?? throw new ArgumentException("Review host configuration is required.");
            if (
                !Uri.TryCreate(value.BindUrl, UriKind.Absolute, out var bind)
                || bind.Scheme != "http"
                || !IPAddress.TryParse(bind.Host, out var address)
                || !TailnetAddress(address)
                || bind.AbsolutePath != "/"
                || bind.UserInfo.Length != 0
                || bind.Query.Length != 0
                || bind.Fragment.Length != 0
            )
            {
                throw new ArgumentException(
                    "Bind the review host to one literal private Tailscale HTTP address."
                );
            }
            if (value.PublicUrl != null)
            {
                if (
                    !Uri.TryCreate(value.PublicUrl, UriKind.Absolute, out var publicUrl)
                    || publicUrl.Scheme != bind.Scheme
                    || publicUrl.Port != bind.Port
                    || publicUrl.AbsolutePath != "/"
                    || publicUrl.UserInfo.Length != 0
                    || publicUrl.Query.Length != 0
                    || publicUrl.Fragment.Length != 0
                    || !(
                        publicUrl.Host == bind.Host
                        || publicUrl.Host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    throw new ArgumentException(
                        "PublicUrl must name the configured Tailnet listener on the same port."
                    );
                }
                if (!(await Dns.GetHostAddressesAsync(publicUrl.Host, ct)).Contains(address))
                {
                    throw new ArgumentException(
                        "PublicUrl must resolve to the configured Tailnet bind address."
                    );
                }
            }
            if (
                value.AllowedTailnetUserIds is not { Length: > 0 }
                || value.AllowedTailnetUserIds.Any(id =>
                    string.IsNullOrEmpty(id) || !id.All(char.IsAsciiDigit)
                )
            )
            {
                throw new ArgumentException(
                    "Configure exact decimal Tailnet user IDs as JSON strings."
                );
            }
            foreach (
                var reader in new[] { value.VenueReader, value.MusicReader, value.PeopleReader }
            )
            {
                if (
                    !Uri.TryCreate(reader, UriKind.Absolute, out var uri)
                    || uri.Scheme != "http"
                    || !IPAddress.TryParse(uri.Host, out var ip)
                    || !(IPAddress.IsLoopback(ip) || TailnetAddress(ip))
                    || uri.AbsolutePath != "/"
                    || uri.UserInfo.Length != 0
                    || uri.Query.Length != 0
                    || uri.Fragment.Length != 0
                )
                {
                    throw new ArgumentException(
                        "Each reader must be a fixed literal localhost or Tailnet HTTP origin."
                    );
                }
            }
            var root = Path.GetFullPath(
                Required(value.EvidenceRoot, "evidence directory"),
                Path.GetDirectoryName(fullPath)!
            );
            Directory.CreateDirectory(root);
            var assets =
                value.AssetsDirectory == null
                    ? Path.GetFullPath("tools/RelistenNormalization")
                    : Path.GetFullPath(value.AssetsDirectory, Path.GetDirectoryName(fullPath)!);
            if (!File.Exists(Path.Combine(assets, "normalization-review.html")))
            {
                throw new ArgumentException("The review UI assets directory is unavailable.");
            }
            return value with
            {
                EvidenceRoot = root,
                AssetsDirectory = assets,
                MusicProjection = value.MusicProjection?.Resolve(Path.GetDirectoryName(fullPath)!),
                QueuePath =
                    value.QueuePath == null
                        ? null
                        : Path.GetFullPath(value.QueuePath, Path.GetDirectoryName(fullPath)!),
            };
        }

        internal string LocalDatabaseUrl()
        {
            var value =
                Environment.GetEnvironmentVariable("DATABASE_URL")
                ?? Environment.GetEnvironmentVariable("RELISTEN_NORMALIZATION_DATABASE_URL");
            if (
                !Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.Scheme != "postgresql"
                || uri.Host is not ("localhost" or "127.0.0.1" or "[::1]" or "::1")
                || !uri.AbsolutePath.StartsWith(
                    "/relisten_normalization_",
                    StringComparison.Ordinal
                )
                || uri.UserInfo.Split(':').Length != 2
                || uri.Query.Length != 0
                || uri.Fragment.Length != 0
            )
            {
                throw new ArgumentException(
                    "Set DATABASE_URL to an explicit dedicated localhost relisten_normalization_* PostgreSQL database."
                );
            }
            return value!;
        }

        private static bool TailnetAddress(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            return bytes.Length == 4 && bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
        }
    }
}
