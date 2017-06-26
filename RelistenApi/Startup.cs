using System;
using System.Linq;
using System.Net;
using Hangfire;
using Hangfire.Console;
// using Hangfire.PostgreSql;
using Hangfire.RecurringJobExtensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
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

            SetupAuthentication(services);

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

			services.AddSingleton(new DbService(Configuration["DATABASE_URL"]));

			var configurationOptions = RedisService.BuildConfiguration(Configuration["REDIS_URL"]);

            // use the static property because it is formatted correctly for NpgSQL
			services.AddHangfire(hangfire => {
                // processed into a connection string
                // hangfire.UsePostgreSqlStorage(DbService.ConnStr);

				hangfire.UseRedisStorage(ConnectionMultiplexer.Connect(configurationOptions));
				hangfire.UseConsole();
				hangfire.UseRecurringJob(typeof(ScheduledService));
			});

			services.AddSingleton(new RedisService(configurationOptions));
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
            services.AddScoped<UpstreamSourceService, UpstreamSourceService>();
			services.AddScoped<ScheduledService, ScheduledService>();
			services.AddScoped<SearchService, SearchService>();
			services.AddScoped<LinkService, LinkService>();
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

            app.UseIdentity();
            app.UseStaticFiles();

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

			app.UseHangfireDashboard("/relisten-admin/hangfire", new DashboardOptions
			{
				Authorization = new[] { new MyAuthorizationFilter() }
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

        public void SetupAuthentication(IServiceCollection services)
        {
            var userStore = new EnvUserStore(Configuration);
			var roleStore = new EnvRoleStore();
			services.AddScoped<IPasswordHasher<ApplicationUser>, PlaintextHasher>();
			services.AddSingleton<IUserStore<ApplicationUser>>(userStore);
			services.AddSingleton<IUserPasswordStore<ApplicationUser>>(userStore);
			services.AddSingleton<IRoleStore<ApplicationRole>>(roleStore);
			services.AddSingleton<IUserClaimsPrincipalFactory<ApplicationUser>, EnvUserPrincipalFactory>();

			services.AddAuthentication();
			services.AddAuthorization();
			services.AddIdentity<ApplicationUser, ApplicationRole>(options =>
			{
				options.SecurityStampValidationInterval = TimeSpan.FromDays(365);

				options.Password.RequiredLength = 1;
				options.Password.RequireLowercase = false;
				options.Password.RequireUppercase = false;
				options.Password.RequireDigit = false;
				options.Password.RequireNonAlphanumeric = false;

                options.Cookies.ApplicationCookie.AutomaticChallenge = true;
                options.Cookies.ApplicationCookie.LoginPath = "/relisten-admin/login";

                options.Cookies.ApplicationCookie.ExpireTimeSpan = TimeSpan.FromDays(365);
			}).AddDefaultTokenProviders();
		}
	}
}
