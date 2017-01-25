using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using System.Collections;

namespace Relisten
{
	public class Program
	{
		public static void Main(string[] args)
		{
			Console.WriteLine("Launched from {0}", Directory.GetCurrentDirectory());
			Console.WriteLine("Environment Variables:");

			foreach(DictionaryEntry envVar in Environment.GetEnvironmentVariables())
			{
				Console.WriteLine(envVar.Key + "=" + envVar.Value);
			}

			var host = new WebHostBuilder()
				.UseKestrel()
				.UseUrls("http://*:3823/")
				.UseContentRoot(Directory.GetCurrentDirectory())
				.UseIISIntegration()
				.UseStartup<Startup>()
				.Build();

			host.Run();
		}
	}
}
