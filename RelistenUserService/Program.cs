using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RelistenUserService.Authentication;
using RelistenUserService.Configuration;
using RelistenUserService.Http;
using RelistenUserService.Persistence;

var builder = WebApplication.CreateBuilder(args);
var accountsOptions = builder.Configuration
    .GetSection(AccountsOptions.SectionName)
    .Get<AccountsOptions>()
    ?? throw new InvalidOperationException("The Accounts configuration section is required.");
var runtime = AccountsRuntimeConfiguration.Create(accountsOptions, builder.Environment);

builder.Services.AddProblemDetails();
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
});
builder.Services.Configure<ApiBehaviorOptions>(AccountApiBehavior.Configure);
builder.Services.AddRelistenAccounts(
    builder.Configuration,
    builder.Environment,
    runtime);

var app = builder.Build();
if (runtime.Options.ApplyMigrationsOnStartup)
{
    // The first production rollout runs one no-surge replica, so schema installation
    // completes before the pod accepts traffic. Move this back to an operator Job before
    // scaling the service beyond one replica.
    await app.Services.GetRequiredService<AccountsDatabaseMigrator>()
        .MigrateAsync(app.Lifetime.ApplicationStopping);
}

if (runtime.Options.EnableDevelopmentPersonas)
{
    // Finish local client setup before Data Protection's hosted service tries to
    // load its database-backed key ring during app startup.
    await app.Services.GetRequiredService<DevelopmentDatabaseInitializer>()
        .InitializeAsync(app.Lifetime.ApplicationStopping);
}

// TLS terminates at the cluster ingress. Only configured ingress networks may tell
// OpenIddict that the original request was HTTPS or select a public Relisten host.
app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseMiddleware<HostBoundaryMiddleware>();
app.UseRouting();
app.UseMiddleware<RefreshTokenReplayMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

if (runtime.Options.EnableDevelopmentPersonas)
{
    app.MapDevelopmentPersonaEndpoints();
}

app.MapControllers();
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
app.MapGet(
    "/health/ready",
    async (AccountsDbContext dbContext, CancellationToken cancellationToken) =>
        await dbContext.Database.CanConnectAsync(cancellationToken)
            ? Results.Ok(new { status = "ready" })
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
app.Run();

public partial class Program;
