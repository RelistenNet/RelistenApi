using System;
using System.Collections;
using System.Globalization;
using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace Relisten
{
    public class Program
    {
        public static void Main(string[] args)
        {
            Console.WriteLine("Launched from {0}", Directory.GetCurrentDirectory());

            var port = 3823;

            foreach (DictionaryEntry envVar in Environment.GetEnvironmentVariables())
            {
                var key = envVar.Key as string;
                if (string.Equals(key, "PORT", StringComparison.OrdinalIgnoreCase))
                {
                    var value = envVar.Value?.ToString();
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out port);
                }
            }

            CreateHostBuilder(args, port).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args, int port)
        {
            return Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    var sentryDsn = Environment.GetEnvironmentVariable("SENTRY_DSN");
                    if (!string.IsNullOrEmpty(sentryDsn))
                    {
                        Console.WriteLine($"Configuring Sentry: {sentryDsn}");
                        webBuilder.UseSentry(o =>
                        {
                            o.Dsn = sentryDsn;
                            o.SendDefaultPii = true; // we don't have PII?
                        });
                    }

                    webBuilder
                        .UseContentRoot(Directory.GetCurrentDirectory())
                        .UseUrls($"http://*:{port}/")
                        .UseStartup<Startup>();
                });
        }
    }
}
