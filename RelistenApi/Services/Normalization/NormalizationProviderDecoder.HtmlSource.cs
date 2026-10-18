using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

// HAP 1.5 rewrites attribute whitespace in OuterHtml. Citation boundaries must use
// original source positions and decoded input text, including omitted closing tags.
public sealed partial class NormalizationProviderDecoder
{
    /// <summary>Maps DOM selections back to original source, never to rendered text offsets.</summary>
    private sealed class HtmlObservationSource
    {
        private const string TransitionPattern = @"(?<marker>-?(?:>|&gt;|&#0*62;|&#x0*3e;)|→)";
        private readonly ProviderObservationReader reader;
        private readonly string raw;
        private readonly Dictionary<HtmlNode, int?> sourceEnds = new();
        public HtmlNode Root { get; }

        public HtmlObservationSource(ProviderObservationReader reader, string raw)
        {
            this.reader = reader;
            this.raw = raw;
            var document = new HtmlDocument();
            document.LoadHtml(raw);
            Root = document.DocumentNode;
        }

        public IEnumerable<HtmlNode> NodesWithClass(HtmlNode parent, string className) =>
            parent
                .Descendants()
                .Where(node =>
                    node.GetAttributeValue("class", "")
                        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                        .Contains(className)
                );

        public ObservedValue<string>? FirstTextWithClass(HtmlNode parent, string className)
        {
            var node = NodesWithClass(parent, className).FirstOrDefault();
            return node == null ? null : Text(node);
        }

        public int End(HtmlNode node) => SourceEnd(node) ?? -1;

        public NormalizedEvidenceReference? Evidence(HtmlNode node)
        {
            var end = SourceEnd(node);
            if (end == null)
            {
                reader.Issue("html_source_span_unavailable", "");
                return null;
            }
            return Span(node.StreamPosition, end.Value - node.StreamPosition);
        }

        private int? SourceEnd(HtmlNode node)
        {
            if (sourceEnds.TryGetValue(node, out var end))
            {
                return end;
            }
            end = FindSourceEnd(node);
            sourceEnds.Add(node, end);
            return end;
        }

        private int? FindSourceEnd(HtmlNode node)
        {
            var start = node.StreamPosition;
            if (start < 0 || start >= raw.Length)
            {
                return null;
            }
            if (node is HtmlTextNode text)
            {
                return
                    start <= raw.Length - text.Text.Length
                    && raw.AsSpan(start, text.Text.Length).SequenceEqual(text.Text)
                    ? start + text.Text.Length
                    : null;
            }
            if (node.NodeType == HtmlNodeType.Comment)
            {
                var close = raw.IndexOf("-->", start, StringComparison.Ordinal);
                return raw.AsSpan(start).StartsWith("<!--") && close >= 0 ? close + 3 : null;
            }
            if (
                node.NodeType != HtmlNodeType.Element
                || !StartsTag(start, node.Name, closing: false)
            )
            {
                return null;
            }
            var openingEnd = TagEnd(start);
            if (openingEnd == null)
            {
                return null;
            }
            var childEnd = node.LastChild == null ? openingEnd : SourceEnd(node.LastChild);
            if (childEnd == null || childEnd < openingEnd)
            {
                return null;
            }
            var closingStart = childEnd.Value;
            while (closingStart < raw.Length && char.IsWhiteSpace(raw[closingStart]))
            {
                closingStart++;
            }
            // HAP repairs omitted paragraph end tags. Such nodes stop after their last
            // original child; only a matching original end tag extends their citation.
            return StartsTag(closingStart, node.Name, closing: true)
                ? TagEnd(closingStart)
                : childEnd;
        }

        private bool StartsTag(int start, string name, bool closing)
        {
            var prefix = closing ? "</" : "<";
            var length = prefix.Length + name.Length;
            return start <= raw.Length - length
                && raw.AsSpan(start, prefix.Length).SequenceEqual(prefix)
                && raw.AsSpan(start + prefix.Length, name.Length)
                    .Equals(name, StringComparison.OrdinalIgnoreCase)
                && start + length < raw.Length
                && (char.IsWhiteSpace(raw[start + length]) || raw[start + length] is '>' or '/');
        }

        private int? TagEnd(int start)
        {
            char? quote = null;
            for (var index = start + 1; index < raw.Length; index++)
            {
                if (quote != null)
                {
                    if (raw[index] == quote)
                    {
                        quote = null;
                    }
                }
                else if (raw[index] is '\'' or '"')
                {
                    quote = raw[index];
                }
                else if (raw[index] == '>')
                {
                    return index + 1;
                }
            }
            return null;
        }

        public ObservedValue<string>? Text(HtmlNode node)
        {
            var evidence = Evidence(node);
            var text = Regex.Replace(WebUtility.HtmlDecode(node.InnerText), @"\s+", " ").Trim();
            return evidence == null || text.Length == 0 ? null : new(text, evidence);
        }

        public ObservedValue<string>? RawText(HtmlNode node)
        {
            var evidence = Evidence(node);
            return evidence == null ? null : new(evidence.Quote!, evidence);
        }

        public ProviderTextObservation? MarkupText(string field, HtmlNode node)
        {
            var source = RawText(node);
            return source == null
                ? null
                : new(field, source, Text(node)?.Value, ProviderTextFormat.SourceMarkup);
        }

