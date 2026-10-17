using System.Text;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>Bounded readers fail as a whole and preserve exact historical revision identities.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationBoundedEvidence
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;

    [SetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
    }

    [TearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task HistoricalAndCurrentInputsKeepExactReferencesAndDeduplicateSharedRoot()
    {
        var old = await Capture("old");
        var current = await Capture("new", old.Resource, 1);
        var sibling = await Capture("sibling");
        var heads = await evidence.GetCurrentInputReferencesAsync([
            old.Resource,
            sibling.Resource,
            old.Resource,
        ]);
        heads.Should().Equal(Reference(current), Reference(sibling));
        var historical = await evidence.GetInputsAsync([
            Reference(old),
            Reference(current),
            Reference(old),
        ]);
        historical.Select(input => input.Uuid).Should().Equal(old.Uuid, current.Uuid);
        historical
            .Select(input => Encoding.UTF8.GetString(input.Payload))
            .Should()
            .Equal("old", "new");
    }

    [Test]
    public async Task ByteLimitsCountUniqueInputsAndFailWithoutPartialResult()
    {
        var first = await Capture("1234");
        var second = await Capture("5678");
        var exact = await evidence.GetInputsAsync(
            [Reference(first), Reference(first)],
            new(1, 4, 4)
        );
        exact.Should().ContainSingle().Which.Payload.Should().Equal(first.Payload);
        await FluentActions
            .Invoking(() => evidence.GetInputsAsync([Reference(first)], new(1, 3, 10)))
            .Should()
            .ThrowAsync<InvalidOperationException>();
        await FluentActions
            .Invoking(() =>
                evidence.GetInputsAsync([Reference(first), Reference(second)], new(2, 4, 7))
            )
            .Should()
            .ThrowAsync<InvalidOperationException>();
        await FluentActions
            .Invoking(() =>
                evidence.GetInputsAsync([Reference(first), Reference(second)], new(1, 4, 8))
            )
            .Should()
            .ThrowAsync<InvalidOperationException>();
        (await evidence.GetInputsAsync([Reference(first), Reference(second)], new(2, 4, 8)))
            .Should()
            .HaveCount(2);
    }

    [Test]
    public async Task MissingAndCrossResourceReferencesAreRejectedAsAWhole()
    {
        var first = await Capture("first");
        var other = await Capture("other");
        await FluentActions
            .Invoking(() =>
                evidence.GetInputsAsync([Reference(first), new(Guid.NewGuid(), first.Resource)])
            )
            .Should()
            .ThrowAsync<ArgumentException>();
        await FluentActions
            .Invoking(() => evidence.GetInputsAsync([new(first.Uuid, other.Resource)]))
            .Should()
            .ThrowAsync<ArgumentException>();
        await FluentActions
            .Invoking(() =>
                evidence.GetInputsAsync([Reference(first), new(first.Uuid, other.Resource)])
            )
            .Should()
            .ThrowAsync<ArgumentException>();
        await FluentActions
            .Invoking(() =>
                evidence.GetCurrentInputReferencesAsync([
                    first.Resource,
                    first.Resource with
                    {
                        ProviderKey = Guid.NewGuid().ToString(),
                    },
                ])
            )
            .Should()
            .ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task CancelledReadsRemainRetryable()
    {
        var input = await Capture("payload");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await FluentActions
            .Invoking(() =>
                evidence.GetInputsAsync([Reference(input)], cancellationToken: cancelled.Token)
            )
            .Should()
            .ThrowAsync<OperationCanceledException>();
        await FluentActions
            .Invoking(() =>
                evidence.GetCurrentInputReferencesAsync([input.Resource], cancelled.Token)
            )
            .Should()
            .ThrowAsync<OperationCanceledException>();
        (await evidence.GetInputsAsync([Reference(input)])).Should().ContainSingle();
        (await evidence.GetCurrentInputReferencesAsync([input.Resource]))
            .Should()
            .Equal(Reference(input));
    }

    private async Task<NormalizationInputRevision> Capture(
        string payload,
        NormalizationResourceKey? resource = null,
        long generation = 0
    )
    {
        var result = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource ?? new("bounded-fixture", "recording", Guid.NewGuid().ToString()),
                Encoding.UTF8.GetBytes(payload),
                "text/plain",
                "bounded-v1",
                generation,
                DateTimeOffset.UtcNow
            )
        );
        return result.Revision;
    }

    private static NormalizationBindingInputReference Reference(NormalizationInputRevision input) =>
        new(input.Uuid, input.Resource);
}
