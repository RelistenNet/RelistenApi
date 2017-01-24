﻿using System;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Relisten.Data;
using Relisten.Import;
using Relisten.Services.Auth;
using Swashbuckle.Swagger.Model;

namespace Relisten
{
	public class Startup
	{
		public Startup(IHostingEnvironment env)
		{
			Console.WriteLine("Loading config from {0}", env.ContentRootPath);
			var builder = new ConfigurationBuilder()
				.SetBasePath(env.ContentRootPath)
				.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
				.AddJsonFile($"appsettings.{env.EnvironmentName}.json", optional: true)
				.AddEnvironmentVariables();
			Configuration = builder.Build();
		}

		public IConfigurationRoot Configuration { get; }

		// This method gets called by the runtime. Use this method to add services to the container.
		public void ConfigureServices(IServiceCollection services)
		{
			// Add framework services.
			services.
				AddMvc().
				AddJsonOptions(jsonOptions =>
				{
					jsonOptions.SerializerSettings.DateFormatString = "yyyy'-'MM'-'dd'T'HH':'mm':'ssK";
					jsonOptions.SerializerSettings.DateTimeZoneHandling = DateTimeZoneHandling.Utc;
				});

			services.AddLogging();

			services.AddSwaggerGen(c =>
			{
				c.SingleApiVersion(new Info
				{
					Version = "v2",
					Title = "Relisten API",
					Description = "Music",
					TermsOfService = "TODO"
				});
			});

			var db = Configuration["POSTGRES_URL_INT"];
			Console.WriteLine("Attmepting to connect to {0}", db);
			DbService.SetConnectionURL(db);

			// use the static property because it is formatted correctly for NpgSQL
			services.AddHangfire(hangfire => hangfire.UsePostgreSqlStorage(DbService.ConnStr));

			services.AddSingleton<RedisService>(new RedisService(Configuration["REDIS_ADDRESS_INT"]));
			services.AddScoped<DbService, DbService>();

			services.AddScoped<SetlistShowService, SetlistShowService>();
			services.AddScoped<VenueService, VenueService>();
			services.AddScoped<TourService, TourService>();
			services.AddScoped<SetlistSongService, SetlistSongService>();
			services.AddScoped<SetlistFmImporter, SetlistFmImporter>();
			services.AddScoped<PhishinImporter, PhishinImporter>();
			services.AddScoped<ShowService, ShowService>();
			services.AddScoped<ArchiveOrgImporter, ArchiveOrgImporter>();
			services.AddScoped<SourceService, SourceService>();
			services.AddScoped<SourceReviewService, SourceReviewService>();
			services.AddScoped<SourceSetService, SourceSetService>();
			services.AddScoped<SourceTrackService, SourceTrackService>();
			services.AddScoped<PhishNetImporter, PhishNetImporter>();
			services.AddScoped<YearService, YearService>();
			services.AddScoped<EraService, EraService>();
			services.AddScoped<ImporterService, ImporterService>();
			services.AddScoped<JerryGarciaComImporter, JerryGarciaComImporter>();
			services.AddScoped<PanicStreamComImporter, PanicStreamComImporter>();
			services.AddScoped<ArtistService, ArtistService>();
			services.AddScoped<ScheduledService, ScheduledService>();
		}

		// This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
		public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory)
		{
			if (env.IsDevelopment())
			{
				app.UseDeveloperExceptionPage();
			}

			loggerFactory.AddConsole(Configuration.GetSection("Logging"));
			loggerFactory.AddDebug();

			app.UseMvc();

			app.UseHangfireServer();
			app.UseHangfireDashboard("/admin/hangfire", new DashboardOptions
			{
				Authorization = new[] { new HangfireBasicAuthFilter(Configuration["ADMIN:USERNAME"], Configuration["ADMIN:PASSWORD"]) }
			});

			app.UseSwagger((httpRequest, swaggerDoc) =>
			{
				swaggerDoc.Host = httpRequest.Host.Value;
			});

			app.UseSwaggerUi();
		}
	}
}