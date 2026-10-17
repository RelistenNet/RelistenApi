using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>
/// Shared scalar reading and citation rules for provider decoders. Provider-specific
/// structure and field meaning belong to each decoder, not to this reader.
/// </summary>
internal sealed class ProviderObservationReader(
    NormalizationInputRevision input,
    CancellationToken cancellationToken
) : IDisposable
{
    private readonly NormalizationRetainedDocument document = NormalizationRetainedDocument.Open(
        input,
        cancellationToken
    );
    private readonly List<ProviderDecodeIssue> issues = [];

    public NormalizationInputRevision Input => input;
    public JsonElement Root
    {
        get
        {
            cancellationToken.ThrowIfCancellationRequested();
            return document.Resolve("");
        }
    }

    public NormalizedEvidenceReference Evidence(string pointer)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(input.Uuid, pointer);
    }

    public ProviderObservationOrigin Origin(
        NormalizationResourceKey item,
        string pointer,
        ProviderObservationScope scope
    ) => new(item, Evidence(pointer), scope);

    public void Issue(string code, string pointer) => issues.Add(new(code, Evidence(pointer)));

    public ProviderDecodeResult Result() =>
        new()
        {
            InputRevisionUuid = input.Uuid,
            Resource = input.Resource,
            DecoderVersion = NormalizationProviderDecoder.Version,
            Issues = issues.ToArray(),
        };

    public ObservedValue<string>? String(JsonElement value, string pointer)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            Issue("expected_text", pointer);
            return null;
        }
        return new(value.GetString()!, Evidence(pointer));
    }

    public ObservedValue<string>? String(JsonElement parent, string property, string pointer) =>
        parent.TryGetProperty(property, out var value)
            ? String(value, Pointer(pointer, property))
            : null;

    public IReadOnlyList<ObservedValue<string>> Strings(JsonElement value, string pointer)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            var single = String(value, pointer);
            return single == null ? [] : [single];
        }

        var result = new List<ObservedValue<string>>();
        var index = 0;
        foreach (var entry in value.EnumerateArray())
        {
            var text = String(
                entry,
                Pointer(pointer, index++.ToString(CultureInfo.InvariantCulture))
            );
            if (text != null)
            {
                result.Add(text);
            }
        }
        return result;
    }

    public ObservedValue<int>? Integer(JsonElement value, string pointer)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return new(number, Evidence(pointer));
        }
        Issue("expected_integer", pointer);
        return null;
    }

    public ObservedValue<double>? Number(JsonElement value, string pointer)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (
            value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number)
            && double.IsFinite(number)
        )
        {
            return new(number, Evidence(pointer));
        }
        Issue("expected_number", pointer);
        return null;
    }

    public ObservedProviderIdentity? Identity(
        JsonElement value,
        string pointer,
        string provider,
        string resourceType
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        string? key = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (
            value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
            && number > 0
        )
        {
            key = number.ToString(CultureInfo.InvariantCulture);
        }
        if (string.IsNullOrWhiteSpace(key))
        {
            Issue("invalid_provider_identity", pointer);
            return null;
        }
        return new(new(provider, resourceType, key), Evidence(pointer));
    }

    public IReadOnlyList<ProviderTextObservation> TextFields(
        JsonElement parent,
        string pointer,
        IReadOnlyDictionary<string, string> fields
    )
    {
        var result = new List<ProviderTextObservation>();
        foreach (var field in fields)
        {
            if (parent.TryGetProperty(field.Key, out var value))
            {
                result.AddRange(
                    Strings(value, Pointer(pointer, field.Key))
                        .Select(text => new ProviderTextObservation(field.Value, text))
                );
            }
        }
        return result;
    }

    public static string Pointer(string parent, string property) =>
        parent + "/" + property.Replace("~", "~0").Replace("/", "~1");

    public void Dispose() => document.Dispose();
}
