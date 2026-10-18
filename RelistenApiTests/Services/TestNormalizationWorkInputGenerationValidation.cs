using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>Invalid generation observations fail before Work opens a database connection.</summary>
[TestFixture]
public sealed class TestNormalizationWorkInputGenerationValidation
{
    [TestCase(false, 0L)]
    [TestCase(false, -1L)]
    [TestCase(false, long.MinValue)]
    [TestCase(true, 0L)]
    [TestCase(true, -1L)]
    [TestCase(true, long.MinValue)]
    public async Task SuppliedGenerationMustBePositiveBeforeAdmission(
        bool contributed,
        long generation
    )
    {
        // This command has no database. Reaching admission instead of rejecting the command
        // would fail with a different exception, so this fixture needs no local services.
        var work = new NormalizationWorkService(null!);
        var resource = new NormalizationResourceKey("test", "generation", "validation");
        var subject = new NormalizationSubjectKey("people", "person", Guid.NewGuid());
        NormalizationBindingInputReference[] expected = [new(Guid.NewGuid(), resource, generation)];
        Func<Task> begin = contributed
            ? () =>
                work.BeginContributedBatchAsync(
                    Guid.NewGuid(),
                    subject,
                    "validation-v1",
                    [new(new(resource, 0, [resource], []), expected)]
                )
            : () =>
                work.BeginWorkAsync(
                    Guid.NewGuid(),
                    subject,
                    "validation-v1",
                    [resource],
                    [],
                    expectedInputRevisions: expected
                );

        await begin.Should().ThrowAsync<ArgumentException>();
    }

    [TestCase(null)]
    [TestCase(2L)]
    public async Task SharedResourcePreparationsCannotMixDifferentGenerationObservations(
        long? secondGeneration
    )
    {
        var work = new NormalizationWorkService(null!);
        var resource = new NormalizationResourceKey("test", "generation", "shared-validation");
        var revision = Guid.NewGuid();
        var contributor = new NormalizationResourceKey("test", "contributor", "first");
        Func<Task> begin = () =>
            work.BeginContributedBatchAsync(
                Guid.NewGuid(),
                new("people", "person", Guid.NewGuid()),
                "validation-v1",
                [
                    new(new(contributor, 0, [resource], []), [new(revision, resource, 1)]),
                    new(
                        new(contributor with { ProviderKey = "second" }, 0, [resource], []),
                        [new(revision, resource, secondGeneration)]
                    ),
                ]
            );

        await begin.Should().ThrowAsync<NormalizationConflictException>();
    }
}
