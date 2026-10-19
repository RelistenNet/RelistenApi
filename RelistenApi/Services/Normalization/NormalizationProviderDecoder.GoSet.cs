using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

public sealed partial class NormalizationProviderDecoder
{
    private static ProviderDecodeResult DecodeGoSetSong(ProviderObservationReader reader)
    {
        var html = ReadHtml(reader);
        if (html == null)
        {
            return reader.Result();
        }
        var canonical = html
            .Root.Descendants("link")
            .SingleOrDefault(node => node.GetAttributeValue("rel", "") == "canonical");
        var slug = canonical == null ? null : HtmlSongKey(canonical, "go-set.net");
        if (slug == null || slug != reader.Input.Resource.ProviderKey)
        {
            reader.Issue("go_set_song_identity_mismatch", "");
            return reader.Result();
        }
        var table = html.NodesWithClass(html.Root, "etp-table").SingleOrDefault();
        var summary = table?.PreviousSibling;
        while (summary != null && summary.Name != "div")
        {
            summary = summary.PreviousSibling;
        }
        var titleNode = summary?.ChildNodes.FirstOrDefault(node => node.Name == "b");
        var title = titleNode == null ? null : html.Text(titleNode);
        if (table == null || title == null)
        {
            reader.Issue("missing_go_set_song_history", "");
            return reader.Result();
        }

        var rows = table
            .Descendants("tr")
            .Where(row => row.ChildNodes.Any(node => node.Name == "td"))
            .ToArray();
        var ids = rows.SelectMany(row =>
                row.Descendants().Where(node => node.Attributes["data-song-id"] != null)
            )
            .Select(node => GoSetNumberAttribute(html, node, "data-song-id"))
            .Where(value => value != null)
            .Select(value => value!)
            .ToArray();
        if (ids.Select(value => value.Value).Distinct().Count() > 1)
        {
            reader.Issue("go_set_conflicting_song_identity", "");
            return reader.Result();
        }
        var identities = new List<ObservedProviderIdentity>
        {
            new(new("go-set.net", "song_slug", slug), html.Attribute(canonical!, "href")!.Evidence),
        };
        if (ids.FirstOrDefault() is { } songId)
        {
            identities.Add(new(new("go-set.net", "song", songId.Value), songId.Evidence));
        }
        var originalPerformer = GoSetOriginalPerformer(html, titleNode!);
        var history = new List<ProviderSetlistObservation>();
        foreach (var row in rows)
        {
            var observation = ReadGoSetHistoryRow(
                reader,
                html,
                row,
                title,
                identities,
                originalPerformer
            );
            if (observation != null)
            {
                history.Add(observation);
            }
        }
        var description = html.MarkupText("song_summary", summary!);
        return reader.Result() with
        {
            Repertoire =
            [
                new()
                {
                    Origin = new(
                        reader.Input.Resource,
                        html.Evidence(summary!)!,
                        ProviderObservationScope.ResourceItem
                    ),
                    Title = title,
                    Identities = identities,
                    Text = description == null ? [] : [description],
                },
            ],
            Setlists = history,
        };
    }

    private static ObservedValue<string>? GoSetOriginalPerformer(
        HtmlObservationSource html,
        HtmlNode title
    )
    {
        // This explicit sentence supplies a performance attribution, never a writer.
        // A generic "by" in the document title or a bare footnote has no such authority.
        var clause = title.NextSibling;
        if (clause?.NodeType != HtmlNodeType.Text || html.Text(clause) is not { } wording)
        {
            return null;
        }
        var match = Regex.Match(
            wording.Value,
            @"^was originally performed by (?<name>.+)\.$",
            RegexOptions.CultureInvariant
        );
        return match.Success ? new(match.Groups["name"].Value, wording.Evidence) : null;
    }

