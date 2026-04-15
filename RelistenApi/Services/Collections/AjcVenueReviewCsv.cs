using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Relisten.Import;

namespace Relisten.Services.Collections;

public sealed class AjcVenueReviewSourceRow
{
    public string archive_identifier { get; set; } = "";
    public string title { get; set; } = "";
    public string creator { get; set; } = "";
    public string display_date { get; set; } = "";
    public string current_venue { get; set; } = "";
    public string current_coverage { get; set; } = "";
    public string description { get; set; } = "";
}

public sealed class AjcVenueReviewCsvRow
{
    public string archive_identifier { get; set; } = "";
    public string title { get; set; } = "";
    public string creator { get; set; } = "";
    public string display_date { get; set; } = "";
    public string current_venue { get; set; } = "";
    public string current_coverage { get; set; } = "";
    public string description_first_lines { get; set; } = "";
    public string proposed_venue { get; set; } = "";
    public string proposed_coverage { get; set; } = "";
    public string confidence { get; set; } = "";
    public string parse_notes { get; set; } = "";
}

public static class AjcVenueReviewCsv
{
    private static readonly string[] Header =
    [
        "archive_identifier",
        "title",
        "creator",
        "display_date",
        "current_venue",
        "current_coverage",
        "description_first_lines",
        "proposed_venue",
        "proposed_coverage",
        "confidence",
        "parse_notes"
    ];

    public static AjcVenueReviewCsvRow BuildRow(AjcVenueReviewSourceRow source)
    {
        var inference = ArchiveOrgVenueInference.Infer(source.description, source.creator, source.title,
            source.display_date, source.current_venue, source.current_coverage);

        return new AjcVenueReviewCsvRow
        {
            archive_identifier = source.archive_identifier,
            title = source.title,
            creator = source.creator,
            display_date = source.display_date,
            current_venue = source.current_venue,
            current_coverage = source.current_coverage,
            description_first_lines = string.Join(" | ", inference.DescriptionFirstLines.Take(4)),
            proposed_venue = inference.ProposedVenue ?? "",
            proposed_coverage = inference.ProposedCoverage ?? "",
            confidence = inference.Confidence,
            parse_notes = inference.Notes
        };
    }

    public static string ToCsv(IEnumerable<AjcVenueReviewCsvRow> rows)
    {
        var builder = new StringBuilder();
        AppendRow(builder, Header);

        foreach (var row in rows)
        {
            AppendRow(builder,
            [
                row.archive_identifier,
                row.title,
                row.creator,
                row.display_date,
                row.current_venue,
                row.current_coverage,
                row.description_first_lines,
                row.proposed_venue,
                row.proposed_coverage,
                row.confidence,
                row.parse_notes
            ]);
        }

        return builder.ToString();
    }

    private static void AppendRow(StringBuilder builder, IReadOnlyList<string> values)
    {
        builder.AppendLine(string.Join(",", values.Select(Escape)));
    }

    private static string Escape(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }
}
