using FluentAssertions;
using Relisten.Data;

namespace RelistenApiTests.Collections;

[TestFixture]
public class TestCollectionQueries
{
    [Test]
    public void OnThisDayDateValidationAcceptsLeapDayAndRejectsInvalidDates()
    {
        CollectionService.IsValidMonthDay(2, 29).Should().BeTrue();
        CollectionService.IsValidMonthDay(2, 30).Should().BeFalse();
        CollectionService.IsValidMonthDay(13, 1).Should().BeFalse();
        CollectionService.IsValidMonthDay(0, 1).Should().BeFalse();
    }
}
