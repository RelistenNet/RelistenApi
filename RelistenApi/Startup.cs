// using Hangfire.PostgreSql;

using System;
using Dapper;
using Hangfire;
using Hangfire.Console;
using Hangfire.RecurringJobExtensions;
using Hangfire.Redis;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Newtonsoft.Json;
using Relisten.Api.Models;
using Relisten.Api.Models.Api;
using Relisten.Data;
using Relisten.Import;
using Relisten.Services.Auth;
using SimpleMigrations;
using SimpleMigrations.DatabaseProvider;
using StackExchange.Redis;

namespace Relisten
{
    public class Startup
    {
        public Startup(IConfiguration configuration, IHostEnvironment hostEnvironment)
        {
            Configuration = configuration;
            HostEnvironment = hostEnvironment;
        }

        private IConfiguration Configuration { get; }
        private IHostEnvironment HostEnvironment { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            SetupApplicationInsightsFilters();

            services.AddCors();
            services.AddResponseCompression();
            services.AddHttpContextAccessor();
            services.AddTransient<IConfigureOptions<MvcNewtonsoftJsonOptions>, RelistenApiJsonOptionsWrapper>();

            SetupAuthentication(services);

            // Add framework services.
            services.AddMvc(mvcOptions =>
            {
                mvcOptions.EnableEndpointRouting = false;
            }).AddNewtonsoftJson();

            services.AddLogging(loggingBuilder =>
            {
                loggingBuilder.AddConfiguration(Configuration.GetSection("Logging"));
                loggingBuilder.SetMinimumLevel(HostEnvironment.IsProduction()
                    ? LogLevel.Warning
                    : LogLevel.Information);
                loggingBuilder.AddConsole();
                loggingBuilder.AddDebug();
            });

            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v2",
                    new OpenApiInfo
                    {
                        Version = "v2",
                        Title = "Relisten API",
                        Contact =
                            new OpenApiContact {Name = "Alec Gorge", Url = new Uri("https://twitter.com/alecgorge")},
                        License = new OpenApiLicense
                        {
                            Name = "MIT", Url = new Uri("https://opensource.org/licenses/MIT")
                        }
                    });

                c.SwaggerDoc("v3",
                    new OpenApiInfo
                    {
                        Version = "v3",
                        Title = "Relisten API",
                        Contact =
                            new OpenApiContact {Name = "Alec Gorge", Url = new Uri("https://twitter.com/alecgorge")},
                        License = new OpenApiLicense
                        {
                            Name = "MIT", Url = new Uri("https://opensource.org/licenses/MIT")
                        }
                    });

