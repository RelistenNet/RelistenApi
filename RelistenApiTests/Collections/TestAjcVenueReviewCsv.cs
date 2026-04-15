using FluentAssertions;
using Relisten.Services.Collections;

namespace RelistenApiTests.Collections;

[TestFixture]
public class TestAjcVenueReviewCsv
{
    [Test]
    public void BuildRowUsesSharedVenueInference()
    {
        var row = AjcVenueReviewCsv.BuildRow(new AjcVenueReviewSourceRow
        {
            archive_identifier = "ajc02200_allinsectunion1996-02-17",
            title = "All Insect Union Live at Lounge Ax 1996-02-17",
            creator = "All Insect Union",
            display_date = "1996-02-17",
            current_venue = "Unknown Venue",
            current_coverage = "Unknown Location",
            description = """
            <div>All Insect Union</div><div>February 17, 1996</div><div>Lounge Ax</div><div>Chicago, Illinois</div><div><br /></div><div>Recording generously loaned from the Aadam Jacobs Audio Archive.</div>
            """
        });

        row.proposed_venue.Should().Be("Lounge Ax");
        row.proposed_coverage.Should().Be("Chicago, IL");
        row.confidence.Should().Be("high");
        row.description_first_lines.Should().Be("All Insect Union | February 17, 1996 | Lounge Ax | Chicago, Illinois");
    }

    [Test]
    public void BuildRowKeepsKnownCurrentVenueAndNormalizesKnownCurrentCoverage()
    {
        var row = AjcVenueReviewCsv.BuildRow(new AjcVenueReviewSourceRow
        {
            archive_identifier = "ajc_current",
            title = "Example Live at Wrong Room 1996-02-17",
            creator = "Example",
            display_date = "1996-02-17",
            current_venue = "Metro",
            current_coverage = "Chicago, Illinois 60613",
            description = """
            <div>Example</div><div>February 17, 1996</div><div>Wrong Room</div><div>Wrong City, WI</div>
            """
        });

        row.proposed_venue.Should().Be("Metro");
        row.proposed_coverage.Should().Be("Chicago, IL");
        row.confidence.Should().Be("existing");
        row.parse_notes.Should().Contain("current venue");
        row.parse_notes.Should().Contain("current coverage");
    }

    [Test]
    public void BuildRowOnlyParsesFieldsThatAreUnknown()
    {
        var row = AjcVenueReviewCsv.BuildRow(new AjcVenueReviewSourceRow
        {
            archive_identifier = "ajc_partial_current",
            title = "Example Live at Lounge Ax 1996-02-17",
            creator = "Example",
            display_date = "1996-02-17",
            current_venue = "Unknown Venue",
            current_coverage = "Milwaukee, Wisconsin 53202",
            description = """
            <div>Example</div><div>February 17, 1996</div><div>Lounge Ax</div><div>Chicago, Illinois</div>
            """
        });

        row.proposed_venue.Should().Be("Lounge Ax");
        row.proposed_coverage.Should().Be("Milwaukee, WI");
        row.confidence.Should().Be("existing");
        row.parse_notes.Should().Contain("current coverage");
    }

    [Test]
    public void ToCsvEscapesCommasQuotesAndLineBreaks()
    {
        var csv = AjcVenueReviewCsv.ToCsv([
            new AjcVenueReviewCsvRow
            {
                archive_identifier = "id1",
                title = "Title with, comma",
                creator = "Artist \"Quoted\"",
                display_date = "1996-02-17",
                current_venue = "Unknown Venue",
                current_coverage = "Unknown Location",
                description_first_lines = "Line 1\nLine 2",
                proposed_venue = "Lounge Ax",
                proposed_coverage = "Chicago, IL",
                confidence = "high",
                parse_notes = "single-line venue"
            }
        ]);

        csv.Should().Contain("\"Title with, comma\"");
        csv.Should().Contain("\"Artist \"\"Quoted\"\"\"");
        csv.Should().Contain("\"Line 1\nLine 2\"");
    }
}
