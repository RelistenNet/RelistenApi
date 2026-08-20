using System;
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using Relisten.Api.Models;
using Relisten.Import;

namespace RelistenApiTests.Importers.ArchiveOrg;

[TestFixture]
public class TestArchiveOrgDeletionGuard
{
    [Test]
    public void ExactlyFiveDeletedShowsShouldBeAllowed()
    {
        var sources = SourcesForDates("2024-01-01", "2024-01-02", "2024-01-03", "2024-01-04", "2024-01-05");

        var plan = BuildPlan(sources, new HashSet<string>());

        plan.ShowDisplayDates.Should().HaveCount(5);
        ArchiveOrgImporter.ExceedsDeletionLimit(plan).Should().BeFalse();
    }

    [Test]
    public void SixDeletedShowsShouldBeBlocked()
    {
        var sources = SourcesForDates(
            "2024-01-01",
            "2024-01-02",
            "2024-01-03",
            "2024-01-04",
            "2024-01-05",
            "2024-01-06");

        var plan = BuildPlan(sources, new HashSet<string>());

        plan.ShowDisplayDates.Should().HaveCount(6);
        ArchiveOrgImporter.ExceedsDeletionLimit(plan).Should().BeTrue();
    }

    [Test]
    public void MultipleSourcesForFiveShowsShouldBeAllowed()
    {
        var sources = new List<Source>();
        for (var date = 1; date <= 5; date++)
        {
            sources.Add(Source(date * 2, $"source-{date}-a", $"2024-01-{date:00}"));
            sources.Add(Source(date * 2 + 1, $"source-{date}-b", $"2024-01-{date:00}"));
        }

        var plan = BuildPlan(sources, new HashSet<string>());

        plan.SourceIdentifiers.Should().HaveCount(10);
        plan.ShowDisplayDates.Should().HaveCount(5);
        ArchiveOrgImporter.ExceedsDeletionLimit(plan).Should().BeFalse();
    }

    [Test]
    public void RetainedSourceShouldKeepADeletedSourcesShow()
    {
        var sources = SourcesForDates(
            "2024-01-01",
            "2024-01-02",
            "2024-01-03",
            "2024-01-04",
            "2024-01-05",
            "2024-01-06");
        sources.Add(Source(7, "retained-source", "2024-01-06"));

        var plan = BuildPlan(sources, new HashSet<string> { "retained-source" });

        plan.SourceIdentifiers.Should().HaveCount(6);
        plan.ShowDisplayDates.Should().HaveCount(5);
        plan.ShowDisplayDates.Should().NotContain("2024-01-06");
        ArchiveOrgImporter.ExceedsDeletionLimit(plan).Should().BeFalse();
    }

    [Test]
    public void SixSourceDateMovesShouldBeBlockedAndRestored()
    {
        var beforeSources = SourcesForDates(
            "2024-01-01",
            "2024-01-02",
            "2024-01-03",
            "2024-01-04",
            "2024-01-05",
            "2024-01-06");
        var currentSources = beforeSources
            .Select(source => Source(source.id, source.upstream_identifier, source.display_date.Replace("2024", "2025")))
            .ToList();
        var identifiersToKeep = currentSources
            .Select(source => source.upstream_identifier)
            .ToHashSet();

        var plan = ArchiveOrgImporter.BuildDeletionPlan(
            beforeSources,
            currentSources,
            currentSources,
            identifiersToKeep);

        plan.SourceIdentifiers.Should().BeEmpty();
        plan.ShowDisplayDates.Should().HaveCount(6);
        plan.DisplayDatesToRestore.Should().HaveCount(6);
        ArchiveOrgImporter.ExceedsDeletionLimit(plan).Should().BeTrue();
    }

    [Test]
    public void SourcesFromAnotherProviderShouldNotBeDeletionCandidates()
    {
        var archiveSource = Source(1, "archive-source", "2024-01-01");
        var otherProviderSource = Source(2, "other-source", "2024-01-02");
        var allSources = new List<Source> { archiveSource, otherProviderSource };

        var plan = ArchiveOrgImporter.BuildDeletionPlan(
            allSources,
            allSources,
            new[] { archiveSource },
            new HashSet<string>());

        plan.SourceIdentifiers.Should().Equal("archive-source");
        plan.ShowDisplayDates.Should().Equal("2024-01-01");
    }

    [Test]
    public void BlockedChainedDateMovesShouldRestoreEveryMovedSource()
    {
        var beforeSources = new List<Source>();
        var currentSources = new List<Source>();
        for (var index = 1; index <= 6; index++)
        {
            var firstId = index * 2 - 1;
            var secondId = index * 2;
            var firstDate = $"2024-01-{index:00}";
            var secondDate = $"2024-02-{index:00}";
            var finalDate = $"2024-03-{index:00}";

            beforeSources.Add(Source(firstId, $"source-{firstId}", firstDate));
            beforeSources.Add(Source(secondId, $"source-{secondId}", secondDate));
            currentSources.Add(Source(firstId, $"source-{firstId}", secondDate));
            currentSources.Add(Source(secondId, $"source-{secondId}", finalDate));
        }

        var identifiersToKeep = currentSources
            .Select(source => source.upstream_identifier)
            .ToHashSet();
        var plan = ArchiveOrgImporter.BuildDeletionPlan(
            beforeSources,
            currentSources,
            currentSources,
            identifiersToKeep);

        plan.ShowDisplayDates.Should().HaveCount(6);
        plan.DisplayDatesToRestore.Should().HaveCount(12);
        var datesAfterRestore = currentSources
            .Select(source => plan.DisplayDatesToRestore.GetValueOrDefault(source.id, source.display_date))
            .ToHashSet();
        datesAfterRestore.Should().BeEquivalentTo(beforeSources.Select(source => source.display_date));
    }

    private static ArchiveOrgDeletionPlan BuildPlan(
        IReadOnlyCollection<Source> sources,
        IReadOnlySet<string> identifiersToKeep)
    {
        return ArchiveOrgImporter.BuildDeletionPlan(sources, sources, sources, identifiersToKeep);
    }

    private static List<Source> SourcesForDates(params string[] dates)
    {
        var sources = new List<Source>();
        for (var index = 0; index < dates.Length; index++)
        {
            sources.Add(Source(index + 1, $"source-{index}", dates[index]));
        }

        return sources;
    }

    private static Source Source(int id, string identifier, string displayDate)
    {
        return new Source
        {
            id = id,
            upstream_identifier = identifier,
            display_date = displayDate,
            uuid = Guid.NewGuid()
        };
    }
}
