using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

[TestFixture]
public sealed class TestNormalizationRetainedDocument
{
    [TestCase("application/json; charset=iso-8859-1", false)]
    [TestCase("application/vnd.relisten+json; charset=utf-16", false)]
    [TestCase("application/json", true)]
    public void JsonUsesRetainedCharsetOrBomAndPreservesPointersAndUnicodeSpans(
        string mediaType,
        bool bom
    )
    {
        var encoding = mediaType.Contains("8859") ? Encoding.Latin1 : Encoding.Unicode;
        const string json = "{\"a/b\":{\"~names\":[\"Zoë & Fran&#231;ois\"]}}";
        var input = Input(json, mediaType, encoding, bom);
        using var document = NormalizationRetainedDocument.Open(input);
        var evidence = new NormalizedEvidenceReference(input.Uuid, "/a~1b/~0names/0", 0, 3, "Zoë");

        document.ValidateEvidence(evidence);
        document.Resolve(evidence.JsonPointer).GetString().Should().Be("Zoë & Fran&#231;ois");
        document.Resolve("/a~1b/~0names").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [TestCase("text/html; charset=iso-8859-1", false)]
    [TestCase("text/html; charset=utf-16", false)]
    [TestCase("text/plain", true)]
    public void TextUsesWholeDocumentLocatorBeforeHtmlOrEntityNormalization(
        string mediaType,
        bool bom
    )
    {
        var encoding = mediaType.Contains("8859") ? Encoding.Latin1 : Encoding.Unicode;
        const string html = "<p>Zoë &amp; François</p>";
        var input = Input(html, mediaType, encoding, bom);
        using var document = NormalizationRetainedDocument.Open(input);
        var quote = "Zoë &amp; François";
        document.ValidateEvidence(new(input.Uuid, "", 3, 3 + quote.Length, quote));
        document.Resolve("").GetString().Should().Be(html);

        var renderedQuote = () =>
            document.ValidateEvidence(new(input.Uuid, "", 3, 3 + quote.Length, "Zoë & François"));
        renderedQuote.Should().Throw<ArgumentException>();
        var domPointer = () => document.Resolve("/p");
        domPointer.Should().Throw<ArgumentException>();
    }

    [Test]
    public async Task RealRetainedPhishNetHtmlQuotesOriginalSource()
    {
        var bytes = await File.ReadAllBytesAsync(
            Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "Fixtures",
                "PhishNet",
                "setlist-1992-11-21.html"
            )
        );
        var input = new NormalizationInputRevision
        {
            Uuid = Guid.NewGuid(),
            Payload = bytes,
            MediaType = "text/html; charset=utf-8",
        };
        using var document = NormalizationRetainedDocument.Open(input);
        var raw = Encoding.UTF8.GetString(bytes);
        const string quote = "The Landlady</a> >";
        var start = raw[..raw.IndexOf(quote, StringComparison.Ordinal)].EnumerateRunes().Count();

        document.ValidateEvidence(new(input.Uuid, "", start, start + quote.Length, quote));
        document.Resolve("").GetString().Should().Be(raw);
    }

    [Test]
    public void SpansCountUnicodeCodePointsAndRejectWrongRevisionOrIncompleteSpans()
    {
        var input = Input("before 🎤 Pat", "text/plain", Encoding.UTF8);
        using var document = NormalizationRetainedDocument.Open(input);
        var evidence = new NormalizedEvidenceReference(input.Uuid, "", 7, 8, "🎤");
        document.ValidateEvidence(evidence);

        foreach (
            var invalid in new[]
            {
                evidence with
                {
                    InputRevisionUuid = Guid.NewGuid(),
                },
                evidence with
                {
                    StartCodePoint = -1,
                },
                evidence with
                {
                    EndCodePoint = 99,
                },
                evidence with
                {
                    EndCodePoint = 6,
                },
                evidence with
                {
                    EndCodePoint = 9,
                },
                evidence with
                {
                    StartCodePoint = null,
                },
                evidence with
                {
                    Quote = null,
                },
            }
        )
        {
            var validate = () => document.ValidateEvidence(invalid);
            validate.Should().Throw<ArgumentException>();
        }
    }

    [TestCase("/names/01")]
    [TestCase("/names/+0")]
    [TestCase("/names/-")]
    [TestCase("/names/1")]
    [TestCase("/names/0~2")]
    [TestCase("/names/0~")]
    [TestCase("names/0")]
    public void InvalidJsonPointersCannotSelectAnotherValue(string pointer)
    {
        var input = Input("{\"names\":[\"Pat\"]}", "application/json", Encoding.UTF8);
        using var document = NormalizationRetainedDocument.Open(input);
        var resolve = () => document.Resolve(pointer);
        resolve.Should().Throw<ArgumentException>();
    }

    [TestCase("application/octet-stream", "private provider content")]
    [TestCase("application/xml", "<private>provider content</private>")]
    [TestCase("application/json", "private provider content")]
    [TestCase("text/html; charset=unknown-provider-encoding", "private provider content")]
    public void UnsupportedOrMalformedDocumentsFailWithoutProviderContent(
        string mediaType,
        string raw
    )
    {
        var input = Input(raw, mediaType, Encoding.UTF8);
        var open = () => NormalizationRetainedDocument.Open(input);
        var error = open.Should().Throw<ArgumentException>();
        error.Which.Message.Should().NotContain("private").And.NotContain("provider content");
    }

    [TestCase("utf-8")]
    [TestCase("utf-16")]
    public void MalformedEncodingCannotCreateReplacementCharactersInEvidence(string charset)
    {
        var bytes =
            charset == "utf-8"
                ? new byte[]
                {
                    (byte)'{',
                    (byte)'"',
                    (byte)'x',
                    (byte)'"',
                    (byte)':',
                    (byte)'"',
                    0xFF,
                    (byte)'"',
                    (byte)'}',
                }
                : Encoding.Unicode.GetBytes("{\"x\":\"Pat\"}")[..^1];
        foreach (var mediaType in new[] { "application/json", "text/html" })
        {
            var input = new NormalizationInputRevision
            {
                Uuid = Guid.NewGuid(),
                Payload = bytes,
                MediaType = $"{mediaType}; charset={charset}",
            };
            var open = () => NormalizationRetainedDocument.Open(input);
            open.Should().Throw<ArgumentException>().Which.Message.Should().NotContain("Pat");
        }
    }

    private static NormalizationInputRevision Input(
        string raw,
        string mediaType,
        Encoding encoding,
        bool bom = false
    ) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Payload = bom
                ? [.. encoding.GetPreamble(), .. encoding.GetBytes(raw)]
                : encoding.GetBytes(raw),
            MediaType = mediaType,
        };
}
