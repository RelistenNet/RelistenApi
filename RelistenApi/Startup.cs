using System;
using Hangfire;
using Hangfire.Console;
using Hangfire.PostgreSql;
using Hangfire.RecurringJobExtensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Cors.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Relisten.Data;
using Relisten.Import;
using Relisten.Services.Auth;
using StackExchange.Redis;
using Swashbuckle.AspNetCore.Swagger;

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
			services.AddCors();

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
				c.SwaggerDoc("v2", new Info {
					Version = "v2",
					Title = "Relisten API",
					Contact = new Contact {
						Name = "Alec Gorge",
						Url = "https://twitter.com/alecgorge"
					},
					License = new License {
						Name = "MIT",
						Url = "https://opensource.org/licenses/MIT"
					}
				});
			});

			// use the static property because it is formatted correctly for NpgSQL
			services.AddHangfire(hangfire => {
				hangfire.UseRedisStorage(ConnectionMultiplexer.Connect(Configuration["REDIS_ADDRESS_INT"] + ",syncTimeout=10000"));
				hangfire.UseConsole();
				hangfire.UseRecurringJob(typeof(ScheduledService));
			});

			services.AddSingleton(new RedisService(Configuration["REDIS_ADDRESS_INT"]));
			services.AddSingleton(new DbService(Configuration["POSTGRESQL_URL_INT"]));
			services.AddSingleton(Configuration);

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
			services.AddScoped<PhantasyTourImporter, PhantasyTourImporter>();
			services.AddScoped<YearService, YearService>();
			services.AddScoped<EraService, EraService>();
			services.AddScoped<ImporterService, ImporterService>();
			services.AddScoped<JerryGarciaComImporter, JerryGarciaComImporter>();
			services.AddScoped<PanicStreamComImporter, PanicStreamComImporter>();
			services.AddScoped<ArtistService, ArtistService>();
			services.AddScoped<ScheduledService, ScheduledService>();
			services.AddScoped<SearchService, SearchService>();
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

			app.UseCors(builder => builder
								  .WithMethods("GET", "POST", "OPTIONS", "HEAD")
								  .WithOrigins("*")
								  .AllowAnyMethod());

			app.UseMvc();

			app.UseHangfireServer(new BackgroundJobServerOptions
			{
				Queues = new[] { "artist_import" },
				ServerName = "relistenapi:artist_import",
				WorkerCount = 3
			});

			app.UseHangfireServer(new BackgroundJobServerOptions
			{
				Queues = new[] { "default" },
				ServerName = "relistenapi:default"
			});

			app.UseHangfireDashboard("/admin/hangfire", new DashboardOptions
			{
				Authorization = new[] { new HangfireBasicAuthFilter(Configuration["ADMIN:USERNAME"], Configuration["ADMIN:PASSWORD"]) }
			});

			app.UseSwagger(c => {
				c.RouteTemplate = "api-docs/{documentName}/swagger.json";
			});

			app.UseSwaggerUI(ctx => {
				ctx.RoutePrefix = "api-docs";
				ctx.SwaggerEndpoint("/api-docs/v2/swagger.json", "Relisten API v2");
			});

			app.UseCors(builder => builder.WithMethods("GET", "POST", "OPTIONS", "HEAD").WithOrigins("*").AllowAnyMethod());
		}
	}
}
