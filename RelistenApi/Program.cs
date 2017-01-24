using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;

namespace Relisten
{
	public class Program
	{
		public static void Main(string[] args)
		{
			Console.WriteLine("Launched from {0}", Directory.GetCurrentDirectory());
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
