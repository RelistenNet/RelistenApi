using System.Text.Json.Nodes;
using FluentAssertions;
using Relisten.Normalization.Songs;
using Relisten.Normalization.Temporal;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Relisten.NormalizationTests;

public class TestCatalogueAnswers
{
    private static CatalogueRequest Request() =>
        new()
        {
            Data = "{}",
            SongIds = new Dictionary<string, Guid> { ["s1"] = Guid.NewGuid() },
            SongNames = new Dictionary<string, string> { ["s1"] = "Sugaree" },
            KeyTexts = new Dictionary<string, string> { ["k1"] = "Sugaree", ["k2"] = "Tuning" },
            KeyIds = new Dictionary<string, string> { ["k1"] = "sugaree", ["k2"] = "tuning" },
        };

    [TestCase(
        """{"new_songs": [], "keys": [{"id": "k1", "songs": ["s9"], "not_a_song": null, "confidence": 0.95, "rationale": ""}]}"""
    )]
    [TestCase(
        """{"new_songs": [], "keys": [{"id": "k7", "songs": ["s1"], "not_a_song": null, "confidence": 0.95, "rationale": ""}]}"""
    )]
    [TestCase(
        """{"new_songs": [], "keys": [{"id": "k1", "songs": ["s1"], "not_a_song": null, "confidence": 0.95, "rationale": ""}, {"id": "k1", "songs": ["s1"], "not_a_song": null, "confidence": 0.95, "rationale": ""}]}"""
    )]
    [TestCase(
        """{"new_songs": [], "keys": [{"id": "k1", "songs": ["s1"], "not_a_song": "crowd", "confidence": 0.95, "rationale": ""}]}"""
    )]
    [TestCase(
        """{"new_songs": [], "keys": [{"id": "k1", "songs": [], "not_a_song": null, "confidence": 0.95, "rationale": ""}]}"""
    )]
    [TestCase(
        """{"new_songs": [], "keys": [{"id": "k1", "songs": ["s1"], "song_names": ["Crazy Fingers"], "not_a_song": null, "confidence": 0.99, "rationale": ""}]}"""
    )]
    [TestCase(
        """{"new_songs": [], "keys": [{"id": "k1", "key_text": "Tuning", "songs": ["s1"], "song_names": ["Sugaree"], "not_a_song": null, "confidence": 0.99, "rationale": ""}]}"""
    )]
    [TestCase(
        """{"new_songs": [], "keys": [{"id": "k1", "key_text": "Sugare", "songs": ["s1"], "song_names": ["Sugaree"], "not_a_song": null, "confidence": 0.99, "rationale": ""}]}"""
    )]
    [TestCase(
        """{"new_songs": [], "keys": [{"id": "k1", "key_text": "Sugaree", "songs": ["s1"], "song_names": ["Shugaree"], "not_a_song": null, "confidence": 0.99, "rationale": ""}]}"""
    )]
    public void AKeyWhoseAnswerBreaksTheRulesStaysUnresolved(string json)
    {
        var answer = CatalogueRequest.Parse(json, Request());

        answer.Keys.Should().NotContainKey("sugaree");
        answer.Rejected.Should().Be(2);
    }

    [TestCase("Not Fade Away", "not fade away sbb 1972", "not fade away")]
    [TestCase("", "tuning", "tuning")]
    [TestCase("Dark Star", "not fade away", "not fade away")] // not in the title: the key
    public void ASongTextMustBeWordsOfItsTitle(string songText, string key, string expected) =>
        CatalogueLlm.SpelledName(songText, key).Should().Be(expected);

    [Test]
    public void ARejectedAnswerStillLeavesItsRecurringTitlesForReview()
    {
        var keys = new Dictionary<string, CatalogueLlm.KeyInfo>
        {
            ["sugaree"] = new(
                new Relisten.Normalization.Titles.KeyUsage("sugaree", 5, 5, []),
                "Sugaree",
                [],
                []
            ),
            ["tuning"] = new(
                new Relisten.Normalization.Titles.KeyUsage("tuning", 1, 1, []),
                "Tuning",
                [],
                []
            ),
        };

        CatalogueLlm
            .Review(
                new CatalogueAnswer([], new Dictionary<string, KeyAnswer>(), 2),
                Request(),
                keys
            )
            .Select(r => r.Key)
            .Should()
            .Equal("sugaree");
    }

