using FluentAssertions;
using Relisten.Import;

namespace RelistenApiTests.Importers.ArchiveOrg;

[TestFixture]
public class TestArchiveOrgVenueInference
{
    [Test]
    public void InfersVenueAndCoverageWhenDateComesBeforeVenue()
    {
        var result = ArchiveOrgVenueInference.Infer(
            """
            <div>All Insect Union</div><div>February 17, 1996</div><div>Lounge Ax</div><div>Chicago, Illinois</div><div><br /></div><div>Recording generously loaned from the Aadam Jacobs Audio Archive.</div>
            """,
            "All Insect Union",
            "All Insect Union Live at Lounge Ax 1996-02-17",
            "1996-02-17");

        result.ProposedVenue.Should().Be("Lounge Ax");
        result.ProposedCoverage.Should().Be("Chicago, IL");
        result.Confidence.Should().Be("high");
        result.DescriptionFirstLines.Should().StartWith(["All Insect Union", "February 17, 1996", "Lounge Ax"]);
    }

    [Test]
    public void InfersVenueAndCoverageWhenVenueComesBeforeDate()
    {
        var result = ArchiveOrgVenueInference.Infer(
            """
            <div>Spatula</div><div>Lounge Ax</div><div>Chicago, IL</div><div>July 20th, 1996</div><div><br /></div><div>Master recording generously loaned from the Aadam Jacobs Audio Archive</div>
            """,
            "Spatula",
            "Spatula Live at Lounge Ax 1996-07-20",
            "1996-07-20");

        result.ProposedVenue.Should().Be("Lounge Ax");
        result.ProposedCoverage.Should().Be("Chicago, IL");
        result.Confidence.Should().Be("high");
    }

    [Test]
    public void JoinsMultiLineFestivalAndVenueContext()
    {
        var result = ArchiveOrgVenueInference.Infer(
            """
            <div>Björk</div><div>July 19, 2013</div><div>Pitchfork Music Festival</div><div>Union Park</div><div>Chicago, IL</div><div>Master recording generously loaned from the Aadam Jacobs Audio Archive</div>
            """,
            "Björk",
            "Björk Live at Pitchfork Music Festival 2013-07-19",
            "2013-07-19");

        result.ProposedVenue.Should().Be("Pitchfork Music Festival - Union Park");
        result.ProposedCoverage.Should().Be("Chicago, IL");
        result.Confidence.Should().Be("medium");
        result.Notes.Should().Contain("multi-line");
    }

    [Test]
    public void MarksAddressHeavyHeadersAsMediumConfidence()
    {
        var result = ArchiveOrgVenueInference.Infer(
            """
            <div>CHEER-ACCIDENT&nbsp;</div><div>Saturday, 22 June 2013</div><div>Mayne Stage</div><div>1328 W Morse Avenue&nbsp;</div><div>Chicago, Illinois&nbsp; 60626</div><div>USA</div><div><br /></div><div>Recording generously loaned from the Aadam Jacobs Audio Archive</div>
            """,
            "Cheer-Accident",
            "Cheer-Accident Live at Mayne Stage 2013-06-22",
            "2013-06-22");

        result.ProposedVenue.Should().Be("Mayne Stage");
        result.ProposedCoverage.Should().Be("Chicago, IL");
        result.Confidence.Should().Be("medium");
        result.Notes.Should().Contain("address");
    }

    [Test]
    public void ExistingMetadataVenueAndCoverageWinWhenPresent()
    {
        var result = ArchiveOrgVenueInference.ResolveVenue(
            "Cicero's",
            "St. Louis, MO",
            """
            <div>Waco Brothers</div><div>Wrong Room</div><div>Wrong City, IL</div><div>February 1, 1996</div>
            """,
            "Waco Brothers",
            "Waco Brothers Live at Cicero's on 1996-02-01",
            "1996-02-01",
            inferFromDescription: true);

        result.VenueName.Should().Be("Cicero's");
        result.Coverage.Should().Be("St. Louis, MO");
        result.Inference.ProposedVenue.Should().Be("Cicero's");
        result.Inference.Confidence.Should().Be("existing");
    }

    [Test]
    public void DisabledInferencePreservesBlankMetadata()
    {
        var result = ArchiveOrgVenueInference.ResolveVenue(
            "",
            "",
            """
            <div>All Insect Union</div><div>February 17, 1996</div><div>Lounge Ax</div><div>Chicago, Illinois</div>
            """,
            "All Insect Union",
            "All Insect Union Live at Lounge Ax 1996-02-17",
            "1996-02-17",
            inferFromDescription: false);

        result.VenueName.Should().Be("");
        result.Coverage.Should().Be("");
        result.Inference.ProposedVenue.Should().BeNull();
    }

    [Test]
    public void SplitsCompactVenueAndCityLine()
    {
        var result = ArchiveOrgVenueInference.Infer(
            """
            <div>Devil in a Woodpile</div><div>June 11, 2015</div><div>Hideout, Chicago IL</div><div>Recording generously loaned from the Aadam Jacobs Audio Archive</div>
            """,
            "Devil in a Woodpile",
            "Devil in a Woodpile Live at Hideout 2015-06-11",
            "2015-06-11");

        result.ProposedVenue.Should().Be("Hideout");
        result.ProposedCoverage.Should().Be("Chicago, IL");
        result.Confidence.Should().Be("high");
    }

