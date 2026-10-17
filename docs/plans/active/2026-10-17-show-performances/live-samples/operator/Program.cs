using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Relisten;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Ingestion;
using Relisten.Ingestion.Providers.ArchiveCollections;
using Relisten.Ingestion.Providers.TapersSection;
using Relisten.Services.Indexing;
using Temporalio.Client;

// This local acceptance runner uses the same review, collection and Temporal admission
// contracts as an operator. It cannot publish sources directly or target a remote database.
var builder = Host.CreateApplicationBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var configuration = builder.Configuration;
if (
    !Uri.TryCreate(configuration["DATABASE_URL"], UriKind.Absolute, out var database)
    || !database.IsLoopback
    || !database.AbsolutePath.StartsWith("/relisten_", StringComparison.Ordinal)
    || database.AbsolutePath == "/relisten_db"
)
{
    throw new InvalidOperationException("Use an isolated relisten_* database on localhost.");
}

RequireLoopbackEndpoint(
    "http://" + (configuration["Temporal:Address"] ?? "localhost:7233"),
    "Temporal:Address"
);
RequireLoopbackEndpoint(configuration["REDIS_URL"], "REDIS_URL");
if (!string.IsNullOrWhiteSpace(configuration["Temporal:PayloadRedisUrl"]))
{
    RequireLoopbackEndpoint(configuration["Temporal:PayloadRedisUrl"], "Temporal:PayloadRedisUrl");
}

SqlMapper.AddTypeHandler(new PersistentIdentifierHandler());
SqlMapper.AddTypeHandler(new DateTimeHandler());
builder.Services.AddSingleton(sp => new DbService(
    configuration["DATABASE_URL"]!,
    null,
    null,
    builder.Environment,
    sp.GetRequiredService<ILogger<DbService>>()
));
builder.Services.AddSingleton(_ => new RedisService(
    RedisService.BuildConfiguration(configuration["REDIS_URL"]!)
));
builder.Services.AddRelistenCatalogServices();
builder.Services.AddTemporalIngestionClient(configuration);
using var host = builder.Build();
using var scope = host.Services.CreateScope();
var runner = new SampleOperator(scope.ServiceProvider);
await runner.RunAsync(args);

