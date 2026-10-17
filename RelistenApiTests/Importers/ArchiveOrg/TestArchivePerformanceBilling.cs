using FluentAssertions;
using Relisten.Data;
using Relisten.Import;

namespace RelistenApiTests.Importers.ArchiveOrg;

[TestFixture]
public sealed class TestArchivePerformanceBilling
{
    [TestCase("Jerry Garcia Band, 1975-03-07 (Early Show)", PerformanceType.Early)]
    [TestCase("Garcia & Saunders - LATE PERFORMANCE", PerformanceType.Late)]
    [TestCase("Afternoon Show at the Hall", PerformanceType.Afternoon)]
    [TestCase("Garcia - Matinée", PerformanceType.Afternoon)]
    [TestCase("Garcia at Unknown Venue - Early Show", PerformanceType.Early)]
    public void ExplicitConcertBillingIdentifiesPerformance(string title, PerformanceType type)
    {
        var result = ArchivePerformanceAssignmentService.ParseTitle(title);
        result.Type.Should().Be(type);
        result.Diagnostic.Should().BeNull();
        result.Label.Should().NotBeNullOrEmpty();
    }

    [TestCase("Jerry Garcia Band - Set 1 and Set 2")]
    [TestCase("Garcia - late set")]
    [TestCase("Garcia - Early Morning Rain")]
    [TestCase("Garcia - afternoon recording")]
    public void SetNumbersAndIncidentalWordsDoNotCreateConcerts(string title)
    {
        var result = ArchivePerformanceAssignmentService.ParseTitle(title);
        result.Type.Should().BeNull();
        result.Diagnostic.Should().BeNull();
    }

    [TestCase("Garcia - Early and Late Show")]
    [TestCase("Garcia - Late & Early Shows")]
    [TestCase("Garcia - Early Show / Late Show")]
    [TestCase("Garcia - Afternoon Show plus Late Performance")]
    public void CombinedCoverageDoesNotChooseAPrimaryPerformance(string title)
    {
        var result = ArchivePerformanceAssignmentService.ParseTitle(title);
        result.Type.Should().BeNull();
        result.Combined.Should().BeTrue();
        result.Diagnostic.Should().Be("multiple_performances_in_recording");
    }

    [TestCase("Garcia - Early Show?")]
    [TestCase("Garcia - probably Late Show")]
    public void QualifiedBillingLeavesThePerformanceUnresolved(string title)
    {
        var result = ArchivePerformanceAssignmentService.ParseTitle(title);
        result.Type.Should().BeNull();
        result.Diagnostic.Should().Be("uncertain_performance_billing");
    }

    [TestCase("Garcia - First Show")]
    [TestCase("Garcia - Show #2")]
    public void OrdinalConcertLabelsLeaveThePerformanceUnresolved(string title)
    {
        var result = ArchivePerformanceAssignmentService.ParseTitle(title);
        result.Type.Should().BeNull();
        result.Diagnostic.Should().Be("unclassified_performance_billing");
    }
}
