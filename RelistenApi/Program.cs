using System;
using System.Collections;
using System.Globalization;
using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

namespace Relisten
{
    public class Program
    {
        public static int Main(string[] args)
        {
            // Two-stage initialization: bootstrap logger before anything else
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                .Enrich.FromLogContext()
                .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter())
                .CreateBootstrapLogger();

            try
            {
                Log.Information("Starting RelistenApi from {WorkingDirectory}", Directory.GetCurrentDirectory());

                var port = 3823;

                foreach (DictionaryEntry envVar in Environment.GetEnvironmentVariables())
                {
                    var key = envVar.Key as string;
                    if (string.Equals(key, "PORT", StringComparison.OrdinalIgnoreCase))
                    {
                        var value = envVar.Value?.ToString();
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort))
                        {
                            port = parsedPort;
                            Log.Information("Using PORT environment variable: {Port}", port);
                        }
                    }
                }

                CreateHostBuilder(args, port).Build().Run();

                Log.Information("RelistenApi stopped cleanly");
                return 0;
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "RelistenApi terminated unexpectedly");
                return 1;
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        public static IHostBuilder CreateHostBuilder(string[] args, int port)
        {
            return Host.CreateDefaultBuilder(args)
                .UseSerilog((context, services, configuration) => configuration
                    .ReadFrom.Configuration(context.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext())
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    var sentryDsn = Environment.GetEnvironmentVariable("SENTRY_DSN");
                    if (!string.IsNullOrEmpty(sentryDsn))
                    {
                        Log.Information("Configuring Sentry integration with DSN: {SentryDsn}",
                            MaskSentryDsn(sentryDsn));
                        webBuilder.UseSentry(o =>
                        {
                            o.Dsn = sentryDsn;
                            o.SendDefaultPii = true; // we don't have PII?
                        });
                    }
                    else
                    {
                        Log.Warning("SENTRY_DSN not configured, Sentry integration disabled");
                    }

                    webBuilder
                        .UseContentRoot(Directory.GetCurrentDirectory())
                        .UseUrls($"http://*:{port}/")
                        .UseStartup<Startup>();
                });
        }

        private static string MaskSentryDsn(string dsn)
        {
            // Mask the sensitive parts of the DSN for logging
            var uri = new Uri(dsn);
            return $"{uri.Scheme}://***@{uri.Host}{uri.PathAndQuery}";
        }
    }
}