static void RequireLoopbackEndpoint(string? address, string setting)
{
    if (!Uri.TryCreate(address, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback)
    {
        throw new InvalidOperationException(
            $"The local sample runner requires {setting} on localhost."
        );
    }
}

/// <summary>
/// Applies an explicit reviewed sample, then admits ordinary workflows. Every committed
/// review is durable; rerun the same plan after interruption instead of inserting catalog rows.
/// </summary>
sealed class SampleOperator(IServiceProvider services)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly ArchiveIntakeService intake =
        services.GetRequiredService<ArchiveIntakeService>();
    private readonly ArchivePerformerReviewService reviews = new(
        services.GetRequiredService<DbService>()
    );
    private readonly TemporalIngestionClient ingestion =
        services.GetRequiredService<TemporalIngestionClient>();
    private readonly CollectionService collections =
        services.GetRequiredService<CollectionService>();

    public async Task RunAsync(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "review-plan":
                await ApplyReviewPlanAsync(args[1], args[2], args[3]);
                break;
            case "refresh":
                await RefreshAsync(args[1], args[2]);
                break;
            case "results":
                await ReadResultsAsync(args[1]);
                break;
            case "collection":
                await ConfigureCollectionAsync(args[1], args[2]);
                break;
            case "discover":
                await DiscoverAsync(Guid.Parse(args[1]), int.Parse(args[2]), args[3]);
                break;
            case "native-item":
                await ImportNativeItemAsync(Guid.Parse(args[1]), args[2], args[3]);
                break;
            case "provider-refresh":
                await RefreshProviderAsync(
                    args[1],
                    Guid.Parse(args[2]),
                    int.Parse(args[3]),
                    args[4]
                );
                break;
            case "import-result":
                await ReadImportResultAsync(args[1]);
                break;
            case "review-performance":
                await new ReviewedPerformanceOperator(services, Json).RunAsync(
                    args[1],
                    args[2],
                    args[3]
                );
                break;
            default:
                throw new ArgumentException(
                    "Use review-plan PLAN RAW_DIR OUTPUT, refresh BINDINGS OUTPUT, results ADMISSIONS, collection PLAN OUTPUT, discover SELECTOR_UUID MAX_PAGES OUTPUT, native-item ARTIST_UUID ITEM OUTPUT, provider-refresh phish-in|phish-net ARTIST_UUID YEAR OUTPUT, import-result ADMISSION, or review-performance PLAN RAW_DIR OUTPUT."
                );
        }
    }

    private async Task ApplyReviewPlanAsync(string planFile, string rawDirectory, string output)
    {
        var plan = Read<ReviewedSample>(planFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.ReviewBasis);
        var report = new PreparedSample();

        foreach (var project in plan.Projects)
        {
            if (!project.Approved)
            {
                throw new ArgumentException(
                    "Each project must have explicit artist approval in the reviewed plan."
                );
            }

            var prepared = await PrepareProjectAsync(project, plan.ReviewBasis);
            report.Projects.Add(prepared);
            Save(output, report);
        }

        foreach (var item in plan.Items)
        {
            var target = report.Projects.Single(project => project.Slug == item.ProjectSlug);
            var raw = await File.ReadAllTextAsync(
                Path.Combine(rawDirectory, item.Identifier + ".json")
            );
            var projection = ArchiveObservationProjector.Project(new(item.Identifier), raw);
            var observation = await intake.RetainObservationAsync(projection);
            var decision = await reviews.ReviewAsync(
                new(
                    item.Identifier,
                    target.CandidateId,
                    observation,
                    item.Evidence,
                    plan.ReviewBasis,
                    item.Approved,
                    item.Alternatives
                )
            );

            report.Items.Add(
                new(
                    item.Identifier,
                    target.Slug,
                    observation,
                    decision.Revision,
                    item.Approved,
                    item.Alternatives
                )
            );
            Save(output, report);
        }

        Console.WriteLine(
            JsonSerializer.Serialize(
                new
                {
                    projects = report.Projects.Count,
                    items = report.Items.Count,
                    output,
                },
                Json
            )
        );
    }

    private async Task<PreparedProject> PrepareProjectAsync(ReviewedProject project, string basis)
    {
        var artist = project.ExistingArtistUuid;
        if (project.CreateExistingArtistFixture)
        {
            var artists = services.GetRequiredService<ArtistService>();
            // The fixture is an ordinary pre-existing identity, without an invented
            // provider mapping. Look it up first so replay cannot create another artist.
            var existing = await artists.FindArtistForImport(project.Slug);
            if (existing != null && existing.name != project.Name)
            {
                throw new ArgumentException("The existing fixture slug belongs to another artist.");
            }
            artist =
                existing?.uuid
                ?? (
                    await artists.Save(
                        new SlimArtistWithFeatures
                        {
                            name = project.Name,
                            slug = project.Slug,
                            features = ArchiveOrgArtistDefaults.ArchiveOrgDefaultFeatures(),
                        }
                    )
                ).uuid;
        }

        var candidate = await intake.ProposeProjectAsync(
            project.Name,
            project.Slug,
            project.Labels,
            artist == null
        );
        var mapped = artist is { } existingArtist
            ? await intake.BindExistingArtistAsync(candidate, new(existingArtist), basis)
            : await intake.MaterializeArtistAsync(candidate)
                ?? throw new InvalidOperationException(
                    "A new project matched an existing or ambiguous identity; review its target explicitly."
                );

        return new(
            project.Name,
            project.Slug,
            candidate,
            mapped.Value,
            project.CreateExistingArtistFixture
        );
    }

    private async Task RefreshAsync(string bindingsFile, string output)
    {
        var bindings = Read<PreparedSample>(bindingsFile);
        var admissions = new List<ArtistAdmission>();

        foreach (var project in bindings.Projects)
        {
            var handle = await ingestion.RefreshTapersSectionArtistAsync(
                new(new(project.ArtistUuid))
            );
            admissions.Add(
                new(project.Slug, project.ArtistUuid, handle.WorkflowId, handle.ResultRunId)
            );
            Save(output, admissions);
        }

        await ReadResultsAsync(output);
    }

    private async Task ReadResultsAsync(string output)
    {
        var admissions = Read<List<ArtistAdmission>>(output);
        var client = services.GetRequiredService<ITemporalClient>();
        // Persist each handle before waiting. A process restart can inspect the exact
        // admitted runs even when some artists complete before others.
        foreach (var admission in admissions.Where(admission => admission.Result == null))
        {
            try
            {
                admission.Result = await client
                    .GetWorkflowHandle(admission.WorkflowId, admission.ResultRunId)
                    .GetResultAsync<TapersSectionArtistResult>();
                admission.FailureType = null;
            }
            catch (Exception error)
            {
                admission.FailureType = error.GetType().Name;
            }
            Save(output, admissions);
        }

        Console.WriteLine(
            JsonSerializer.Serialize(new { artists = admissions.Count, output }, Json)
        );
    }

    private async Task ConfigureCollectionAsync(string planFile, string output)
    {
        var plan = Read<SampleCollection>(planFile);
        await collections.SaveCollectionAsync(plan.Definition);
        foreach (var artist in plan.ArtistUuids)
        {
            await collections.SetArtistMembershipAsync(
                plan.Definition.Uuid,
                artist,
                true,
                plan.ReviewBasis
            );
        }

        if (plan.SelectorUuid is { } selector)
        {
            var source =
                await services
                    .GetRequiredService<UpstreamSourceService>()
                    .FindUpstreamSourceByName(UpstreamProvider.ArchiveOrg)
                ?? throw new InvalidOperationException("Archive.org provider is missing.");
            await collections.SaveSelectorAsync(
                new(selector, plan.Definition.Uuid, source.id, plan.SelectorIdentifier!)
            );
            foreach (var identifier in plan.ItemIdentifiers)
            {
                await collections.SetProviderMembershipAsync(
                    selector,
                    identifier,
                    true,
                    plan.ReviewBasis
                );
            }
        }

        Save(output, await collections.FindAsync(plan.Definition.Slug));
    }

    private async Task DiscoverAsync(Guid selector, int maxPages, string output)
    {
        var handle = await ingestion.DiscoverArchiveCollectionAsync(new(selector, maxPages));
        Save(
            output,
            new
            {
                handle.WorkflowId,
                handle.ResultRunId,
                status = "admitted",
            }
        );
        var result = await handle.GetResultAsync();
        Save(
            output,
            new
            {
                handle.WorkflowId,
                handle.ResultRunId,
                result,
            }
        );
    }

    private async Task ImportNativeItemAsync(Guid artist, string item, string output)
    {
        var handle = await ingestion.ImportArchiveItemAsync(new(new(artist), new(item)));
        Save(
            output,
            new
            {
                handle.WorkflowId,
                handle.ResultRunId,
                status = "admitted",
            }
        );
        await ReadImportResultAsync(output);
    }

    private async Task RefreshProviderAsync(string provider, Guid artist, int year, string output)
    {
        ArtistRefreshCommand request = provider switch
        {
            "phish-in" => new RefreshPhishInArtist(new(artist), year),
            "phish-net" => new RefreshPhishNetArtist(new(artist), year),
            _ => throw new ArgumentException("Use phish-in or phish-net."),
        };
        var handle = await ingestion.RefreshAsync(request);
        Save(
            output,
            new
            {
                handle.WorkflowId,
                handle.ResultRunId,
                status = "admitted",
            }
        );
        await ReadImportResultAsync(output);
    }

    private async Task ReadImportResultAsync(string output)
    {
        var admission = Read<ImportAdmission>(output);
        var result = await services
            .GetRequiredService<ITemporalClient>()
            .GetWorkflowHandle(admission.WorkflowId, admission.ResultRunId)
            .GetResultAsync<ArtistImportResult>();
        Save(
            output,
            new
            {
                admission.WorkflowId,
                admission.ResultRunId,
                result,
            }
        );
    }

    private static T Read<T>(string file) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json)
        ?? throw new ArgumentException("A JSON plan object is required.");

    private static void Save<T>(string path, T value)
    {
        File.WriteAllText(
            path + ".tmp",
            JsonSerializer.Serialize(value, Json) + Environment.NewLine
        );
        File.Move(path + ".tmp", path, true);
    }
}

