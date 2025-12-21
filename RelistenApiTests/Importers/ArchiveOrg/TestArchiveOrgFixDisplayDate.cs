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
        var meta = new Metadata { date = date, identifier = "id" };
        return ArchiveOrgImporterUtils.FixDisplayDate(meta)!;
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
}
