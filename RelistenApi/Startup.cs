using System;
using System.Linq;
using Dapper;
using Hangfire;
using Hangfire.Console;
using Hangfire.RecurringJobExtensions;
using Hangfire.Redis.StackExchange;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Newtonsoft.Json;
using Npgsql;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Relisten.Api.Models;
using Relisten.Api.Models.Api;
using Relisten.Data;
using Relisten.Import;
using Relisten.Services.Indexing;
using Relisten.Services.Auth;
using Relisten.Services.Popularity;
using Relisten.Vendor.ArchiveOrg;
using Serilog;
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

            var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
            if (otlpEndpoint != null)
            {
                services.AddOpenTelemetry()
                    .ConfigureResource(resource =>
                        resource.AddService(serviceName: "relistenapi"))
                    .WithTracing(tracing => tracing
                        .AddAspNetCoreInstrumentation(options =>
                        {
                            options.EnrichWithHttpResponse = (activity, response) =>
                            {
                                var endpoint = response.HttpContext.GetEndpoint();
                                if (endpoint is RouteEndpoint routeEndpoint)
                                {
                                    var descriptor = routeEndpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
                                    if (descriptor is not null)
                                    {
                                        var controller = descriptor.ControllerName;
                                        var action = descriptor.ActionName;

                                        var pathParameters = descriptor.Parameters
                                            .Where(p => p.BindingInfo is null ||
                                                        p.BindingInfo.BindingSource?.Id == "Path")
                                            .Select(p => $"{{{p.Name}}}");

                                        var route = string.Join("/", [controller, action, .. pathParameters]);

                                        activity.DisplayName = $"{route}";
                                        activity.SetTag("http.route", route);
                                    }
                                }
                            };
                        })
                        .AddNpgsql()
                        .AddOtlpExporter(otlpOptions =>
                        {
                            otlpOptions.Endpoint = new Uri(otlpEndpoint);

                            otlpOptions.Protocol = OtlpExportProtocol.Grpc;
                        }))
                    .WithMetrics(metrics => metrics
                        .AddAspNetCoreInstrumentation()
                        .AddMeter("Npgsql")
                        .AddOtlpExporter(otlpOptions =>
                        {
                            otlpOptions.Endpoint = new Uri(otlpEndpoint);
                            otlpOptions.Protocol = OtlpExportProtocol.Grpc;
                        }));
            }


            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v2",
                    new OpenApiInfo
                    {
                        Version = "v2",
                        Title = "Relisten API",
                        Contact =
                            new OpenApiContact { Name = "Alec Gorge", Url = new Uri("https://twitter.com/alecgorge") },
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
                            new OpenApiContact { Name = "Alec Gorge", Url = new Uri("https://twitter.com/alecgorge") },
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

            services.AddSingleton<DbService>(serviceProvider =>
            {
                var dbUrl = !string.IsNullOrWhiteSpace(Configuration["PGBOUNCER_DATABASE_URL"])
                    ? Configuration["PGBOUNCER_DATABASE_URL"]
                    : Configuration["DATABASE_URL"];

                var logger = serviceProvider.GetRequiredService<ILogger<DbService>>();
                var db = new DbService(dbUrl!, HostEnvironment, logger);

                // Run migrations immediately after construction
                using (var pg = db.CreateConnection(longTimeout: true, readOnly: false))
                {
                    var migrationsAssembly = typeof(Startup).Assembly;
                    var databaseProvider = new PostgresqlDatabaseProvider(pg);
                    var migrator = new SimpleMigrator(migrationsAssembly, databaseProvider);
                    migrator.Load();

                    if (migrator.CurrentMigration == null || migrator.CurrentMigration.Version == 0)
                    {
                        migrator.Baseline(2);
                    }

                    migrator.MigrateTo(8);

                    if (migrator.LatestMigration.Version != migrator.CurrentMigration!.Version)
                    {
                        throw new Exception(
                            $"The newest available migration ({migrator.LatestMigration.Version}) != The current database migration ({migrator.CurrentMigration!.Version}). You probably need to add a call to run the migration.");
                    }
                }

                return db;
            });

            var configurationOptions = RedisService.BuildConfiguration(Configuration["REDIS_URL"]!);

            // use the static property because it is formatted correctly for NpgSQL
            services.AddHangfire(hangfire =>
            {
                // processed into a connection string
                // hangfire.UsePostgreSqlStorage(DbService.ConnStr);

                hangfire.UseRedisStorage(ConnectionMultiplexer.Connect(configurationOptions),
                    new RedisStorageOptions { InvisibilityTimeout = TimeSpan.FromHours(4) });
                hangfire.UseConsole();
                hangfire.UseRecurringJob(typeof(ScheduledService));
            });

            if (!HostEnvironment.IsDevelopment())
            {
                if (!string.IsNullOrEmpty(Configuration["ENABLE_HANGFIRE_SERVER"]) &&
                    Configuration["ENABLE_HANGFIRE_SERVER"] != "false")
                {
                    services.AddHangfireServer(options =>
                    {
                        options.Queues = ["artist_import"];
                        options.ServerName = $"relistenapi:artist_import ({Environment.MachineName})";
                        options.WorkerCount = 10;
                    });

                    services.AddHangfireServer(options =>
                    {
                        options.Queues = ["default"];
                        options.ServerName = $"relistenapi:default ({Environment.MachineName})";
                    });
                }
            }

            var redis = new RedisService(configurationOptions);
            services.AddSingleton(redis);
            services.AddSingleton(Configuration);

            services.AddDataProtection()
                .PersistKeysToStackExchangeRedis(redis.connection, "DataProtection-Keys")
                .SetApplicationName("RelistenApi");

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
            services.AddScoped<IUpstreamSourceLookup>(sp => sp.GetRequiredService<UpstreamSourceService>());
            services.AddScoped<IArchiveOrgCollectionIndexClient, ArchiveOrgCollectionIndexClient>();
            services.AddScoped<IArchiveOrgArtistIndexRepository, ArchiveOrgArtistIndexRepository>();
            services.AddScoped<ArchiveOrgArtistIndexer, ArchiveOrgArtistIndexer>();
            services.AddScoped<PopularityCacheService, PopularityCacheService>();
            services.AddScoped<PopularityService, PopularityService>();
            services.AddScoped<PopularityJobs, PopularityJobs>();
            services.AddScoped<ScheduledService, ScheduledService>();
            services.AddScoped<SearchService, SearchService>();
            services.AddScoped<LinkService, LinkService>();
            services.AddScoped<SourceTrackPlaysService, SourceTrackPlaysService>();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseResponseCompression();

            app.UseSerilogRequestLogging(options =>
            {
                options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
                {
                    diagnosticContext.Set("UserAgent", httpContext.Request.Headers["User-Agent"].ToString());
                    diagnosticContext.Set("RemoteIP", httpContext.Connection.RemoteIpAddress);
                };
            });

            app.UseCors(builder => builder
                .WithMethods("GET", "POST", "OPTIONS", "HEAD")
                .WithOrigins("*")
                .AllowAnyMethod());

            app.UseAuthentication();
            app.UseStaticFiles();

            app.UseMvc();

            if (!env.IsDevelopment())
            {
                app.UseHangfireDashboard("/relisten-admin/hangfire",
                    new DashboardOptions { Authorization = [new MyAuthorizationFilter()] });
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