    [Test]
    public void SplitsCityAtVenueLine()
    {
        var result = ArchiveOrgVenueInference.Infer(
            """
            <div>NATE LEPINE TRIO</div><div>2013-09-29</div><div>Chicago, IL at the Hungry Brain</div><div>Master recording generously loaned from the Aadam Jacobs Audio Archive</div>
            """,
            "Nate Lepine Trio",
            "Nate Lepine Trio Live at the Hungry Brain 2013-09-29",
            "2013-09-29");

        result.ProposedVenue.Should().Be("Hungry Brain");
        result.ProposedCoverage.Should().Be("Chicago, IL");
        result.Confidence.Should().Be("high");
    }

    [Test]
    public void ParsesSingleLineArtistDateVenueCoverageHeader()
    {
        var result = ArchiveOrgVenueInference.Infer(
            """
            <div>Guided By Voices - 1995-04-05 Lounge Ax, Chicago, IL</div><div><br /></div><div>1. Intro</div><div>2. Closer You Are</div>
            """,
            "Guided By Voices",
            "Guided By Voices Live at Lounge Ax 1995-04-05",
            "1995-04-05");

        result.ProposedVenue.Should().Be("Lounge Ax");
        result.ProposedCoverage.Should().Be("Chicago, IL");
        result.Confidence.Should().Be("high");
    }

    [Test]
    public void ParsesEmDashVenueCoverageDateHeader()
    {
        var result = ArchiveOrgVenueInference.Infer(
            """
            <div>Hüsker Dü — Cubby Bear, Chicago IL, 23 Jun 1985 (early show)</div><div>Audience recording by Aadam on Sony Walkman WM-R2 cassette recorder using built-in mic</div>
            """,
            "Hüsker Dü",
            "Hüsker Dü Live at Cubby Bear 1985-06-23 (Early)",
            "1985-06-23");

        result.ProposedVenue.Should().Be("Cubby Bear");
        result.ProposedCoverage.Should().Be("Chicago, IL");
        result.Confidence.Should().Be("high");
    }

    [Test]
    public void CombinesCityAndCountryStack()
    {
        var result = ArchiveOrgVenueInference.Infer(
            """
            <div>The Leopards</div><div>July 2nd, 1998</div><div>Purcell Room</div><div>South Bank Centre</div><div>London</div><div>UK</div><div>Recording generously loaned from the Aadam Jacobs Audio Archive</div>
            """,
            "The Leopards",
            "The Leopards Live at Purcell Room 1998-07-02",
            "1998-07-02");

        result.ProposedVenue.Should().Be("Purcell Room - South Bank Centre");
        result.ProposedCoverage.Should().Be("London, UK");
        result.Confidence.Should().Be("medium");
    }

    [Test]
    public void TrackListOnlyDescriptionDoesNotBecomeVenue()
    {
        var result = ArchiveOrgVenueInference.Infer(
            """
            Recorded by Aadam Jacobs<br /><br />1 intro<br />2 Never Been in a Riot<br />3 Millionaire
            """,
            "Mekons",
            "Mekons Live at The Mutiny on 2007-10-12",
            "2007-10-12");

        result.ProposedVenue.Should().BeNull();
        result.ProposedCoverage.Should().BeNull();
        result.Confidence.Should().Be("low");
    }

    [Test]
    public void NumberedArtistNameDoesNotStopHeaderParsingAsTrackList()
    {
        var result = ArchiveOrgVenueInference.Infer(
            """
            <div>3 Days</div><div>Fireside Bowl</div><div>Chicago, IL</div><div>February 13th, 1998</div>
            """,
            "3 Days",
            "3 Days Live at Fireside Bowl 1998-02-13",
            "1998-02-13");

        result.ProposedVenue.Should().Be("Fireside Bowl");
        result.ProposedCoverage.Should().Be("Chicago, IL");
        result.Confidence.Should().Be("high");
    }

    [Test]
    public void TimeLikeVenueIsNotIgnoredAsDate()
    {
        var result = ArchiveOrgVenueInference.Infer(
            """
            <div>Corey Wills</div><div>October 12, 2014</div><div>8AM</div><div>Chicago, Illinois</div>
            """,
            "Corey Wills",
            "Corey Wills Live at 8AM 2014-10-12",
            "2014-10-12");

        result.ProposedVenue.Should().Be("8AM");
        result.ProposedCoverage.Should().Be("Chicago, IL");
        result.Confidence.Should().Be("high");
    }

    [Test]
    public void CityLineCanAppearBeforeVenueLine()
    {
        var result = ArchiveOrgVenueInference.Infer(
            """
            <div>Depeche Mode</div><div>March 22nd, 1985</div><div>Chicago, IL</div><div>Aragon Ballroom</div><div>Some Great Reward Tour</div><div>Equipment: Unknown Cassette Recorder</div>
            """,
            "Depeche Mode",
            "Depeche Mode Live at Aragon Ballroom 1985-03-22",
            "1985-03-22");

        result.ProposedVenue.Should().Be("Aragon Ballroom");
        result.ProposedCoverage.Should().Be("Chicago, IL");
        result.Confidence.Should().Be("medium");
    }

