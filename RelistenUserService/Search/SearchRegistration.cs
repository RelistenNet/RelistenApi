using RelistenUserService.Configuration;

namespace RelistenUserService.Search;

internal static class SearchRegistration
{
    public static void AddMergedSearch(
        this IServiceCollection services,
        AccountsRuntimeConfiguration runtime,
        IConfiguration configuration
    )
    {
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
                client.DefaultRequestHeaders.Add(
                    "X-TYPESENSE-API-KEY",
                    configuration["Search:Typesense:SearchKey"]
                        ?? configuration["Search:Typesense:ApiKey"]
                        ?? "local_dev_typesense"
                );
            })
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(SearchHandler);
    }

    private static HttpClientHandler SearchHandler() =>
        new() { AllowAutoRedirect = false, UseCookies = false };
}
