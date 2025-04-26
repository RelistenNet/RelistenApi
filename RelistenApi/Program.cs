using System;
using System.Collections;
using System.Globalization;
using System.IO;
using Microsoft.AspNetCore;
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
                if (envVar.Key.ToString().ToUpperInvariant() == "PORT")
                {
                    int.TryParse(envVar.Value.ToString(), NumberStyles.Integer, null, out port);
                }
            }

            BuildWebHost(args, port).Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    var sentryDsn = Environment.GetEnvironmentVariable("SENTRY_DSN");

                    if (!string.IsNullOrEmpty(sentryDsn))
                    {
                        // Add the following line:
                        webBuilder.UseSentry(o =>
                        {
                            o.Dsn = sentryDsn;
                            o.SendDefaultPii = true; // we don't have PII?
                            o.Debug = true;
                        });
                    }
                });

        public static IWebHost BuildWebHost(string[] args, int port) => WebHost.CreateDefaultBuilder(args)
            .UseContentRoot(Directory.GetCurrentDirectory())
            .UseUrls($"http://*:{port}/")
            .UseStartup<Startup>()
            .Build();
    }
}