    private static ProviderSetlistObservation? ReadGoSetHistoryRow(
        ProviderObservationReader reader,
        HtmlObservationSource html,
        HtmlNode row,
        ObservedValue<string> title,
        IReadOnlyList<ObservedProviderIdentity> songs,
        ObservedValue<string>? originalPerformer
    )
    {
        var cells = row.ChildNodes.Where(node => node.Name == "td").ToArray();
        var button = row.Descendants()
            .FirstOrDefault(node => node.Attributes["data-show-id"] != null);
        var show = button == null ? null : GoSetNumberAttribute(html, button, "data-show-id");
        var dateLink = cells.ElementAtOrDefault(1)?.Descendants("a").FirstOrDefault();
        var date = dateLink == null ? null : html.Text(dateLink);
        if (
            cells.Length != 9
            || show == null
            || date == null
            || !GoSetDate(date.Value)
            || GoSetDateLink(html.Attribute(dateLink!, "href")?.Value) != date.Value
        )
        {
            reader.Issue("invalid_go_set_history_row", "");
            return null;
        }
        var occurrence = GoSetNumberAttribute(html, button!, "data-uniqueid");
        var notes = html.MarkupText("footnote", cells[8]);
        var link = html.Attribute(dateLink!, "href");
        var set = html.Text(cells[4]);
        var origin = new ProviderObservationOrigin(
            new("go-set.net", "show", show.Value),
            show.Evidence,
            ProviderObservationScope.PageMember
        );
        return new()
        {
            Origin = origin,
            LiteralDate = date,
            Venue = new() { Origin = origin, OriginalLocality = html.Text(cells[2]) },
            // Each row is one supplied occurrence. The song history is not a show sequence.
            Ordered = false,
            Passages =
            [
                new()
                {
                    Title = title,
                    OccurrenceIdentity =
                        occurrence == null
                            ? null
                            : new(
                                new("go-set.net", "passage", occurrence.Value),
                                occurrence.Evidence
                            ),
                    SongIdentities = songs,
                    SetLabel = set,
                    Annotations = notes == null ? [] : [notes],
                    PerformerAnnotations =
                        originalPerformer == null
                            ? []
                            : [new("cover_artist", originalPerformer, [])],
                },
            ],
            Text = link == null ? [] : [new("show_link", link)],
        };
    }

    private static ProviderDecodeResult DecodeGoSetShow(ProviderObservationReader reader)
    {
        var html = ReadHtml(reader);
        if (html == null)
        {
            return reader.Result();
        }
        var containers = html.NodesWithClass(html.Root, "setlist-container").ToArray();
        if (containers.Length != 1)
        {
            reader.Issue("go_set_setlist_account_unavailable", "");
            return reader.Result();
        }
        var container = containers[0];
        var anchor = html.NodesWithClass(container, "setlist-anchor").FirstOrDefault();
        var dated = anchor == null ? null : html.Attribute(anchor, "name");
        var date =
            dated?.Value.StartsWith("setlist-", StringComparison.Ordinal) == true
                ? dated.Value[8..]
                : null;
        if (date == null || !GoSetDate(date) || reader.Input.Resource.ProviderKey != "date:" + date)
        {
            reader.Issue("go_set_setlist_identity_mismatch", "");
            return reader.Result();
        }
        var account = GoSetShowAccount(html);
        var body = html.NodesWithClass(container, "setlist-body").FirstOrDefault();
        if (account == null || body == null)
        {
            reader.Issue("go_set_setlist_account_unavailable", "");
            return reader.Result();
        }
        var origin = new ProviderObservationOrigin(
            new("go-set.net", "show", account.Value),
            account.Evidence,
            ProviderObservationScope.PageMember
        );
        var (sets, passages) = ReadGoSetShowSequence(reader, html, body);
        var text = html.NodesWithClass(container, "setlist-footnotes")
            .Select(node => html.MarkupText("footnotes", node))
            .Where(note => note != null)
            .Select(note => note!)
            .ToArray();
        var venueNode = html.NodesWithClass(container, "setlist-venue").FirstOrDefault();
        var venueLink = venueNode?.Descendants("a").FirstOrDefault();
        var venueName = venueLink == null ? null : html.Text(venueLink);
        var venue =
            venueName == null
                ? null
                : new ProviderVenueObservation
                {
                    Origin = origin,
                    Names = [venueName],
                    OriginalLocality = html.Text(venueNode!),
                };
        return reader.Result() with
        {
            Venues = venue == null ? [] : [venue],
            Setlists =
            [
                new()
                {
                    Origin = origin,
                    LiteralDate = new(date, dated!.Evidence),
                    Venue = venue,
                    Ordered = true,
                    Sets = sets,
                    Passages = passages,
                    Text = text,
                    Footnotes = text.Select(note => new ProviderFootnoteDefinition(null, note))
                        .ToArray(),
                },
            ],
        };
    }

    private static ObservedValue<string>? GoSetShowAccount(HtmlObservationSource html)
    {
        var accounts = html
            .Root.Descendants("textarea")
            .Where(node =>
                node.GetAttributeValue("id", "")
                    .StartsWith("embed-code-textarea-", StringComparison.Ordinal)
            )
            .Select(node => html.Text(node))
            .Where(text => text != null)
            .Select(text => text!)
            .ToArray();
        if (accounts.Length != 1)
        {
            return null;
        }
        var match = Regex.Match(
            accounts[0].Value,
            @"\bsrc=""https://go-set\.net/api/embed\.php\?id=(?<id>[1-9][0-9]*)""",
            RegexOptions.CultureInvariant
        );
        return match.Success ? new(match.Groups["id"].Value, accounts[0].Evidence) : null;
    }

