using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

// Provider structure belongs here; HtmlObservationSource keeps every citation in original source coordinates.
public sealed partial class NormalizationProviderDecoder
{
    private static ProviderDecodeResult DecodeJerry(ProviderObservationReader reader)
    {
        var html = ReadHtml(reader);
        if (html == null)
        {
            return reader.Result();
        }

        var names = html.NodesWithClass(html.Root, "venue-name").ToArray();
        if (names.Length == 0)
        {
            reader.Issue("missing_jerry_show_details", "");
            return reader.Result();
        }
        var venueName = html.Text(names[0]);
        var venue =
            venueName == null
                ? null
                : new ProviderVenueObservation
                {
                    Origin = reader.Origin(
                        reader.Input.Resource,
                        "",
                        ProviderObservationScope.ResourceItem
                    ),
                    Names = [venueName],
                    OriginalLocality = html.FirstTextWithClass(html.Root, "venue-address"),
                    Country = html.FirstTextWithClass(html.Root, "venue-country"),
                };
        var performer = names.Length > 1 ? html.Text(names[1]) : null;
        var (sets, passages) = ReadJerrySequence(reader, html);

        var time = html
            .Root.Descendants("time")
            .FirstOrDefault(node => node.Attributes["datetime"] != null);
        var tour = names.Length > 2 ? html.MarkupText("tour", names[2]) : null;
        var edition = new ProviderSetlistObservation
        {
            Origin = reader.Origin(
                reader.Input.Resource,
                "",
                ProviderObservationScope.ResourceItem
            ),
            Performers = performer == null ? [] : [new() { Name = performer }],
            LiteralDate = time == null ? null : html.Attribute(time, "datetime"),
            Venue = venue,
            Ordered = sets.Count > 0,
            Sets = sets,
            Passages = passages,
            Text = tour == null ? [] : [tour],
        };
        return reader.Result() with { Venues = venue == null ? [] : [venue], Setlists = [edition] };
    }

    private static (
        IReadOnlyList<ProviderSetObservation> Sets,
        IReadOnlyList<ProviderPassageObservation> Passages
    ) ReadJerrySequence(ProviderObservationReader reader, HtmlObservationSource html)
    {
        var sets = new List<ProviderSetObservation>();
        var passages = new List<ProviderPassageObservation>();
        foreach (
            var list in html
                .Root.Descendants("ol")
                .Where(node => !node.Ancestors().Any(parent => parent.Name == "ol"))
        )
        {
            var boundary = html.Evidence(list);
            if (boundary == null)
            {
                continue;
            }
            var labelNode = list.Ancestors()
                .Prepend(list)
                .FirstOrDefault(node => node.Attributes["data-set"] != null);
            var label = labelNode == null ? null : html.Attribute(labelNode, "data-set");
            var set = new ProviderSetObservation
            {
                Ordinal = sets.Count + 1,
                Evidence = boundary,
                Label = label,
            };
            sets.Add(set);
            foreach (var item in list.ChildNodes.Where(node => node.Name == "li"))
            {
                var songLink = item.Descendants("a")
                    .FirstOrDefault(node => HtmlSongKey(node, "jerrygarcia.com") != null);
                var titleNode = songLink ?? item;
                var title = html.Text(titleNode);
                if (title == null)
                {
                    continue;
                }
                if (songLink == null)
                {
                    title = title with { Value = title.Value.TrimEnd('>', '→', '*', ' ') };
                }
                if (title.Value.Length == 0)
                {
                    reader.Issue("empty_passage_title", "");
                    continue;
                }

                var annotations = new List<ProviderTextObservation>();
                var rawItem = html.Text(item);
                if (rawItem?.Value.EndsWith('*') == true)
                {
                    var annotation = html.MarkupText("annotation_marker", item);
                    if (annotation != null)
                    {
                        annotations.Add(annotation);
                    }
                }
                var key = songLink == null ? null : HtmlSongKey(songLink, "jerrygarcia.com");
                var identity =
                    key == null ? null : html.Identity(songLink!, "jerrygarcia.com", "song", key);
                passages.Add(
                    new()
                    {
                        Ordinal = passages.Count + 1,
                        SetOrdinal = set.Ordinal,
                        SetLabel = label,
                        Title = title,
                        SongIdentities = identity == null ? [] : [identity],
                        TransitionMarker =
                            songLink == null
                                ? html.TrailingTransition(item)
                                : html.TransitionAfter(songLink, html.End(item)),
                        Annotations = annotations,
                    }
                );
            }
        }

        return (sets, passages);
    }

