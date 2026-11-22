using Microsoft.Extensions.Logging.Abstractions;
using Relisten.Search;

namespace RelistenApiTests.Search;

[TestFixture, Category("LocalIngestion")]
public sealed class TestSearchIndexBuild
{
    [Test]
    public async Task RejectedDocumentRemovesTheFailedBuildAndKeepsThePublishedCollections()
    {
        MigratedTestDatabase.RequireLocalDatabase();
        await using var index = new TestIndex();
        var first = await index.Build();
        var rejected = Guid.NewGuid().ToString("N");
        var invalid = new Dictionary<string, object>
        {
            ["id"] = "show",
            ["date"] = new { invalid = true },
        };

        Assert.ThrowsAsync<InvalidOperationException>(() => index.Build(rejected, invalid));

        Assert.That(await index.Collections(), Is.EquivalentTo(index.Names(first)));
        await index.AssertPublished(first);
    }

    [Test]
    public async Task RebuildSweepsAbandonedCollectionsAndRetirePreservesTheNewAliasTargets()
    {
        MigratedTestDatabase.RequireLocalDatabase();
        await using var index = new TestIndex();
        var first = await index.Build();
        var abandoned = Guid.NewGuid().ToString("N");
        await index.CreatePerformanceCollection(abandoned);

        var second = await index.Build();

        Assert.That(
            await index.Collections(),
            Is.EquivalentTo(index.Names(first).Concat(index.Names(second)))
        );
        await index.AssertPublished(second);

        // The old timer can wake before the new build's grace period ends.
        await index.Retire(first);
        Assert.That(
            await index.Collections(),
            Is.EquivalentTo(index.Names(first).Concat(index.Names(second)))
        );

        await index.Retire(second);

        Assert.That(await index.Collections(), Is.EquivalentTo(index.Names(second)));
        await index.AssertPublished(second);
    }

    [Test]
    public async Task PrefixedCollectionOutsideBuildNamingSurvivesBothSweeps()
    {
        MigratedTestDatabase.RequireLocalDatabase();
        await using var index = new TestIndex();
        var preserved = await index.CreatePerformanceCollection("keep");

        var build = await index.Build();
        Assert.That(await index.Collections(), Does.Contain(preserved));

        await index.Retire(build);
        Assert.That(await index.Collections(), Does.Contain(preserved));
    }

    private sealed class TestIndex : IAsyncDisposable
    {
        private readonly string prefix = "test_search_" + Guid.NewGuid().ToString("N");
        private readonly HttpClient http = new();
        private readonly TypesenseClient client;
        private readonly SearchIndexBuilder builder;

        public TestIndex()
        {
            TypesenseClient.Configure(http, "http://127.0.0.1:18108", "local_dev_typesense");
            client = new TypesenseClient(http);
            builder = new SearchIndexBuilder(client, NullLogger<SearchIndexBuilder>.Instance);
        }

        public async Task<string> Build()
        {
            var buildId = Guid.NewGuid().ToString("N");
            await Build(buildId, new Dictionary<string, object> { ["id"] = "show" });
            return buildId;
        }

        public Task Build(string buildId, Dictionary<string, object> entity) =>
            builder.Build(
                prefix,
                buildId,
                new SearchBuildDocuments(
                    Rows("review"),
                    Rows("performance"),
                    Rows("recording"),
                    new[] { entity }.ToAsyncEnumerable()
                ),
                CancellationToken.None
            );

        public async Task<string> CreatePerformanceCollection(string suffix)
        {
            var kind = SearchIndexSchema.Performances;
            var name = Alias(kind) + "_" + suffix;
            await client.CreateCollection(
                SearchIndexSchema.Create(kind, name),
                CancellationToken.None
            );
            return name;
        }

        public IEnumerable<string> Names(string buildId) =>
            SearchIndexSchema.Collections.Select(kind => Alias(kind) + "_" + buildId);

        public async Task<string[]> Collections() =>
            (await client.ReadCollectionNames(CancellationToken.None))
                .Where(name => name.StartsWith(prefix + "_", StringComparison.Ordinal))
                .ToArray();

        public async Task AssertPublished(string buildId)
        {
            foreach (var kind in SearchIndexSchema.Collections)
            {
                var alias = Alias(kind);
                Assert.That(
                    await client.ReadAlias(alias, CancellationToken.None),
                    Is.EqualTo(alias + "_" + buildId)
                );
            }
        }

        public Task Retire(string buildId) =>
            builder.RemoveRetired(prefix, buildId, CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (var kind in SearchIndexSchema.Collections)
                {
                    await client.DeleteAlias(Alias(kind), CancellationToken.None);
                }

                foreach (var name in await Collections())
                {
                    await client.DeleteCollection(name, CancellationToken.None);
                }
            }
            finally
            {
                http.Dispose();
            }
        }

        private string Alias(string kind) => SearchIndexBuilder.AliasName(prefix, kind);

        private static IAsyncEnumerable<Dictionary<string, object>> Rows(string id) =>
            new[] { new Dictionary<string, object> { ["id"] = id } }.ToAsyncEnumerable();
    }
}
