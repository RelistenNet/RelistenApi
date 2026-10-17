using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

/// <summary>
/// Exercises selected extraction claims from retained cross-artist evidence. The
/// development labels do not describe complete parses or certify global identity.
/// Evaluation-reserve cases remain outside this regression suite.
/// </summary>
[TestFixture]
public class TestNormalizationDevelopmentCorpus
{
    private static readonly string CorpusDirectory = Path.Combine(
        TestContext.CurrentContext.TestDirectory,
        "Fixtures",
        "normalization"
    );

    private static readonly IReadOnlyDictionary<string, JsonElement> Evidence = ReadJsonLines(
            "evidence.jsonl"
        )
        .ToDictionary(record => record.GetProperty("id").GetString()!);

    public static IEnumerable<TestCaseData> PeopleCases => Cases("people");

    public static IEnumerable<TestCaseData> TitleCases => Cases("songs");

    [TestCaseSource(nameof(PeopleCases))]
    public void Recovers_selected_credit_mentions_from_real_metadata(JsonElement corpusCase)
    {
        var fields = InputFields(corpusCase);
        var result = new RecordingCreditExtractor().Extract(fields);
        var expected = corpusCase.GetProperty("expected");

        AssertEvidence(fields, result.Credits.Select(credit => credit.Evidence));
        AssertEvidence(fields, result.Unresolved.Select(item => item.Evidence));

        if (expected.GetProperty("status").GetString() == "unresolved")
        {
            // Empty/null credit fields and explicit unknown names supply no person.
            // The extraction contract reports mentions, not an identity decision.
            result.Credits.Should().BeEmpty();
            return;
        }

        var remaining = result.Credits.ToList();
        foreach (
            var credit in expected.GetProperty("assertions").GetProperty("credits").EnumerateArray()
        )
        {
            var name = Comparable(credit.GetProperty("name").GetString()!);
            var role = credit.GetProperty("role").GetString();
            var matching = remaining.FindIndex(actual =>
                Comparable(actual.Name) == name && Token(actual.Role) == role
            );

            matching
                .Should()
                .BeGreaterThanOrEqualTo(0, $"the metadata explicitly credits {name} as {role}");
            remaining.RemoveAt(matching);
        }
    }

    [TestCaseSource(nameof(TitleCases))]
    public void Recovers_selected_title_contents_without_inventing_alignment(JsonElement corpusCase)
    {
        var fields = InputFields(corpusCase);
        var title = fields["title"]!;
        // The evidence export has numeric artist IDs, not artist UUIDs. This caller
        // scope is supplied only to invoke extraction; it asserts no canonical ID.
        var result = new TrackTitleExtractor().Extract(Guid.Empty, title);
        var assertions = corpusCase.GetProperty("expected").GetProperty("assertions");

        AssertEvidence(fields, result.Segments.Select(segment => segment.Evidence));
        AssertEvidence(fields, result.Qualifiers.Select(qualifier => qualifier.Evidence));
        result.OriginalTitle.Should().Be(title);

        if (assertions.TryGetProperty("segments", out var expectedSegments))
        {
            var ordered =
                assertions.TryGetProperty("sequence", out var sequence)
                && sequence.GetString() == "title_order";
            var remaining = result.Segments.ToList();
            foreach (var segment in expectedSegments.EnumerateArray())
            {
                var matching = remaining.FindIndex(actual =>
                    Token(actual.Kind) == segment.GetProperty("kind").GetString()
                    && Comparable(actual.Title)
                        == Comparable(segment.GetProperty("title").GetString())
                );

                matching
                    .Should()
                    .BeGreaterThanOrEqualTo(0, "each selected content mention must be recovered");
                remaining.RemoveRange(ordered ? 0 : matching, ordered ? matching + 1 : 1);
            }
        }

        if (assertions.TryGetProperty("title", out var expectedTitle))
        {
            result
                .Segments.Should()
                .Contain(segment =>
                    Comparable(segment.Title) == Comparable(expectedTitle.GetString())
                );
        }

        if (assertions.TryGetProperty("sequence", out var expectedSequence))
        {
            Token(result.Sequence).Should().Be(expectedSequence.GetString());
        }

        if (assertions.TryGetProperty("reason", out var expectedReason))
        {
            result.Reason.Should().Be(expectedReason.GetString());
        }

        if (assertions.TryGetProperty("coverage", out var coverage))
        {
            result
                .Segments.Should()
                .Contain(segment => Token(segment.Coverage) == coverage.GetString());
        }

        if (assertions.TryGetProperty("transition_after", out var transition))
        {
            Token(result.Segments.Last().TransitionAfter).Should().Be(transition.GetString());
        }

        foreach (var segment in result.Segments)
        {
            segment.Basis.Should().Be(assertions.GetProperty("basis").GetString());
            segment.OccurrenceUuid.Should().BeNull("titles supply no occurrence alignment");
            segment.StartMs.Should().BeNull("these inputs supply no measured audio boundaries");
            segment.EndMs.Should().BeNull("these inputs supply no measured audio boundaries");
        }
    }

    private static IEnumerable<TestCaseData> Cases(string domain)
    {
        foreach (var corpusCase in ReadJsonLines("cases.jsonl"))
        {
            if (
                corpusCase.GetProperty("split").GetString() == "development"
                && corpusCase.GetProperty("domain").GetString() == domain
                && corpusCase.GetProperty("task").GetString() == "extract"
            )
            {
                yield return new TestCaseData(corpusCase).SetName(
                    $"DevelopmentCorpus_{corpusCase.GetProperty("id").GetString()}"
                );
            }
        }
    }

    private static Dictionary<string, string?> InputFields(JsonElement corpusCase)
    {
        return corpusCase
            .GetProperty("inputs")
            .EnumerateArray()
            .ToDictionary(
                input => input.GetProperty("field").GetString()!,
                input =>
                    Evidence[input.GetProperty("evidence_id").GetString()!]
                        .GetProperty("fields")
                        .GetProperty(input.GetProperty("field").GetString()!)
                        .GetString()
            );
    }

    private static IEnumerable<JsonElement> ReadJsonLines(string name)
    {
        foreach (var line in File.ReadLines(Path.Combine(CorpusDirectory, name)))
        {
            using var document = JsonDocument.Parse(line);
            yield return document.RootElement.Clone();
        }
    }

    private static void AssertEvidence(
        IReadOnlyDictionary<string, string?> fields,
        IEnumerable<NormalizationTextEvidence> citations
    )
    {
        foreach (var citation in citations)
        {
            var raw = fields[citation.Field]!;
            var runes = raw.EnumerateRunes().ToArray();
            citation.StartCodePoint.Should().BeGreaterThanOrEqualTo(0);
            citation.EndCodePoint.Should().BeInRange(citation.StartCodePoint + 1, runes.Length);
            var quote = string.Concat(
                runes[citation.StartCodePoint..citation.EndCodePoint]
                    .Select(rune => rune.ToString())
            );
            citation.Quote.Should().Be(quote, "evidence must cite the exact retained input");
            citation
                .JsonPointer.Should()
                .Be("/" + citation.Field.Replace("~", "~0").Replace("/", "~1"));
        }
    }

    private static string? Comparable(string? value) =>
        value?.Trim().Normalize(NormalizationForm.FormC);

    private static string Token<T>(T value)
        where T : Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
