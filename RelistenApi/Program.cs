using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using System.Collections;
using System.Globalization;

namespace Relisten
{
	public class Program
	{
		public static void Main(string[] args)
		{
			Console.WriteLine("Launched from {0}", Directory.GetCurrentDirectory());
			Console.WriteLine("Environment Variables:");

			var port = 3823;

			foreach(DictionaryEntry envVar in Environment.GetEnvironmentVariables())
			{
				if(envVar.Key.ToString().ToUpperInvariant() == "PORT")
				{
					int.TryParse(envVar.Value.ToString(), NumberStyles.Integer, null, out port);
				}
				Console.WriteLine(envVar.Key + "=" + envVar.Value);
			}

			var host = new WebHostBuilder()
				.UseKestrel()
				.UseUrls($"http://*:{port}/")
				.UseContentRoot(Directory.GetCurrentDirectory())
				.UseIISIntegration()
				.UseStartup<Startup>()
				.Build();

			host.Run();
		}
	}
}
