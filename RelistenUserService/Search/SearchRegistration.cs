using RelistenUserService.Configuration;

namespace RelistenUserService.Search;

internal static class SearchRegistration
{
    public static void AddMergedSearch(
        this IServiceCollection services,
        AccountsRuntimeConfiguration runtime,
        IConfiguration configuration,
        IHostEnvironment environment
    )
    {
        var searchKey = configuration["Search:Typesense:SearchKey"];
        if (string.IsNullOrWhiteSpace(searchKey))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "Search:Typesense:SearchKey is required outside Development."
                );
            }

            searchKey = "local_dev_typesense";
        }

        services.AddScoped<MergedSearch>();
        services.AddScoped<PlaylistSearchCards>();
        services
            .AddHttpClient<CatalogSearchClient>(client =>
            {
                client.BaseAddress = new Uri(runtime.Options.CatalogBaseUrl.TrimEnd('/') + "/");
                // The catalog can use its full five-second submitted-search budget.
                client.Timeout = TimeSpan.FromSeconds(6);
            })
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(SearchHandler);
        services
            .AddHttpClient<PlaylistSearchCandidates>(client =>
            {
                var url = configuration["Search:Typesense:Url"] ?? "http://127.0.0.1:18108";
                client.BaseAddress = new Uri(url.TrimEnd('/') + "/");
                client.DefaultRequestHeaders.Add("X-TYPESENSE-API-KEY", searchKey);
            })
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(SearchHandler);
    }

    private static HttpClientHandler SearchHandler() =>
        new() { AllowAutoRedirect = false, UseCookies = false };
}