        private ProviderTextObservation? MarkupAttribute(string field, HtmlNode node, string name)
        {
            var source = RawAttribute(node, name);
            return source == null
                ? null
                : new(field, source, Attribute(node, name)?.Value, ProviderTextFormat.SourceMarkup);
        }

        public ObservedValue<string>? Attribute(HtmlNode node, string name)
        {
            var rawAttribute = RawAttribute(node, name);
            return rawAttribute == null
                ? null
                : rawAttribute with
                {
                    Value = WebUtility.HtmlDecode(rawAttribute.Value),
                };
        }

        private ObservedValue<string>? RawAttribute(HtmlNode node, string name)
        {
            var attribute = node.Attributes[name];
            if (attribute == null)
            {
                return null;
            }
            // HAP exposes the original attribute-name position. Start there, rather than
            // searching serialized HTML or matching attribute-like text inside another value.
            var start = attribute.StreamPosition;
            var openingEnd = TagEnd(node.StreamPosition);
            var index = start + attribute.OriginalName.Length;
            if (
                openingEnd == null
                || start < node.StreamPosition
                || index > openingEnd
                || !raw.AsSpan(start, attribute.OriginalName.Length)
                    .Equals(attribute.OriginalName, StringComparison.OrdinalIgnoreCase)
            )
            {
                reader.Issue("html_attribute_span_unavailable", "");
                return null;
            }
            while (index < openingEnd && char.IsWhiteSpace(raw[index]))
            {
                index++;
            }
            if (index == openingEnd || raw[index++] != '=')
            {
                reader.Issue("html_attribute_span_unavailable", "");
                return null;
            }
            while (index < openingEnd && char.IsWhiteSpace(raw[index]))
            {
                index++;
            }
            if (index == openingEnd)
            {
                reader.Issue("html_attribute_span_unavailable", "");
                return null;
            }
            var quote = raw[index] is '\'' or '"' ? raw[index++] : (char?)null;
            var valueStart = index;
            while (
                index < openingEnd
                && (
                    quote == null
                        ? !char.IsWhiteSpace(raw[index]) && raw[index] != '>'
                        : raw[index] != quote
                )
            )
            {
                index++;
            }
            var value = raw[valueStart..index];
            if (
                (quote != null && index == openingEnd)
                || WebUtility.HtmlDecode(value) != WebUtility.HtmlDecode(attribute.Value)
            )
            {
                reader.Issue("html_attribute_span_unavailable", "");
                return null;
            }
            return new(value, Span(valueStart, value.Length));
        }

        public ObservedProviderIdentity? Identity(
            HtmlNode node,
            string provider,
            string resourceType,
            string key
        )
        {
            var evidence = Evidence(node);
            return evidence == null ? null : new(new(provider, resourceType, key), evidence);
        }

        public ObservedValue<string>? TransitionAfter(HtmlNode node, int limit)
        {
            var start = End(node);
            if (start < 0 || limit < start || limit > raw.Length)
            {
                return null;
            }
            var match = Regex.Match(
                raw[start..limit],
                @"\A(?:\s|<sup\b[^>]*>.*?</sup>)*" + TransitionPattern,
                RegexOptions.Singleline | RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1)
            );
            return match.Success
                ? new(
                    WebUtility.HtmlDecode(match.Groups["marker"].Value),
                    Span(start + match.Groups["marker"].Index, match.Groups["marker"].Length)
                )
                : null;
        }

        public ObservedValue<string>? TrailingTransition(HtmlNode node)
        {
            var textNode = node.Descendants()
                .LastOrDefault(child =>
                    child.NodeType == HtmlNodeType.Text
                    && !string.IsNullOrWhiteSpace(child.InnerText)
                );
            if (textNode == null)
            {
                return null;
            }
            var text = RawText(textNode);
            if (text == null)
            {
                return null;
            }
            var match = Regex.Match(
                text.Value,
                TransitionPattern + @"\s*$",
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1)
            );
            return match.Success
                ? new(
                    WebUtility.HtmlDecode(match.Groups["marker"].Value),
                    Span(
                        textNode.StreamPosition + match.Groups["marker"].Index,
                        match.Groups["marker"].Length
                    )
                )
                : null;
        }

        public IReadOnlyList<ProviderTextObservation> AnnotationsAfter(HtmlNode node, int limit)
        {
            var result = new List<ProviderTextObservation>();
            var annotation = node.NextSibling;
            while (annotation != null && annotation.StreamPosition < limit)
            {
                if (annotation.Name == "sup")
                {
                    var text =
                        annotation.Attributes["title"] != null
                            ? MarkupAttribute("footnote", annotation, "title")
                            : MarkupText("footnote", annotation);
                    if (text != null)
                    {
                        result.Add(text);
                    }
                }
                annotation = annotation.NextSibling;
            }
            var tooltip = Attribute(node, "title");
            if (tooltip != null && tooltip.Value != Text(node)?.Value)
            {
                var text = MarkupAttribute("info", node, "title");
                if (text != null)
                {
                    result.Add(text);
                }
            }
            return result;
        }

        private NormalizedEvidenceReference Span(int start, int length) =>
            new(
                reader.Input.Uuid,
                "",
                raw[..start].EnumerateRunes().Count(),
                raw[..(start + length)].EnumerateRunes().Count(),
                raw.Substring(start, length)
            );
    }
}
