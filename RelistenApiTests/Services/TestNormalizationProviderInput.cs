using System.Text;
using System.Transactions;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>Exercises the prefetch generation and durable retention contract against PostgreSQL.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationProviderInput
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationProviderInputService provider = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new NormalizationDomainFixture();
        evidence = new(fixture.Db);
        provider = Service(enabled: true);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task ExactProviderBytesSurviveLaterCatalogFailure()
    {
        var resource = Resource();
        var payload = await File.ReadAllBytesAsync(
            Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "Fixtures",
                "PhishNet",
                "setlist-1992-11-21.html"
            )
        );
        var before = DateTime.UtcNow;
        var calls = 0;
        var value = new object();
        var import = async () =>
        {
            var fetched = await provider.FetchAsync(
                resource,
                "raw-http:1",
                ct =>
                {
                    calls++;
                    return Task.FromResult(
                        new NormalizationProviderResponse<object>(
                            value,
                            payload,
                            "text/html",
                            "etag:fixture"
                        )
                    );
                }
            );
            fetched.Should().BeSameAs(value);
            throw new InvalidOperationException("Later catalog projection failed.");
        };

        await import
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("Later catalog projection failed.");
        calls.Should().Be(1);
        var current = await evidence.GetCurrentInputAsync(resource);
        current!.Revision.Payload.Should().Equal(payload);
        current.Revision.MediaType.Should().Be("text/html");
        current.Revision.CaptureVersion.Should().Be("raw-http:1");
        var receipt = (await evidence.GetFetchHistoryAsync(resource)).Single();
        receipt.ProviderRevision.Should().Be("etag:fixture");
        receipt.ObservedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
        receipt.FetchUuid.Should().NotBeEmpty();
    }

    [Test]
    public async Task NewerFetchDuringCallbackRejectsOlderResultButRetainsItsBytes()
    {
        var resource = Resource();
        var calls = 0;
        var older = () =>
            provider.FetchAsync(
                resource,
                "raw-http:1",
                async ct =>
                {
                    calls++;
                    await provider.FetchAsync(
                        resource,
                        "raw-http:1",
                        _ => Task.FromResult(Response("newer")),
                        ct
                    );
                    return Response("older");
                }
            );

        var rejected = (await older.Should().ThrowAsync<NormalizationStaleInputException>()).Which;
        calls.Should().Be(1);
        rejected.Resource.Should().Be(resource);
        rejected.Capture.Fetch.AcceptedAsCurrent.Should().BeFalse();
        rejected.Capture.Fetch.ExpectedGeneration.Should().Be(0);
        (await evidence.GetInputAsync(rejected.Capture.Revision.Uuid))!
            .Payload.Should()
            .Equal(Encoding.UTF8.GetBytes("older"));
        (await evidence.GetCurrentInputAsync(resource))!
            .Revision.Payload.Should()
            .Equal(Encoding.UTF8.GetBytes("newer"));
        (await evidence.GetFetchHistoryAsync(resource)).Should().HaveCount(2);

        // Recovery performs a new HTTP request after reading the current generation.
        (
            await provider.FetchAsync(
                resource,
                "raw-http:1",
                _ => Task.FromResult(Response("fresh"))
            )
        )
            .Should()
            .Be("fresh");
        (await evidence.GetCurrentInputAsync(resource))!.Generation.Should().Be(2);
    }

    [Test]
    public async Task EqualResponsesAdvanceObservationGenerationWithoutDuplicatingRevision()
    {
        var resource = Resource();
        await provider.FetchAsync(resource, "raw-http:1", _ => Task.FromResult(Response("same")));
        var first = await evidence.GetCurrentInputAsync(resource);
        await provider.FetchAsync(resource, "raw-http:1", _ => Task.FromResult(Response("same")));

        var current = await evidence.GetCurrentInputAsync(resource);
        current!.Generation.Should().Be(2);
        current.Revision.Uuid.Should().Be(first!.Revision.Uuid);
        var history = await evidence.GetFetchHistoryAsync(resource);
        history.Select(f => f.ExpectedGeneration).Should().Equal(0, 1);
        history.Select(f => f.FetchUuid).Should().OnlyHaveUniqueItems();
    }

    [Test]
    public async Task FailedFetchDoesNotRetainAnInput()
    {
        var resource = Resource();
        var calls = 0;
        var fetch = () =>
            provider.FetchAsync<string>(
                resource,
                "raw-http:1",
                _ =>
                {
                    calls++;
                    throw new HttpRequestException("Provider unavailable.");
                }
            );

        await fetch.Should().ThrowAsync<HttpRequestException>();
        calls.Should().Be(1);
        (await evidence.GetCurrentInputAsync(resource)).Should().BeNull();
        (await evidence.GetFetchHistoryAsync(resource)).Should().BeEmpty();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task AlreadyCancelledCallDoesNotStartTheFetch(bool enabled)
    {
        var resource = Resource();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;
        var fetch = () =>
            Service(enabled)
                .FetchAsync(
                    resource,
                    "raw-http:1",
                    _ =>
                    {
                        calls++;
                        return Task.FromResult(Response("cancelled"));
                    },
                    cancellation.Token
                );

        await fetch.Should().ThrowAsync<OperationCanceledException>();
        calls.Should().Be(0);
        (await evidence.GetFetchHistoryAsync(resource)).Should().BeEmpty();
    }

    [Test]
    public async Task CancellationAfterFetchPreventsValueReturnAndEvidencePublication()
    {
        var resource = Resource();
        using var cancellation = new CancellationTokenSource();
        var fetch = () =>
            provider.FetchAsync(
                resource,
                "raw-http:1",
                ct =>
                {
                    ct.Should().Be(cancellation.Token);
                    cancellation.Cancel();
                    return Task.FromResult(Response("cancelled"));
                },
                cancellation.Token
            );

        await fetch.Should().ThrowAsync<OperationCanceledException>();
        (await evidence.GetCurrentInputAsync(resource)).Should().BeNull();
        (await evidence.GetFetchHistoryAsync(resource)).Should().BeEmpty();
    }

    [Test]
    public async Task AmbientCatalogTransactionIsRejectedBeforeFetching()
    {
        var resource = Resource();
        var calls = 0;
        using (var transaction = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            var fetch = () =>
                provider.FetchAsync(
                    resource,
                    "raw-http:1",
                    _ =>
                    {
                        calls++;
                        return Task.FromResult(Response("must not fetch"));
                    }
                );

            await fetch
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("*outside the catalog transaction*");
        }

        calls.Should().Be(0);
        (await evidence.GetCurrentInputAsync(resource)).Should().BeNull();
        (await evidence.GetFetchHistoryAsync(resource)).Should().BeEmpty();
    }

    [Test]
    public async Task DisabledCaptureReturnsFetchedValueWithoutCreatingEvidence()
    {
        var resource = Resource();
        var calls = 0;
        var result = await Service(enabled: false)
            .FetchAsync(
                resource,
                "raw-http:1",
                _ =>
                {
                    calls++;
                    return Task.FromResult(Response("ordinary importer result"));
                }
            );

        result.Should().Be("ordinary importer result");
        calls.Should().Be(1);
        (await evidence.GetCurrentInputAsync(resource)).Should().BeNull();
        (await evidence.GetFetchHistoryAsync(resource)).Should().BeEmpty();
    }

    private NormalizationProviderInputService Service(bool enabled) =>
        new(
            evidence,
            Options.Create(new NormalizationProviderCaptureOptions { Enabled = enabled })
        );

    private static NormalizationResourceKey Resource() =>
        new("phish.net", "setlist", "capture-test-" + Guid.NewGuid());

    private static NormalizationProviderResponse<string> Response(string text) =>
        new(text, Encoding.UTF8.GetBytes(text), "text/plain");
}