    private static (
        IReadOnlyList<ProviderSetObservation> Sets,
        IReadOnlyList<ProviderPassageObservation> Passages
    ) ReadGoSetShowSequence(
        ProviderObservationReader reader,
        HtmlObservationSource html,
        HtmlNode body
    )
    {
        var labels = html.NodesWithClass(body, "setlabel").ToArray();
        var sets = labels
            .Select(
                (node, index) =>
                    new ProviderSetObservation
                    {
                        Ordinal = index + 1,
                        Evidence = html.Evidence(node)!,
                        Label = html.Text(node),
                    }
            )
            .ToArray();
        var passages = new List<ProviderPassageObservation>();
        foreach (var box in html.NodesWithClass(body, "setlist-songbox"))
        {
            var song = box.Descendants("a")
                .FirstOrDefault(node => HtmlSongKey(node, "go-set.net") != null);
            var titleNode =
                song
                ?? box.Descendants("a").FirstOrDefault()
                ?? box.ChildNodes.FirstOrDefault(node =>
                    node.NodeType == HtmlNodeType.Text && !string.IsNullOrWhiteSpace(node.InnerText)
                );
            var title = titleNode == null ? null : html.Text(titleNode);
            if (title == null)
            {
                reader.Issue("go_set_passage_title_unavailable", "");
                continue;
            }
            if (song == null)
            {
                reader.Issue("go_set_passage_song_link_unavailable", "");
            }
            var label = Array.FindLastIndex(
                labels,
                node => node.StreamPosition < box.StreamPosition
            );
            var slug = song == null ? null : HtmlSongKey(song, "go-set.net");
            var markerNode = html.NodesWithClass(box, "setlist-transition").FirstOrDefault();
            var marker = markerNode == null ? null : html.Text(markerNode);
            var footnotes = new List<ObservedValue<int>>();
            foreach (var sup in box.Descendants("sup"))
            {
                var text = html.Text(sup);
                var match =
                    text == null
                        ? null
                        : Regex.Match(
                            text.Value,
                            @"^\[(?<number>[1-9][0-9]*)\]$",
                            RegexOptions.CultureInvariant
                        );
                if (
                    match?.Success == true
                    && int.TryParse(match.Groups["number"].Value, out var number)
                )
                {
                    footnotes.Add(new(number, text!.Evidence));
                }
                else
                {
                    reader.Issue("invalid_go_set_footnote_number", "");
                }
            }
            var suppliedOccurrence = GoSetNumberAttribute(html, box, "data-uniqueid");
            passages.Add(
                new()
                {
                    Ordinal = passages.Count + 1,
                    SetOrdinal = label < 0 ? null : label + 1,
                    SetLabel = label < 0 ? null : sets[label].Label,
                    Title = title,
                    SongIdentities =
                        slug == null
                            ? []
                            :
                            [
                                new(
                                    new("go-set.net", "song_slug", slug),
                                    html.Attribute(song!, "href")!.Evidence
                                ),
                            ],
                    OccurrenceIdentity =
                        suppliedOccurrence == null
                            ? null
                            : new(
                                new("go-set.net", "passage", suppliedOccurrence.Value),
                                suppliedOccurrence.Evidence
                            ),
                    TransitionMarker = marker?.Value is ">" or "->" or "→" ? marker : null,
                    FootnoteNumbers = footnotes,
                    // Go Set mixes cover labels, FTP and guests in these notes. Preserve
                    // them verbatim; the show representation does not type their roles.
                    Annotations = html.AnnotationsAfter(titleNode!, html.End(box)),
                }
            );
        }
        return (sets, passages);
    }

    private static ObservedValue<string>? GoSetNumberAttribute(
        HtmlObservationSource html,
        HtmlNode node,
        string name
    )
    {
        var value = html.Attribute(node, name);
        return
            value != null
            && Regex.IsMatch(value.Value, @"^[1-9][0-9]*$", RegexOptions.CultureInvariant)
            ? value
            : null;
    }

    private static string? GoSetDateLink(string? href)
    {
        if (
            href == null
            || !Uri.TryCreate(new Uri("https://go-set.net"), href, out var uri)
            || uri.Host != "go-set.net"
            || uri.AbsolutePath != "/setlists/"
        )
        {
            return null;
        }
        var match = Regex.Match(
            uri.Query,
            @"^\?date=(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})$",
            RegexOptions.CultureInvariant
        );
        return match.Success ? match.Groups["date"].Value : null;
    }

    private static bool GoSetDate(string value) =>
        DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _
        );
}
