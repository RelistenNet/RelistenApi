using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Relisten.Data;
using Relisten.Import;

namespace Relisten
{
    public class Startup
    {
        public Startup(IHostingEnvironment env)
        {
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
                AddJsonOptions(jsonOptions => {
                    jsonOptions.SerializerSettings.DateFormatString = "yyyy'-'MM'-'dd'T'HH':'mm':'ssK";
                    jsonOptions.SerializerSettings.DateTimeZoneHandling = DateTimeZoneHandling.Utc;
                });

            services.AddLogging();

            DbService.SetConnectionURL(Configuration["POSTGRES_URL_INT"]);

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
            services.AddScoped<Importer, Importer>();
            services.AddScoped<JerryGarciaComImporter, JerryGarciaComImporter>();
            services.AddScoped<PanicStreamComImporter, PanicStreamComImporter>();
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
        }
    }
}
