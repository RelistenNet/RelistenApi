using System.Text;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

/// <summary>Even an empty field list must authenticate the standalone retained input.</summary>
[TestFixture]
public sealed class TestSourceActorEmptyFieldValidation
{
    [TestCase("application/json", "{broken")]
    [TestCase("application/octet-stream", "{}")]
    [TestCase("not a media type", "{}")]
    [TestCase("text/plain; charset=not-a-real-charset", "text")]
    public void EmptyFieldsDoNotBypassRetainedInputValidation(string mediaType, string payload)
    {
        var input = Input(mediaType, payload);
        Action extract = () => new RetainedSourceActorAliasExtractor().Extract(input, []);

        extract.Should().Throw<ArgumentException>();
    }

    [Test]
    public void EmptyFieldsStillObserveStandaloneCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Action extract = () =>
            new RetainedSourceActorAliasExtractor().Extract(
                Input("application/json", "{}"),
                [],
                cancellation.Token
            );

        extract.Should().Throw<OperationCanceledException>();
    }

    [Test]
    public void EmptyValidInputProducesNoScopesOrInferredClear()
    {
        var result = new RetainedSourceActorAliasExtractor().Extract(
            Input("application/json", "{}"),
            []
        );

        result.Assertions.Should().BeEmpty();
        result.Unresolved.Should().BeEmpty();
        result.Scopes.Should().BeEmpty();
    }

    private static NormalizationInputRevision Input(string mediaType, string payload) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Resource = new("archive.org", "item_metadata", "empty-alias-scope"),
            Payload = Encoding.UTF8.GetBytes(payload),
            MediaType = mediaType,
            CaptureVersion = "archive-item-response-v1",
        };
}
