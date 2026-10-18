using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>Accepted entrypoints cannot adopt an unrelated Work request, including terminal replay.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationPeopleResolutionWriter
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizedPeopleService people = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        people = new(fixture.Db, fixture.Work);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task UnknownApplicationCannotAdoptRunningOrPublishedWork(
        bool identity,
        bool published
    )
    {
        var source = await fixture.Source();
        var name = "Writer " + Guid.NewGuid().ToString("N");
        var input = await fixture.Capture(name, "people");
        var candidates = await people.FindCandidatesAsync(name);
        if (!identity)
        {
            input = await fixture.Own(input, "recording", source.SourceUuid);
        }
        if (published)
        {
            await fixture.Work.TryPublishAsync(
                input.Ticket.WorkUuid,
                (_, _, _) => Task.CompletedTask
            );
        }
        var before = await fixture.Work.GetSubjectAsync(input.Ticket.SubjectUuid);

        Func<Task<NormalizationPublicationResult>> publish = identity
            ? () =>
                people.PublishResolutionIdentityAsync(
                    input.Ticket.WorkUuid,
                    Guid.NewGuid(),
                    input.Subject.Uuid
                )
            : () => people.PublishResolutionCreditsAsync(input.Ticket.WorkUuid, Guid.NewGuid());

        await publish.Should().ThrowAsync<ArgumentException>();

        (await fixture.Work.GetSubjectAsync(input.Ticket.SubjectUuid))
            .Should()
            .BeEquivalentTo(before);
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().BeEmpty();
        (await people.FindCandidatesAsync(name)).Should().BeEquivalentTo(candidates);
    }
}
