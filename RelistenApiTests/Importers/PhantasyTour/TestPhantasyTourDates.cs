using FluentAssertions;
using Newtonsoft.Json;
using Relisten.Vendor.PhantasyTour;

namespace RelistenApiTests.Importers.PhantasyTour;

[TestFixture]
public class TestPhantasyTourDates
{
    [TestCase("2002-04-25T20:00:00-05:00", "2002-04-26T01:00:00Z")]
    [TestCase("2002-04-25T12:00:00-05:00", "2002-04-25T17:00:00Z")]
    [TestCase("2002-04-25T01:00:00+09:00", "2002-04-24T16:00:00Z")]
    public void ListingAndDetailsPreserveTheProvidersLocalCalendarDate(string local, string utc)
    {
        var json = $$"""{"id":5238,"dateTime":"{{local}}","dateTimeUtc":"{{utc}}"}""";
        var listing = JsonConvert.DeserializeObject<PhantasyTourShowListing>(json)!;
        var details = JsonConvert.DeserializeObject<PhantasyTourShow>(json)!;

        listing.dateTime.Date.Should().Be(new DateTime(2002, 4, 25));
        details.dateTime.Date.Should().Be(listing.dateTime.Date);
        details.dateTime.Offset.Should().Be(DateTimeOffset.Parse(local).Offset);
        details.dateTime.UtcDateTime.Should().Be(details.dateTimeUtc);
    }
}
