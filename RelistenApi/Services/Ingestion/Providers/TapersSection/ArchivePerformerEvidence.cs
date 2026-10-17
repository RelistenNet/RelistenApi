using System;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Relisten.Api.Models;
using Relisten.Data;

namespace Relisten.Ingestion.Providers.TapersSection;

/// <summary>
/// Limits reviewed performer evidence to retained billing fields. Applicability compares whole
/// supporting fields, so unchanged billing survives unrelated file/date updates without silently
/// accepting edited context around an old evidence span.
/// </summary>
internal static class ArchivePerformerEvidence
{
    internal static bool IsValid(JObject projection, TapersSectionEvidence[] evidence) =>
        evidence.Length > 0
        && evidence.All(support =>
        {
            var field = ReadField(projection, support.Locator);
            if (
                field == null
                || string.IsNullOrWhiteSpace(support.Value)
                || string.IsNullOrWhiteSpace(support.Rule)
            )
            {
                return false;
            }

            return support.Start is { } start && support.Length is { } length
                ? start >= 0
                    && length > 0
                    && start <= field.Length - length
                    && field.Substring(start, length) == support.Value
                : support.Start == null && support.Length == null && field == support.Value;
        });

    internal static bool SupportsProject(
        string[] projectLabels,
        TapersSectionEvidence[] evidence
    ) =>
        evidence.Any(support =>
            projectLabels.Any(label =>
                ArtistRegistryService.NormalizeIdentityKey(label)
                == ArtistRegistryService.NormalizeIdentityKey(support.Value)
            )
        );

    internal static bool IsApplicable(ArchivePerformerDecision decision, JObject current)
    {
        var supporting = ArchiveObservationProjector.ParseObject(decision.SupportingPayloadJson);
        if (
            (string?)supporting["metadata"]?["identifier"] != decision.ItemIdentifier
            || (string?)current["metadata"]?["identifier"] != decision.ItemIdentifier
            || !IsValid(supporting, decision.Evidence)
        )
        {
            return false;
        }

        // The original broad creator is a permitted title label for this reviewed item only.
        // A changed creator must not acquire that permission from an unchanged description.
        return JToken.DeepEquals(
                supporting["metadata"]?["creator"],
                current["metadata"]?["creator"]
            )
            && decision.Evidence.All(support =>
                ReadField(supporting, support.Locator) == ReadField(current, support.Locator)
            );
    }

    private static string? ReadField(JObject projection, string locator)
    {
        if (
            !Regex.IsMatch(
                locator,
                @"^metadata\.(?:creator|title|description)(?:\[\d+\])?$",
                RegexOptions.CultureInvariant
            )
        )
        {
            return null;
        }

        var field = projection.SelectToken(locator, false);
        return field?.Type == JTokenType.String ? field.Value<string>() : null;
    }
}
