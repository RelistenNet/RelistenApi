using FluentAssertions;
using NUnit.Framework;
using Relisten.Import;
using Relisten.Vendor.ArchiveOrg.Metadata;

namespace RelistenApiTests.Importers.ArchiveOrg;

[TestFixture]
public class TestArchiveOrgFixDisplayDate
{
    private static string InvokeFixDisplayDate(string date)
    {
        return ArchiveOrgImporterUtils.FixDisplayDate(date, "test-id")!;
    }

    [Test]
    public void FixDisplayDate_ShouldHandleZeroMonthOrDayAcrossYears()
    {
        for (var year = 1950; year <= 2050; year++)
        {
            InvokeFixDisplayDate($"{year}-05-05").Should().Be($"{year}-05-05");
            InvokeFixDisplayDate($"{year}-00-05").Should().Be($"{year}-XX-05");
            InvokeFixDisplayDate($"{year}-05-00").Should().Be($"{year}-05-XX");
            InvokeFixDisplayDate($"{year}-00-00").Should().Be($"{year}-XX-XX");
        }
    }

    [Test]
    public void FixDisplayDate_ShouldFlipMonthAndDayWhenPossible()
    {
        // Month > 12 but day ≤ 12: can flip
        InvokeFixDisplayDate("1997-20-05").Should().Be("1997-05-20");
        InvokeFixDisplayDate("1997-31-01").Should().Be("1997-01-31");
        InvokeFixDisplayDate("2013-14-02").Should().Be("2013-02-14");
    }

    [Test]
    public void FixDisplayDate_ShouldConvertToXXWhenFlipNotPossible()
    {
        // Month > 12 and day > 12: can't flip, convert to XX
        InvokeFixDisplayDate("1997-20-15").Should().Be("1997-XX-15");
        InvokeFixDisplayDate("1997-13-25").Should().Be("1997-XX-25");
    }

    [Test]
    public void FixDisplayDate_ShouldConvertInvalidDayToXX()
    {
        InvokeFixDisplayDate("1997-05-45").Should().Be("1997-05-XX");
        InvokeFixDisplayDate("1997-05-32").Should().Be("1997-05-XX");
    }

    [Test]
    public void FixDisplayDate_ShouldPassThroughValidDates()
    {
        InvokeFixDisplayDate("1997-05-20").Should().Be("1997-05-20");
        InvokeFixDisplayDate("2024-12-31").Should().Be("2024-12-31");
    }

    [Test]
    public void FixDisplayDate_ShouldPassThroughExistingXXDates()
    {
        InvokeFixDisplayDate("1997-XX-05").Should().Be("1997-XX-05");
        InvokeFixDisplayDate("1997-05-XX").Should().Be("1997-05-XX");
        InvokeFixDisplayDate("1997-XX-XX").Should().Be("1997-XX-XX");
    }

    [Test]
    public void FixDisplayDate_ShouldStripTimeComponentFromIso8601Dates()
    {
        // Standard ISO 8601 with UTC timezone
        InvokeFixDisplayDate("2011-03-30T00:00:00Z").Should().Be("2011-03-30");
        InvokeFixDisplayDate("1997-05-20T12:34:56Z").Should().Be("1997-05-20");

        // With invalid month/day that needs fixing after stripping time
        InvokeFixDisplayDate("2010-00-00T00:00:00Z").Should().Be("2010-XX-XX");
        InvokeFixDisplayDate("1997-20-05T00:00:00Z").Should().Be("1997-05-20");
    }

    [Test]
    public void FixDisplayDate_ShouldReturnNullForInvalidCalendarDates()
    {
        // Feb 29 in non-leap year - can't be fixed, returns null
        var meta = new Metadata { date = "1991-02-29", identifier = "test-id" };
        ArchiveOrgImporterUtils.FixDisplayDate(meta).Should().BeNull();

        // Feb 29 in leap year - valid, passes through
        InvokeFixDisplayDate("1992-02-29").Should().Be("1992-02-29");

        // April 31 doesn't exist - can't be fixed, returns null
        var meta2 = new Metadata { date = "1997-04-31", identifier = "test-id" };
        ArchiveOrgImporterUtils.FixDisplayDate(meta2).Should().BeNull();

        // November 31 doesn't exist - can't be fixed, returns null
        var meta3 = new Metadata { date = "1997-11-31", identifier = "test-id" };
        ArchiveOrgImporterUtils.FixDisplayDate(meta3).Should().BeNull();

        // September 31 doesn't exist - can't be fixed, returns null (even with ISO 8601 format)
        ArchiveOrgImporterUtils.FixDisplayDate("2009-09-31", "test-id").Should().BeNull();
        ArchiveOrgImporterUtils.FixDisplayDate("2009-09-31T00:00:00Z", "test-id").Should().BeNull();
    }
}