    private static ProviderDecodeResult DecodePhishNetShows(ProviderObservationReader reader)
    {
        var rows = PhishNetRows(reader);
        var editions = new List<ProviderSetlistObservation>();
        var venues = new List<ProviderVenueObservation>();
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var pointer = "/data/" + index.ToString(CultureInfo.InvariantCulture);
            if (row.ValueKind != JsonValueKind.Object)
            {
                reader.Issue("expected_show_object", pointer);
                continue;
            }
            var showIdentity = row.TryGetProperty("showid", out var id)
                ? reader.Identity(id, pointer + "/showid", "phish.net", "show")
                : null;
            var origin = reader.Origin(
                showIdentity?.Identity ?? reader.Input.Resource,
                pointer,
                ProviderObservationScope.CatalogMember
            );
            var performerName = reader.String(row, "artist_name", pointer);
            var performerId = row.TryGetProperty("artistid", out var artistId)
                ? reader.Identity(artistId, pointer + "/artistid", "phish.net", "artist")
                : null;
            var venueName = reader.String(row, "venue", pointer);
            var venueId = row.TryGetProperty("venueid", out var venueIdValue)
                ? reader.Identity(venueIdValue, pointer + "/venueid", "phish.net", "venue")
                : null;
            var venue =
                venueName == null && venueId == null
                    ? null
                    : new ProviderVenueObservation
                    {
                        Origin = origin,
                        Names = venueName == null ? [] : [venueName],
                        Identities = venueId == null ? [] : [venueId],
                        City = reader.String(row, "city", pointer),
                        Region = reader.String(row, "state", pointer),
                        Country = reader.String(row, "country", pointer),
                    };
            if (venue != null)
            {
                venues.Add(venue);
            }
            var notes = reader.String(row, "setlist_notes", pointer);
            editions.Add(
                new()
                {
                    Origin = origin,
                    Performers =
                        performerName == null && performerId == null
                            ? []
                            :
                            [
                                new()
                                {
                                    Name = performerName,
                                    Identities = performerId == null ? [] : [performerId],
                                },
                            ],
                    LiteralDate = reader.String(row, "showdate", pointer),
                    Venue = venue,
                    Ordered = false,
                    Text = notes == null ? [] : [new("setlist_notes", notes)],
                }
            );
        }
        return reader.Result() with { Setlists = editions, Venues = venues };
    }

    private static ProviderDecodeResult DecodePhishNetReviews(ProviderObservationReader reader)
    {
        var rows = PhishNetRows(reader);
        // A review and its author are retained evidence, but do not assert a recording
        // credit or an independent performance account. No suitable review domain exists here.
        for (var index = 0; index < rows.Length; index++)
        {
            reader.Issue(
                "review_is_not_recording_credit",
                "/data/" + index.ToString(CultureInfo.InvariantCulture)
            );
        }
        return reader.Result();
    }

    private static JsonElement[] PhishNetRows(ProviderObservationReader reader)
    {
        if (
            reader.Root.ValueKind != JsonValueKind.Object
            || !reader.Root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
        )
        {
            reader.Issue("expected_phishnet_data_array", "");
            return [];
        }
        if (
            reader.Root.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.True
        )
        {
            reader.Issue("provider_error", "/error");
            return [];
        }
        return data.EnumerateArray().ToArray();
    }

    private static ProviderDecodeResult DecodePhishNetHtml(ProviderObservationReader reader)
    {
        var html = ReadHtml(reader);
        if (html == null)
        {
            return reader.Result();
        }

        var editions = new List<ProviderSetlistObservation>();
        foreach (var container in html.NodesWithClass(html.Root, "setlist-container"))
        {
            var edition = ReadPhishNetHtmlEdition(reader, html, container);
            if (edition != null)
            {
                editions.Add(edition);
            }
        }
        return reader.Result() with
        {
            Setlists = editions,
            Venues = editions
                .Where(edition => edition.Venue != null)
                .Select(edition => edition.Venue!)
                .ToArray(),
        };
    }

    private static ProviderSetlistObservation? ReadPhishNetHtmlEdition(
        ProviderObservationReader reader,
        HtmlObservationSource html,
        HtmlNode container
    )
    {
        var body = html.NodesWithClass(container, "setlist-body").FirstOrDefault();
        if (body == null)
        {
            reader.Issue("missing_setlist_body", "");
            return null;
        }

        var dateHeader = html.NodesWithClass(container, "setlist-date").FirstOrDefault();
        var links = dateHeader?.Descendants("a").ToArray() ?? [];
        var performer = links.Length > 0 ? html.Text(links[0]) : null;
        var permalink = links
            .Select(link => PhishNetSetlistPermalink(link.GetAttributeValue("href", "")))
            .FirstOrDefault(path => path != null);
        if (permalink == null)
        {
            reader.Issue("missing_setlist_permalink", "");
        }
        var item = !string.IsNullOrWhiteSpace(permalink)
            ? new NormalizationResourceKey("phish.net", "setlist_page", permalink)
            : reader.Input.Resource;
        var origin = new ProviderObservationOrigin(
            item,
            html.Evidence(container) ?? reader.Evidence(""),
            ProviderObservationScope.PageMember
        );
        var venue = PhishNetHtmlVenue(html, container, origin);
        var (sets, passages) = ReadPhishNetHtmlSequence(html, body);
        var dateAnchor = container
            .Descendants("a")
            .FirstOrDefault(node =>
                Regex.IsMatch(node.GetAttributeValue("name", ""), @"^\d{4}-\d{2}-\d{2}$")
            );
        var text = ReadPhishNetHtmlNotes(html, container);

        return new()
        {
            Origin = origin,
            Performers = performer == null ? [] : [new() { Name = performer }],
            LiteralDate = dateAnchor == null ? null : html.Attribute(dateAnchor, "name"),
            Venue = venue,
            Ordered = true,
            Sets = sets,
            Passages = passages,
            Text = text,
            Footnotes = text.Where(note => note.Field == "footnotes")
                .Select(note => new ProviderFootnoteDefinition(null, note))
                .ToArray(),
        };
    }

    private static (
        IReadOnlyList<ProviderSetObservation> Sets,
        IReadOnlyList<ProviderPassageObservation> Passages
    ) ReadPhishNetHtmlSequence(HtmlObservationSource html, HtmlNode body)
    {
        var sets = new List<ProviderSetObservation>();
        var passages = new List<ProviderPassageObservation>();
        var songNodes = html.NodesWithClass(body, "setlist-song").ToArray();
        var labels = html.NodesWithClass(body, "set-label").ToArray();
        for (var labelIndex = 0; labelIndex < labels.Length; labelIndex++)
        {
            var text = html.Text(labels[labelIndex]);
            if (text != null)
            {
                sets.Add(
                    new()
                    {
                        Ordinal = labelIndex + 1,
                        Evidence = text.Evidence,
                        Label = text,
                    }
                );
            }
        }
        for (var index = 0; index < songNodes.Length; index++)
        {
            var node = songNodes[index];
            var title = html.Text(node);
            if (title == null)
            {
                continue;
            }
            var setIndex = Array.FindLastIndex(
                labels,
                label => label.StreamPosition < node.StreamPosition
            );
            var label = setIndex < 0 ? null : html.Text(labels[setIndex]);
            var setOrdinal =
                setIndex < 0 || !sets.Any(set => set.Ordinal == setIndex + 1)
                    ? (int?)null
                    : setIndex + 1;
            var key = HtmlSongKey(node, "phish.net");
            var identity = key == null ? null : html.Identity(node, "phish.net", "song_slug", key);
            var limit =
                index + 1 < songNodes.Length ? songNodes[index + 1].StreamPosition : html.End(body);
            var annotations = html.AnnotationsAfter(node, limit);
            passages.Add(
                new()
                {
                    Ordinal = index + 1,
                    SetOrdinal = setOrdinal,
                    SetLabel = label,
                    Title = title,
                    SongIdentities = identity == null ? [] : [identity],
                    TransitionMarker = html.TransitionAfter(node, limit),
                    Annotations = annotations,
                }
            );
        }
        return (sets, passages);
    }

    private static IReadOnlyList<ProviderTextObservation> ReadPhishNetHtmlNotes(
        HtmlObservationSource html,
        HtmlNode container
    )
    {
        var texts = new List<ProviderTextObservation>();
        foreach (var kind in new[] { "setlist-footer", "setlist-notes" })
        {
            foreach (var node in html.NodesWithClass(container, kind))
            {
                var text = html.MarkupText(
                    kind == "setlist-footer" ? "footnotes" : "setlist_notes",
                    node
                );
                if (text != null)
                {
                    texts.Add(text);
                }
            }
        }
        return texts;
    }

    private static ProviderVenueObservation? PhishNetHtmlVenue(
        HtmlObservationSource html,
        HtmlNode container,
        ProviderObservationOrigin origin
    )
    {
        var venueNode = html.NodesWithClass(container, "setlist-venue").FirstOrDefault();
        var venueLink = venueNode?.Descendants("a").FirstOrDefault();
        if (venueNode == null)
        {
            return null;
        }
        // Responsive markup repeats the displayed venue in two spans.
        var nameNode = venueLink?.Descendants("span").FirstOrDefault() ?? venueLink ?? venueNode;
        var name = html.Text(nameNode);
        var key =
            venueLink == null
                ? null
                : Regex.Match(
                    venueLink.GetAttributeValue("href", ""),
                    @"^/venue/(?<id>\d+)(?:/|$)"
                );
        var identity =
            key?.Success == true
                ? html.Identity(venueLink!, "phish.net", "venue", key.Groups["id"].Value)
                : null;
        var location = html.NodesWithClass(container, "setlist-location").FirstOrDefault();
        var city = location
            ?.Descendants("a")
            .FirstOrDefault(node =>
                node.GetAttributeValue("href", "")
                    .StartsWith("/setlists/city/", StringComparison.Ordinal)
            );
        var region = location
            ?.Descendants("a")
            .FirstOrDefault(node =>
                node.GetAttributeValue("href", "")
                    .StartsWith("/setlists/state/", StringComparison.Ordinal)
            );
        return new()
        {
            Origin = origin,
            Names = name == null ? [] : [name],
            Identities = identity == null ? [] : [identity],
            OriginalLocality = location == null ? null : html.Text(location),
            City = city == null ? null : html.Text(city),
            Region = region == null ? null : html.Text(region),
        };
    }

    private static string? HtmlSongKey(HtmlNode node, string provider)
    {
        var path = HtmlProviderPath(node.GetAttributeValue("href", ""), provider);
        var match = path == null ? null : Regex.Match(path, @"^/song/(?<key>[^/]+)/?$");
        return match?.Success == true ? match.Groups["key"].Value : null;
    }

    private static string? PhishNetSetlistPermalink(string href)
    {
        var path = HtmlProviderPath(href, "phish.net");
        return
            path != null && Regex.IsMatch(path, @"^/setlists/[^/]+\.html$", RegexOptions.IgnoreCase)
            ? path
            : null;
    }

    private static string? HtmlProviderPath(string href, string provider)
    {
        if (href.StartsWith("//", StringComparison.Ordinal))
        {
            href = "https:" + href;
        }
        if (href.StartsWith('/'))
        {
            var suffix = href.IndexOfAny(['?', '#']);
            return suffix < 0 ? href : href[..suffix];
        }
        return
            Uri.TryCreate(href, UriKind.Absolute, out var uri)
            && uri.Scheme is "https" or "http"
            && (uri.Host == provider || uri.Host == "www." + provider)
            ? uri.AbsolutePath
            : null;
    }

    private static HtmlObservationSource? ReadHtml(ProviderObservationReader reader)
    {
        if (reader.Root.ValueKind != JsonValueKind.String)
        {
            reader.Issue("expected_html_document", "");
            return null;
        }
        return new(reader, reader.Root.GetString()!);
    }
}
