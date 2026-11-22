using Microsoft.Extensions.Logging.Abstractions;
using Relisten.Search;

namespace RelistenApiTests.Search;

[TestFixture, Category("LocalIngestion")]
public sealed class TestSearchIndexBuild
{
    [Test]
    public async Task RejectedDocumentLeavesAllAliasesOnTheLastCompleteBuild()
    {
        MigratedTestDatabase.RequireLocalDatabase();
        var prefix = "test_search_" + Guid.NewGuid().ToString("N");
        using var http = new HttpClient();
        TypesenseClient.Configure(http, "http://127.0.0.1:18108", "local_dev_typesense");
        var typesense = new TypesenseClient(http);
        var builder = new SearchIndexBuilder(typesense, NullLogger<SearchIndexBuilder>.Instance);
        var token = CancellationToken.None;

        try
        {
            await builder.Build(
                prefix,
                "first",
                BuildDocuments(new Dictionary<string, object> { ["id"] = "show" }),
                token
            );

            var invalidDocument = new Dictionary<string, object>
            {
                ["id"] = "show",
                ["date"] = new { invalid = true },
            };
            var error = Assert.ThrowsAsync<InvalidOperationException>(() =>
                builder.Build(prefix, "rejected", BuildDocuments(invalidDocument), token)
            );
            Assert.That(error!.Message, Does.Contain("rejected 1"));

            foreach (var kind in SearchIndexSchema.Collections)
            {
                var alias = SearchIndexBuilder.AliasName(prefix, kind);
                Assert.That(await typesense.ReadAlias(alias, token), Is.EqualTo(alias + "_first"));
            }
        }
        finally
        {
            foreach (var kind in SearchIndexSchema.Collections)
            {
                var alias = SearchIndexBuilder.AliasName(prefix, kind);
                await typesense.DeleteAlias(alias, token);
                await typesense.DeleteCollection(alias + "_first", token);
                await typesense.DeleteCollection(alias + "_rejected", token);
            }
        }
    }

    private static SearchBuildDocuments BuildDocuments(Dictionary<string, object> entity) =>
        new(
            Rows(new Dictionary<string, object> { ["id"] = "review" }),
            Rows(new Dictionary<string, object> { ["id"] = "performance" }),
            Rows(new Dictionary<string, object> { ["id"] = "recording" }),
            Rows(entity)
        );

    private static IAsyncEnumerable<Dictionary<string, object>> Rows(
        Dictionary<string, object> document
    ) => new[] { document }.ToAsyncEnumerable();
}
