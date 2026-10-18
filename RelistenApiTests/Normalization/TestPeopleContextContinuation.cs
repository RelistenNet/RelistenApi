using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>
/// Continuation admission uses retained files only. These tests create no extraction job,
/// database connection, API key, or HTTP client.
/// </summary>
[TestFixture]
public sealed class TestPeopleContextContinuation
{
    [Test]
    public async Task ValidPlanContainsOnlyTheUnrecordedOriginalBatches()
    {
        using var files = new ContinuationFiles();

        var plan = await files.ValidateAsync();

        plan.NewJobUuid.Should().Be(files.NewJobUuid);
        plan.OriginalJobUuid.Should().Be(files.OriginalJobUuid);
        plan.PendingInputsPath.Should().Be(files.PendingInputsPath);
        plan.PendingBatches.Should().Be(2);
        plan.PendingQuestions.Should().Be(2);
        plan.RecordedBatches.Should().Be(1);
        plan.RecordedQuestions.Should().Be(1);
    }

    [TestCase("manifest.json")]
    [TestCase("inputs.jsonl")]
    [TestCase("results.jsonl")]
    [TestCase("continuation.json")]
    public async Task RejectsChangedOriginalArtifacts(string fileName)
    {
        using var files = new ContinuationFiles();
        File.AppendAllText(Path.Combine(files.OriginalRun, fileName), " ");

        var validate = () => files.ValidateAsync();

        await validate.Should().ThrowAsync<InvalidDataException>();
    }

    [Test]
    public async Task RejectsChangedPlanBeforeReadingTheReferencedInputs()
    {
        using var files = new ContinuationFiles();
        var expectedHash = files.PlanHash;
        files.Plan["pendingInputsPath"] = Path.Combine(files.OriginalRun, "missing.jsonl");
        files.WritePlan();

        var validate = () =>
            new PeopleContextContinuation().ValidateAsync(files.PlanPath, expectedHash);

        await validate.Should().ThrowAsync<InvalidDataException>();
    }

    [Test]
    public async Task CompletedBatchCannotBecomePendingByChangingContinuationMetadata()
    {
        using var files = new ContinuationFiles();
        files.Continuation["completedBatchIds"] = new JsonArray("batch-2");
        files.Continuation["pendingBatchIds"] = new JsonArray("batch-1", "batch-3");
        files.WriteContinuation();
        files.WritePending([files.Batches[0], files.Batches[2]]);
        files.WritePlan();

        var validate = () => files.ValidateAsync();

        await validate.Should().ThrowAsync<InvalidDataException>();
    }

    [Test]
    public async Task PendingWitnessMustMatchTheOriginalBytesEvenWhenItsHashIsPinned()
    {
        using var files = new ContinuationFiles();
        var original = files.Batches[1];
        var question = original.Questions[0];
        var changed = original with
        {
            Questions =
            [
                question with
                {
                    Left = question.Left with { SourceUuid = Guid.NewGuid() },
                },
            ],
        };
        files.WritePending([changed, files.Batches[2]]);
        files.WritePlan();

        var validate = () => files.ValidateAsync();

        await validate.Should().ThrowAsync<InvalidDataException>();
    }

    [Test]
    public async Task ReformattingPendingJsonDoesNotPreserveThePinnedOriginalLines()
    {
        using var files = new ContinuationFiles();
        var bytes = File.ReadAllText(files.PendingInputsPath).Replace("\":", "\": ");
        File.WriteAllText(files.PendingInputsPath, bytes, new UTF8Encoding(false));
        files.Plan["pendingInputsSha256"] = Hash(files.PendingInputsPath);
        files.WritePlan();

        var validate = () => files.ValidateAsync();

        await validate.Should().ThrowAsync<InvalidDataException>();
    }

    [TestCase("capUsd", 11)]
    [TestCase("maximumTotalAttempts", 509)]
    [TestCase("maximumTotalAttempts", 507)]
    [TestCase("maximumTotalAttempts", 1)]
    [TestCase("maximumDurationSeconds", 1801)]
    [TestCase("minimumFreeBytes", 5905580031L)]
    public async Task RejectsAPlanOutsideTheContinuationLimits(string field, long value)
    {
        using var files = new ContinuationFiles();
        files.Plan[field] = value;
        files.WritePlan();

        var validate = () => files.ValidateAsync();

        await validate.Should().ThrowAsync<InvalidDataException>();
    }

    [Test]
    public async Task RejectsQuestionIdsRepeatedAcrossOtherwiseValidBatches()
    {
        using var files = new ContinuationFiles(duplicateQuestionId: true);

        var validate = () => files.ValidateAsync();

        await validate.Should().ThrowAsync<InvalidDataException>();
    }

    [Test]
    public async Task ContinuationRequiresANewJobUuid()
    {
        using var files = new ContinuationFiles();
        files.Plan["newJobUuid"] = files.OriginalJobUuid;
        files.WritePlan();

        var validate = () => files.ValidateAsync();

        await validate.Should().ThrowAsync<InvalidDataException>();
    }