    [TestCase("not json")]
    [TestCase("""{"keys": []}""")]
    public void AnAnswerOfTheWrongShapeThrows(string json) =>
        FluentActions
            .Invoking(() => CatalogueRequest.Parse(json, Request()))
            .Should()
            .Throw<CatalogueAnswerException>();

    [Test]
    public void AValidAnswerParses()
    {
        var answer = CatalogueRequest.Parse(
            """{"new_songs": [{"id": "n1", "name": "Tuning Jam", "kind": "jam", "original_artist": null}], "keys": [{"id": "k1", "key_text": "Sugaree", "songs": ["s1"], "song_names": ["Sugaree"], "not_a_song": null, "confidence": 0.97, "rationale": "x"}, {"id": "k2", "key_text": "Tuning", "songs": [], "song_names": [], "not_a_song": "tuning", "confidence": 0.99, "rationale": "y"}]}""",
            Request()
        );

        answer.Keys["sugaree"].SongRefs.Should().Equal("s1");
        answer.Keys["tuning"].SongRefs.Should().BeEmpty();
    }
}

/// The workflow loops until a step reports nothing left, carrying unresolved keys forward,
/// then runs the deterministic match.
[TestFixture, NonParallelizable]
public class TestKbCatalogueWorkflow
{
    private WorkflowEnvironment environment = null!;

    [OneTimeSetUp]
    public async Task StartServer() => environment = await WorkflowEnvironment.StartLocalAsync();

    [OneTimeTearDown]
    public async Task StopServer() => await environment.DisposeAsync();

    public class Steps
    {
        public List<int> SkipSizes { get; } = [];
        public List<string> Forgotten { get; } = [];
        public List<int?> OnlySizes { get; } = [];
        public int Matches { get; private set; }
        public int Attempts { get; private set; }

        /// The first chunk fails after (say) its model call.
        public bool FailFirst { get; init; }

        [Activity("Kb.CatalogueChunk")]
        public CatalogueChunkResult CatalogueChunk(KbCatalogueChunkInput input)
        {
            Attempts++;
            if (FailFirst && Attempts == 1)
            {
                throw new ApplicationFailureException("database went away", nonRetryable: false);
            }

            SkipSizes.Add(input.Skip.Count);
            OnlySizes.Add(input.Only?.Count);
            return SkipSizes.Count <= 2
                ? new CatalogueChunkResult(
                    false,
                    50,
                    40,
                    [$"key{SkipSizes.Count}"],
                    [$"key{SkipSizes.Count}", $"sure{SkipSizes.Count}"],
                    [new ReviewItem($"key{SkipSizes.Count}", "Key", 4)],
                    3,
                    100,
                    1000,
                    200
                )
                : new CatalogueChunkResult(true, 0, 0, [], [], [], 0, 0, 0, 0);
        }

        [Activity("Kb.ForgetDecisions")]
        public int ForgetDecisions(KbForgetInput input)
        {
            Forgotten.AddRange(input.Spellings);
            return 1;
        }

        [Activity("Kb.MatchTracks")]
        public CatalogueMatchResult MatchTracks(Guid artist)
        {
            Matches++;
            return new CatalogueMatchResult(7, 0);
        }
    }

