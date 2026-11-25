using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Relisten.Search;

namespace RelistenApiTests.Search;

public sealed partial class TestCatalogSearchHttp
{
    [Test]
    public async Task ReviewScopeUsesRefreshedEvidenceOnTheSameIndex()
    {
        var refresh = _app!.Services.GetRequiredService<CatalogQueryEvidenceRefreshService>();
        var store = _app.Services.GetRequiredService<CatalogQueryEvidenceStore>();
        var baseline = store.Current;
        var query =
            $"?q=remarkable%20encore&scope=reviews&typing=true&artist_id={Id("artist", 14)}";
        Results(await GetSearch(query)).Should().ContainSingle();

        // The search document can keep the old owner until the next artist reindex.
        // Scope eligibility comes from the API's own snapshot of the catalog.
        await using var connection = await MigratedTestDatabase.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE source_reviews SET source_id = 910001 WHERE uuid = @uuid",
            new { uuid = _review }
        );
        try
        {
            Results(await GetSearch(query)).Should().ContainSingle();
            _evidenceClock.Advance(TimeSpan.FromHours(1));
            refresh.RefreshNow(); // Wake the poll without waiting for its real-time interval.

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (ReferenceEquals(store.Current, baseline))
            {
                await Task.Delay(25, deadline.Token);
            }

            store.Current.Collection.Should().Be(baseline.Collection);
            Results(await GetSearch(query)).Should().BeEmpty();
        }
        finally
        {
            await connection.ExecuteAsync(
                "UPDATE source_reviews SET source_id = 910004 WHERE uuid = @uuid",
                new { uuid = _review }
            );
            store.Publish(baseline);
        }
    }

    private sealed class EvidenceClock : TimeProvider
    {
        private long _offset;
        public TaskCompletionSource BackoffStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override long GetTimestamp() => base.GetTimestamp() + Interlocked.Read(ref _offset);

        // Exercise generation recovery without making the fixture wait out production backoff.
        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            BackoffStarted.TrySetResult();
            return base.CreateTimer(callback, state, TimeSpan.FromMilliseconds(100), period);
        }

        public void Advance(TimeSpan elapsed) =>
            Interlocked.Add(ref _offset, (long)(elapsed.TotalSeconds * TimestampFrequency));
    }

    [Test]
    public async Task SearchesRecoverWhenThePinnedGenerationIsRemovedDuringReloadFailure()
    {
        var token = CancellationToken.None;
        var refreshed = _app!.Services.GetRequiredService<CatalogQueryEvidenceRefreshService>();
        var store = _app.Services.GetRequiredService<CatalogQueryEvidenceStore>();
        var baseline = store.Current.Collection!;
        var baselineCollections = new SearchCollections(_prefix, baseline);
        var buildId = Guid.NewGuid().ToString("N");
        var replacement = SearchIndexBuilder.CollectionName(
            _prefix,
            SearchIndexSchema.Entities,
            buildId
        );
        var nextCollections = new SearchCollections(_prefix, replacement);
        var entityAlias = SearchIndexBuilder.AliasName(_prefix, SearchIndexSchema.Entities);

        var exports = new Dictionary<string, string>();
        foreach (var kind in SearchIndexSchema.Collections)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{TypesenseUrl}/collections/{baselineCollections.Name(kind)}/documents/export"
            );
            request.Headers.Add("X-TYPESENSE-API-KEY", TypesenseKey);
            using var exported = await _typesenseHttp!.SendAsync(request);
            exported.EnsureSuccessStatusCode();
            exports.Add(kind, await exported.Content.ReadAsStringAsync());
        }

        const string query = "?q=unknown%20title&scope=shows&mode=add_tracks";
        var expected = Results(await GetSearch(query))
            .Select(row => (string?)row!["key"])
            .ToArray();
        expected.Should().NotBeEmpty();

        var performanceKind = SearchIndexSchema.Performances;
        var removed = false;
        try
        {
            foreach (
                var kind in SearchIndexSchema.Collections.Where(kind => kind != performanceKind)
            )
            {
                await Import(nextCollections, kind);
            }
            // A load following the new aliases cannot finish while this sibling is missing.
            await _typesense!.SetAlias(entityAlias, replacement, token);
            refreshed.RefreshNow();

            await _typesense.DeleteCollection(baselineCollections.Name(performanceKind), token);
            removed = true;
            using var request = new HttpRequestMessage(HttpMethod.Get, SearchPath + query);
            var unavailable = await ReadSearch(request);
            ((string?)unavailable["availability"]!["catalog"]).Should().Be("unavailable");

            await _evidenceClock.BackoffStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Import(nextCollections, performanceKind);
            await WaitForEvidence(replacement, query);
            Results(await GetSearch(query))
                .Select(row => (string?)row!["key"])
                .Should()
                .Equal(expected);
        }
        finally
        {
            if (removed)
            {
                await Import(baselineCollections, performanceKind);
                var performanceAlias = SearchIndexBuilder.AliasName(_prefix, performanceKind);
                await _typesense!.SetAlias(
                    performanceAlias,
                    baselineCollections.Name(performanceKind),
                    token
                );
            }
            await _typesense!.SetAlias(entityAlias, baseline, token);
            refreshed.RefreshNow();
            await WaitForEvidence(baseline, query);
            foreach (var kind in SearchIndexSchema.Collections)
            {
                await _typesense.DeleteCollection(nextCollections.Name(kind), token);
            }
        }

        async Task Import(SearchCollections generation, string kind)
        {
            var name = generation.Name(kind);
            await _typesense!.CreateCollection(SearchIndexSchema.Create(kind, name), token);
            if (exports[kind].Length > 0)
            {
                var imported = await _typesense.Import(name, exports[kind], upsert: false, token);
                imported.Rejected.Should().Be(0);
            }
        }

        async Task WaitForEvidence(string collection, string search)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (store.Current.Collection != collection)
            {
                using var response = await _http.GetAsync(SearchPath + search, deadline.Token);
                response.EnsureSuccessStatusCode();
                await Task.Delay(25, deadline.Token);
            }
        }
    }
}
