using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Relisten;
using Relisten.Api.Models;
using Relisten.Ingestion;

// This host deliberately does not construct Startup: it owns no HTTP server,
// authentication middleware, Hangfire server, or automatic catalog migrations.
var builder = Host.CreateApplicationBuilder(args);
SqlMapper.AddTypeHandler(new PersistentIdentifierHandler());
SqlMapper.AddTypeHandler(new DateTimeHandler());
JsonConvert.DefaultSettings = () =>
    new JsonSerializerSettings { DateTimeZoneHandling = DateTimeZoneHandling.Utc };
builder.Services.AddSingleton(sp => new DbService(
    builder.Configuration["DATABASE_URL"]
        ?? throw new InvalidOperationException("DATABASE_URL is required"),
    builder.Configuration["PGBOUNCER_WRITE_HOST"],
    builder.Configuration["PGBOUNCER_READ_ONLY_HOST"],
    builder.Environment,
    sp.GetRequiredService<ILogger<DbService>>()
));
builder.Services.AddSingleton(_ => new RedisService(
    RedisService.BuildConfiguration(
        builder.Configuration["REDIS_URL"]
            ?? throw new InvalidOperationException("REDIS_URL is required")
    )
));
builder.Services.AddRelistenCatalogServices();
await builder.Build().RunAsync();