sealed record ImportAdmission(string WorkflowId, string ResultRunId);

sealed record ReviewedSample(string ReviewBasis, ReviewedProject[] Projects, ReviewedItem[] Items);

sealed record ReviewedProject(
    string Name,
    string Slug,
    string[] Labels,
    bool Approved,
    Guid? ExistingArtistUuid = null,
    bool CreateExistingArtistFixture = false
);

sealed record ReviewedItem(
    string Identifier,
    string ProjectSlug,
    TapersSectionEvidence[] Evidence,
    bool Approved = false,
    string[]? Alternatives = null
);

sealed record PreparedProject(
    string Name,
    string Slug,
    Guid CandidateId,
    Guid ArtistUuid,
    bool ExistingArtistFixture
);

sealed record PreparedItem(
    string Identifier,
    string ProjectSlug,
    Guid ObservationId,
    long Revision,
    bool Approved,
    string[]? Alternatives
);

sealed class PreparedSample
{
    public List<PreparedProject> Projects { get; init; } = [];
    public List<PreparedItem> Items { get; init; } = [];
}

sealed record ArtistAdmission(string Slug, Guid ArtistUuid, string WorkflowId, string ResultRunId)
{
    public TapersSectionArtistResult? Result { get; set; }
    public string? FailureType { get; set; }
}

sealed record SampleCollection(
    CollectionDefinition Definition,
    string ReviewBasis,
    Guid[] ArtistUuids,
    Guid? SelectorUuid = null,
    string? SelectorIdentifier = null,
    string[]? SelectedItems = null
)
{
    public string[] ItemIdentifiers => SelectedItems ?? [];
}
