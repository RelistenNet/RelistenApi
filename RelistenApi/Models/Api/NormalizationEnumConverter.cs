using System;
using Newtonsoft.Json.Converters;
using Relisten.Normalization;

namespace Relisten.Api.Models.Api;

/// <summary>
/// Adapts the shared normalization vocabulary to Newtonsoft without changing legacy enum contracts.
/// The normalization library owns the EnumMember tokens and its System.Text.Json support.
/// </summary>
public sealed class NormalizationEnumConverter : StringEnumConverter
{
    public NormalizationEnumConverter()
    {
        AllowIntegerValues = false;
    }

    public override bool CanConvert(Type type) =>
        NormalizationEnums.IsSupported(Nullable.GetUnderlyingType(type) ?? type);
}