    [Test]
    public async Task LoopsUntilDoneThenMatches()
    {
        var steps = new Steps();
        using var workflows = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(
                NormalizationQueues.Workflows
            ).AddWorkflow<KbCatalogueWorkflow>()
        );
        using var database = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(NormalizationQueues.Database).AddAllActivities(steps)
        );

        var result = await workflows.ExecuteAsync(() =>
            database.ExecuteAsync(() =>
                environment.Client.ExecuteWorkflowAsync(
                    (KbCatalogueWorkflow w) => w.RunAsync(new BuildKbCatalogue(Guid.NewGuid())),
                    new WorkflowOptions("test/" + Guid.NewGuid(), NormalizationQueues.Workflows)
                )
            )
        );

        // First pass: two chunks, then done; review pass: done at once, with a fresh skip list.
        steps.SkipSizes.Should().Equal(0, 2, 4, 0);
        steps.Matches.Should().Be(1);
        result
            .Should()
            .BeEquivalentTo(
                new KbCatalogueResult(2, 6, 80, 2, 207, 2000, 400),
                o => o.Excluding(r => r.TracksPerChunk).Excluding(r => r.StillUnsure)
            );
        result.TracksPerChunk.Should().Equal(100, 100);
        result.StillUnsure.Select(r => r.Key).Should().BeEquivalentTo("key1", "key2");
    }

    [Test]
    public async Task AReaskForgetsThenAsksOnlyThoseTitles()
    {
        var steps = new Steps();
        using var workflows = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(
                NormalizationQueues.Workflows
            ).AddWorkflow<KbCatalogueWorkflow>()
        );
        using var database = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(NormalizationQueues.Database).AddAllActivities(steps)
        );

        await workflows.ExecuteAsync(() =>
            database.ExecuteAsync(() =>
                environment.Client.ExecuteWorkflowAsync(
                    (KbCatalogueWorkflow w) =>
                        w.RunAsync(
                            new BuildKbCatalogue(
                                Guid.NewGuid(),
                                null,
                                new[] { "Let Me Lie", "Mellow Mood" }
                            )
                        ),
                    new WorkflowOptions("test/" + Guid.NewGuid(), NormalizationQueues.Workflows)
                )
            )
        );

        steps.Forgotten.Should().Equal("Let Me Lie", "Mellow Mood");
        steps.OnlySizes.Should().AllBeEquivalentTo(2);
    }

    [Test]
    public async Task TheChunkCapCoversTheReviewPassToo()
    {
        var steps = new Steps();
        using var workflows = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(
                NormalizationQueues.Workflows
            ).AddWorkflow<KbCatalogueWorkflow>()
        );
        using var database = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(NormalizationQueues.Database).AddAllActivities(steps)
        );

        var result = await workflows.ExecuteAsync(() =>
            database.ExecuteAsync(() =>
                environment.Client.ExecuteWorkflowAsync(
                    (KbCatalogueWorkflow w) =>
                        w.RunAsync(new BuildKbCatalogue(Guid.NewGuid(), 2, null)),
                    new WorkflowOptions("test/" + Guid.NewGuid(), NormalizationQueues.Workflows)
                )
            )
        );

        // Two chunks spent the budget: no review pass call at all.
        steps.SkipSizes.Should().Equal(0, 2);
        result.Chunks.Should().Be(2);
        // The first pass's unsure titles are still reported.
        result.StillUnsure.Select(r => r.Key).Should().BeEquivalentTo("key1", "key2");
    }

    [Test]
    public async Task AFailedChunkCountsAgainstTheCap()
    {
        var steps = new Steps { FailFirst = true };
        using var workflows = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(
                NormalizationQueues.Workflows
            ).AddWorkflow<KbCatalogueWorkflow>()
        );
        using var database = new TemporalWorker(
            environment.Client,
            new TemporalWorkerOptions(NormalizationQueues.Database).AddAllActivities(steps)
        );

        var result = await workflows.ExecuteAsync(() =>
            database.ExecuteAsync(() =>
                environment.Client.ExecuteWorkflowAsync(
                    (KbCatalogueWorkflow w) =>
                        w.RunAsync(new BuildKbCatalogue(Guid.NewGuid(), 2, null)),
                    new WorkflowOptions("test/" + Guid.NewGuid(), NormalizationQueues.Workflows)
                )
            )
        );

        // Two calls in all: the failed one (not retried by Temporal) and one more.
        steps.Attempts.Should().Be(2);
        result.Chunks.Should().Be(2);
    }
}