                c.SchemaFilter<SwaggerSkipV2PropertyFilter>();
            });

            SqlMapper.AddTypeHandler(new PersistentIdentifierHandler());
            SqlMapper.AddTypeHandler(new DateTimeHandler());

            JsonConvert.DefaultSettings = () => new JsonSerializerSettings
            {
                DateTimeZoneHandling = DateTimeZoneHandling.Utc
            };

            var db = new DbService(Configuration["DATABASE_URL"], HostEnvironment);
            RunMigrations(db);
            services.AddSingleton(db);

            var configurationOptions = RedisService.BuildConfiguration(Configuration["REDIS_URL"]);

            // use the static property because it is formatted correctly for NpgSQL
            services.AddHangfire(hangfire =>
            {
                // processed into a connection string
                // hangfire.UsePostgreSqlStorage(DbService.ConnStr);

                hangfire.UseRedisStorage(ConnectionMultiplexer.Connect(configurationOptions),
                    new RedisStorageOptions {InvisibilityTimeout = TimeSpan.FromHours(4)});
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
            services.AddScoped<LocalImporter, LocalImporter>();
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
            services.AddScoped<SourceTrackPlaysService, SourceTrackPlaysService>();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILoggerFactory loggerFactory)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseResponseCompression();
            app.UseCors(builder => builder
                .WithMethods("GET", "POST", "OPTIONS", "HEAD")
                .WithOrigins("*")
                .AllowAnyMethod());

            app.UseAuthentication();
            app.UseStaticFiles();

            app.UseMvc();

            if (!env.IsDevelopment())
            {
                app.UseHangfireServer(new BackgroundJobServerOptions
                {
                    Queues = new[] {"artist_import"},
                    ServerName = $"relistenapi:artist_import ({Environment.MachineName})",
                    WorkerCount = 3
                });

                app.UseHangfireServer(new BackgroundJobServerOptions
                {
                    Queues = new[] {"default"}, ServerName = $"relistenapi:default ({Environment.MachineName})"
                });

                app.UseHangfireDashboard("/relisten-admin/hangfire",
                    new DashboardOptions {Authorization = new[] {new MyAuthorizationFilter()}});
            }

            app.UseSwagger(c =>
            {
                c.RouteTemplate = "api-docs/{documentName}/swagger.json";
            });

            app.UseSwaggerUI(ctx =>
            {
                ctx.RoutePrefix = "api-docs";
                ctx.SwaggerEndpoint("/api-docs/v2/swagger.json", "Relisten API v2");
            });

            app.UseSwaggerUI(ctx =>
            {
                ctx.RoutePrefix = "api-docs-v3";
                ctx.SwaggerEndpoint("/api-docs/v3/swagger.json", "Relisten API v3");
            });

            app.UseCors(builder =>
                builder.WithMethods("GET", "POST", "OPTIONS", "HEAD").WithOrigins("*").AllowAnyMethod());
        }

        public void RunMigrations(DbService db)
        {
            var migrationsAssembly = typeof(Startup).Assembly;
            using (var pg = db.CreateConnection(true))
            {
                var databaseProvider = new PostgresqlDatabaseProvider(pg);
                var migrator = new SimpleMigrator(migrationsAssembly, databaseProvider);
                migrator.Load();

                if (migrator.CurrentMigration == null || migrator.CurrentMigration.Version == 0)
                {
                    migrator.Baseline(2);
                }

                migrator.MigrateTo(6);

                if (migrator.LatestMigration.Version != migrator.CurrentMigration.Version)
                {
                    throw new Exception(
                        $"The newest available migration ({migrator.LatestMigration.Version}) != The current database migration ({migrator.CurrentMigration.Version}). You probably need to add a call to run the migration.");
                }
            }
        }

        public void SetupApplicationInsightsFilters()
        {
            var builder = TelemetryConfiguration.Active.TelemetryProcessorChainBuilder;
            builder.Use(next => new HangfireRequestFilter(next));

            builder.Build();
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
                options.Password.RequiredLength = 1;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireDigit = false;
                options.Password.RequireNonAlphanumeric = false;
            }).AddDefaultTokenProviders();

            services.ConfigureApplicationCookie(options =>
            {
                options.LoginPath = "/relisten-admin/login";

                options.ExpireTimeSpan = TimeSpan.FromDays(365);
            });

            services.Configure<SecurityStampValidatorOptions>(options =>
            {
                // enables immediate logout, after updating the user's stat.
                options.ValidationInterval = TimeSpan.FromDays(365);
            });
        }
    }

    public class HangfireRequestFilter : ITelemetryProcessor
    {
        // Link processors to each other in a chain.
        public HangfireRequestFilter(ITelemetryProcessor next)
        {
            Next = next;
        }

        private ITelemetryProcessor Next { get; }

        // You can pass values from .config
        public string MyParamFromConfigFile { get; set; }

        public void Process(ITelemetry item)
        {
            // To filter out an item, just return
            if (!OKtoSend(item)) { return; }

            Next.Process(item);
        }

        // Example: replace with your own criteria.
        private bool OKtoSend(ITelemetry item)
        {
            var request = item as RequestTelemetry;
            if (request == null)
            {
                return true;
            }

            return !request.Url.AbsolutePath.StartsWith("/relisten-admin/hangfire");
        }
    }

    public class RelistenApiJsonOptionsWrapper : IConfigureOptions<MvcNewtonsoftJsonOptions>
    {
        public static readonly JsonSerializerSettings DefaultSerializerSettings =
            ApplyDefaultSerializerSettings(new JsonSerializerSettings());

        public static readonly JsonSerializerSettings ApiV3SerializerSettings =
            new Lazy<JsonSerializerSettings>(() =>
            {
                var settings = ApplyDefaultSerializerSettings(new JsonSerializerSettings());
                settings.ContractResolver = new ApiV3ContractResolver();

                return settings;
            }).Value;

        public void Configure(MvcNewtonsoftJsonOptions jsonOptions)
        {
            ApplyDefaultSerializerSettings(jsonOptions.SerializerSettings);
        }

        private static JsonSerializerSettings ApplyDefaultSerializerSettings(JsonSerializerSettings settings)
        {
            settings.DateFormatString = "yyyy'-'MM'-'dd'T'HH':'mm':'ssK";
            settings.DateTimeZoneHandling = DateTimeZoneHandling.Utc;
            return settings;
        }
    }
}
