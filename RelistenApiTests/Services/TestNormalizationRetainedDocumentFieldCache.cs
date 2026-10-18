using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

/// <summary>
/// Repeated citations must retain exact code-point validation without allocating the
/// whole retained field. Each document keeps its own retained values during concurrent reads.
/// </summary>
[TestFixture]
public sealed class TestNormalizationRetainedDocumentFieldCache
{
    [TestCase(false)]
    [TestCase(true)]
    [NonParallelizable]
    public void RepeatedSmallQuotesDoNotAllocateTheWholeBackingField(bool json)
    {
        var field = new string('x', 50_000) + "🎤z" + new string('x', 50_000);
        var input = TextInput(field, json);
        using var document = NormalizationRetainedDocument.Open(input);
        var evidence = new NormalizedEvidenceReference(
            input.Uuid,
            json ? "/text" : "",
            50_000,
            50_001,
            "🎤"
        );
        var equivalent = evidence with
        {
            JsonPointer = json ? new string("/text".ToCharArray()) : "",
        };
        for (var iteration = 0; iteration < 64; iteration++)
        {
            document.ValidateEvidence(iteration % 2 == 0 ? evidence : equivalent);
        }

        // Measure synchronously on this thread. A 100002-element Rune[] alone costs over
        // 100 MB across 256 calls. Eight MiB leaves ample room for quote/pointer objects and
        // runtime bookkeeping while detecting repeated whole-field allocation.
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 256; iteration++)
        {
            document.ValidateEvidence(iteration % 2 == 0 ? evidence : equivalent);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().BeLessThan(8L * 1024 * 1024);
    }

    [Test]
    public void AlternatingFieldsKeepAstralQuotesAndEmptyEndSpansExact()
    {
        var input = JsonInput(
            new
            {
                first = "a🎤z",
                second = "🌙β",
                empty = "",
                number = 7,
            }
        );
        using var document = NormalizationRetainedDocument.Open(input);
        var first = new NormalizedEvidenceReference(input.Uuid, "/first", 1, 2, "🎤");
        var second = new NormalizedEvidenceReference(input.Uuid, "/second", 0, 1, "🌙");
        for (var iteration = 0; iteration < 8; iteration++)
        {
            document.ValidateEvidence(first);
            document.ValidateEvidence(
                first with
                {
                    StartCodePoint = 3,
                    EndCodePoint = 3,
                    Quote = "",
                }
            );
            document.ValidateEvidence(second);
            document.ValidateEvidence(
                second with
                {
                    StartCodePoint = 2,
                    EndCodePoint = 2,
                    Quote = "",
                }
            );
            document.ValidateEvidence(new(input.Uuid, "/empty", 0, 0, ""));
        }

        document.ValidateEvidence(first);
        var quoteFromAnotherField = () => document.ValidateEvidence(second with { Quote = "a" });
        quoteFromAnotherField.Should().Throw<ArgumentException>();
        var textAtANumber = () => document.ValidateEvidence(first with { JsonPointer = "/number" });
        textAtANumber.Should().Throw<ArgumentException>();
        document.ValidateEvidence(second);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void APreviouslyValidatedFieldCannotBypassDocumentDisposal(bool json)
    {
        var input = TextInput("before 🎤 after", json);
        using var document = NormalizationRetainedDocument.Open(input);
        var evidence = new NormalizedEvidenceReference(input.Uuid, json ? "/text" : "", 7, 8, "🎤");
        document.ValidateEvidence(evidence);
        document.Dispose();

        var validateAgain = () => document.ValidateEvidence(evidence);

        validateAgain.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public void DocumentsWithTheSamePointerKeepTheirOwnRetainedValues()
    {
        var first = TextInput("A🎤", json: true);
        var second = TextInput("🌙B", json: true);
        using var firstDocument = NormalizationRetainedDocument.Open(first);
        using var secondDocument = NormalizationRetainedDocument.Open(second);
        var firstQuote = new NormalizedEvidenceReference(first.Uuid, "/text", 1, 2, "🎤");
        var secondQuote = new NormalizedEvidenceReference(second.Uuid, "/text", 1, 2, "B");

        for (var iteration = 0; iteration < 8; iteration++)
        {
            firstDocument.ValidateEvidence(firstQuote);
            secondDocument.ValidateEvidence(secondQuote);
        }
        var otherDocumentsQuote = () =>
            firstDocument.ValidateEvidence(firstQuote with { Quote = "B" });
        otherDocumentsQuote.Should().Throw<ArgumentException>();
    }

    [Test]
    public void ConcurrentReadonlyValidationKeepsEachPointersTextAndRunesTogether()
    {
        var input = JsonInput(
            new
            {
                first = "A" + new string('l', 256) + "🎤",
                second = "🌙" + new string('r', 512) + "B",
            }
        );
        using var document = NormalizationRetainedDocument.Open(input);
        var first = new NormalizedEvidenceReference(input.Uuid, "/first", 0, 1, "A");
        var second = new NormalizedEvidenceReference(input.Uuid, "/second", 0, 1, "🌙");

        Parallel.For(
            0,
            8,
            worker =>
            {
                for (var iteration = 0; iteration < 256; iteration++)
                {
                    // Opposite access order increases cache replacement overlap without making
                    // correctness depend on a scheduling delay or a minimum execution time.
                    document.ValidateEvidence(worker % 2 == 0 ? first : second);
                    document.ValidateEvidence(worker % 2 == 0 ? second : first);
                }
            }
        );
    }

    private static NormalizationInputRevision TextInput(string field, bool json) =>
        json
            ? JsonInput(new { text = field })
            : new()
            {
                Uuid = Guid.NewGuid(),
                Payload = Encoding.UTF8.GetBytes(field),
                MediaType = "text/plain; charset=utf-8",
            };

    private static NormalizationInputRevision JsonInput<T>(T value) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Payload = JsonSerializer.SerializeToUtf8Bytes(value),
            MediaType = "application/json; charset=utf-8",
        };
}