    private static string Hash(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>
    /// Seals a cancelled three-batch run with one recorded result. The pending file copies
    /// the two remaining input lines, retaining each question and its original witnesses.
    /// </summary>
    private sealed class ContinuationFiles : IDisposable
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly string directory = Path.Combine(
            Path.GetTempPath(),
            "people-context-continuation-" + Guid.NewGuid()
        );

        public Guid OriginalJobUuid { get; } = Guid.NewGuid();
        public Guid NewJobUuid { get; } = Guid.NewGuid();
        public string OriginalRun { get; }
        public string PendingInputsPath { get; }
        public string PlanPath { get; }
        public string PlanHash => Hash(PlanPath);
        public PeopleContextBatchInput[] Batches { get; }
        public JsonObject Plan { get; }
        public JsonObject Continuation { get; }

        public ContinuationFiles(bool duplicateQuestionId = false)
        {
            OriginalRun = Path.Combine(directory, "original");
            PendingInputsPath = Path.Combine(directory, "pending.jsonl");
            PlanPath = Path.Combine(directory, "plan.json");
            Directory.CreateDirectory(OriginalRun);

            var template = TestLunaPeopleContextMatcher.Input();
            Batches = Enumerable
                .Range(1, 3)
                .Select(index =>
                    template with
                    {
                        BatchId = "batch-" + index,
                        Questions =
                        [
                            template.Questions[0] with
                            {
                                QuestionId = "question-" + (duplicateQuestionId ? 1 : index),
                            },
                        ],
                    }
                )
                .ToArray();
            WriteLines(Path.Combine(OriginalRun, "inputs.jsonl"), Batches);

            var result = new PeopleContextMatchResult(
                Batches[0].BatchId,
                "unresolved",
                [],
                Reason: "invalid_response"
            );
            WriteLines(
                Path.Combine(OriginalRun, "results.jsonl"),
                new[] { new PeopleContextBatchRow(Batches[0], result) }
            );
            var inputHash = Hash(Path.Combine(OriginalRun, "inputs.jsonl"));
            var resultsHash = Hash(Path.Combine(OriginalRun, "results.jsonl"));
            var report = new PeopleContextBatchReport(
                "cancelled",
                OriginalJobUuid,
                LunaPeopleContextMatcher.Version,
                3,
                3,
                1,
                0,
                new Dictionary<string, int> { ["unresolved"] = 1 },
                25m,
                0m,
                0m,
                1000,
                inputHash,
                resultsHash
            );
            File.WriteAllText(
                Path.Combine(OriginalRun, "manifest.json"),
                JsonSerializer.Serialize(report, Json)
            );

            Continuation = new JsonObject
            {
                ["jobUuid"] = OriginalJobUuid,
                ["state"] = "awaiting_storage_decision",
                ["inputPath"] = Path.Combine(OriginalRun, "inputs.jsonl"),
                ["inputSha256"] = inputHash,
                ["completedBatchIds"] = new JsonArray("batch-1"),
                ["pendingBatchIds"] = new JsonArray("batch-2", "batch-3"),
                ["recordedQuestions"] = 1,
                ["pendingQuestions"] = 2,
                ["unrecordedAttempts"] = new JsonArray(),
                ["accountedUsd"] = 0m,
                ["knownTokenCostUsd"] = 0m,
                ["automaticRestart"] = false,
                ["resumeRule"] = "Use only pending original inputs.",
            };
            Plan = new JsonObject
            {
                ["version"] = "people-context-continuation-v1",
                ["newJobUuid"] = NewJobUuid,
                ["originalRun"] = OriginalRun,
                ["originalJobUuid"] = OriginalJobUuid,
                ["originalManifestSha256"] = Hash(Path.Combine(OriginalRun, "manifest.json")),
                ["originalInputSha256"] = inputHash,
                ["originalResultsSha256"] = resultsHash,
                ["pendingInputsPath"] = PendingInputsPath,
                ["pendingBatches"] = 2,
                ["pendingQuestions"] = 2,
                ["recordedBatches"] = 1,
                ["recordedQuestions"] = 1,
                ["capUsd"] = 10m,
                ["maximumTotalAttempts"] = 508,
                ["maximumDurationSeconds"] = 1800,
                ["minimumFreeBytes"] = 5905580032L,
                ["method"] = LunaPeopleContextMatcher.Version,
            };
            WriteContinuation();
            WritePending(Batches.Skip(1));
            WritePlan();
        }

        public Task<PeopleContextContinuationPlan> ValidateAsync() =>
            new PeopleContextContinuation().ValidateAsync(PlanPath, PlanHash);

        public void WritePlan() => File.WriteAllText(PlanPath, Plan.ToJsonString(Json));

        public void WriteContinuation()
        {
            var path = Path.Combine(OriginalRun, "continuation.json");
            File.WriteAllText(path, Continuation.ToJsonString(Json));
            Plan["continuationSha256"] = Hash(path);
        }

        public void WritePending(IEnumerable<PeopleContextBatchInput> batches)
        {
            WriteLines(PendingInputsPath, batches);
            Plan["pendingInputsSha256"] = Hash(PendingInputsPath);
        }

        private static void WriteLines<T>(string path, IEnumerable<T> rows) =>
            File.WriteAllText(
                path,
                string.Join("\n", rows.Select(row => JsonSerializer.Serialize(row, Json))) + "\n",
                new UTF8Encoding(false)
            );

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