    [Test]
    public void CoverageLineCanIncludeShowTimingSuffix()
    {
        var result = ArchiveOrgVenueInference.Infer(
            """
            <div>They Might Be Giants</div><div>December 16, 1988</div><div>Cabaret Metro</div><div>Chicago, Illinois (early show)</div>
            """,
            "They Might Be Giants",
            "They Might Be Giants Live at Cabaret Metro (Early Show) 1988-12-16",
            "1988-12-16");

        result.ProposedVenue.Should().Be("Cabaret Metro");
        result.ProposedCoverage.Should().Be("Chicago, IL");
        result.Confidence.Should().Be("high");
    }

    [Test]
    public void ArtistNameEndingInStateAbbreviationIsNotCoverage()
    {
        var result = ArchiveOrgVenueInference.Infer(
            """
            <div>CLEM SNIDE</div><div>Saturday, 4 August 2001</div><div>Mohawk Place</div><div>47 East Mohawk Street</div>
            """,
            "Clem Snide",
            "Clem Snide Live at Mohawk Place 2001-08-04",
            "2001-08-04");

        result.ProposedVenue.Should().Be("Mohawk Place");
        result.ProposedCoverage.Should().BeNull();
        result.Confidence.Should().Be("low");
    }

    [Test]
    public void StreetSegmentLineIsOmittedFromFestivalVenue()
    {
        var result = ArchiveOrgVenueInference.Infer(
            """
            <div>Swirlies</div><div>Sunday, 12 July 2015</div><div>West Fest</div><div>Main Stage</div><div>Chicago Avenue between Damen Avenue and Wood Street (1800-2000 W Chicago Avenue)</div><div>Chicago, Illinois 60622</div>
            """,
            "Swirlies",
            "Swirlies Live at West Fest 2015-07-12",
            "2015-07-12");

        result.ProposedVenue.Should().Be("West Fest - Main Stage");
        result.ProposedCoverage.Should().Be("Chicago, IL");
        result.Confidence.Should().Be("medium");
        result.Notes.Should().Contain("address");
    }

    [Test]
    public void LeadingTitleComponentIsNotIncludedAsVenue()
    {
        var result = ArchiveOrgVenueInference.Infer(
            """
            <div>Fred Armisen</div><div>Letters to Santa 24 Hours of Improv and Music</div><div>The Second City e.t.c. Theater</div><div>Chicago, IL</div>
            """,
            "Letters to Santa 24 Hours of Improv and Music The Second City e.t.c. Theater",
            "Fred Armisen - Letters to Santa 24 Hours of Improv and Music Live at The Second City e.t.c. Theater",
            "2012-12-18");

        result.ProposedVenue.Should().Be("Letters to Santa 24 Hours of Improv and Music - The Second City e.t.c. Theater");
        result.ProposedCoverage.Should().Be("Chicago, IL");
        result.Confidence.Should().Be("medium");
    }

    [Test]
    public void MisspelledMonthDateLineIsStillIgnored()
    {
        var result = ArchiveOrgVenueInference.Infer(
            """
            <div>Super Furry Animals</div><div>Saturday, 16 Feburary 2002</div><div>Metro</div><div>3730 North Clark Street</div><div>Chicago, Illinois 60613</div>
            """,
            "Super Furry Animals",
            "Super Furry Animals Live at Metro 2008-02-16",
            "2008-02-16");

        result.ProposedVenue.Should().Be("Metro");
        result.ProposedCoverage.Should().Be("Chicago, IL");
        result.Confidence.Should().Be("medium");
    }

    [Test]
    public void NormalizesFullStateNamesAndOmitsZipCodes()
    {
        var result = ArchiveOrgVenueInference.Infer(
            """
            <div>The Sea and Cake</div><div>March 1, 1997</div><div>Shank Hall</div><div>Milwaukee, Wisconsin 53202</div>
            """,
            "The Sea and Cake",
            "The Sea and Cake Live at Shank Hall 1997-03-01",
            "1997-03-01");

        result.ProposedVenue.Should().Be("Shank Hall");
        result.ProposedCoverage.Should().Be("Milwaukee, WI");
        result.Confidence.Should().Be("high");
    }

    [Test]
    public void CountryLineAfterStateLineDoesNotOverrideCoverage()
    {
        var result = ArchiveOrgVenueInference.Infer(
            """
            <div>Bitch Magnet</div><div>October 18th, 1990</div><div>Reckless Records</div><div>Chicago IL</div><div>UK</div>
            """,
            "Bitch Magnet",
            "Bitch Magnet Live at Reckless Records 1990-10-18",
            "1990-10-18");

        result.ProposedVenue.Should().Be("Reckless Records");
        result.ProposedCoverage.Should().Be("Chicago, IL");
        result.Confidence.Should().Be("high");
    }
}
