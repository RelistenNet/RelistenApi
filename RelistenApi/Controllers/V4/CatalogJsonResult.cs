using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Relisten.Api.Models.Api;
using Relisten.Identifiers;

namespace Relisten.Controllers.V4;

/// <summary>Publishes exactly the JSON used for cache validation, after snapshot disposal.</summary>
internal sealed class CatalogJsonResult(object resource) : IActionResult
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        ContractResolver = new DefaultContractResolver
        {
            NamingStrategy = new SnakeCaseNamingStrategy(),
        },
        Converters = { new PublicIdConverter(), new NormalizationEnumConverter() },
        DateTimeZoneHandling = DateTimeZoneHandling.Utc,
    };

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(resource, Settings));
        var etag = new EntityTagHeaderValue(
            "\"" + Convert.ToHexStringLower(SHA256.HashData(bytes)) + "\"",
            isWeak: true
        );
        var response = context.HttpContext.Response;
        response.Headers.ETag = etag.ToString();
        response.Headers.CacheControl = "public, no-cache";
        var validators = context.HttpContext.Request.GetTypedHeaders().IfNoneMatch;
        if (validators is not null)
        {
            foreach (var validator in validators)
            {
                if (
                    validator == EntityTagHeaderValue.Any
                    || validator.Compare(etag, useStrongComparison: false)
                )
                {
                    response.StatusCode = StatusCodes.Status304NotModified;
                    return;
                }
            }
        }

        response.ContentType = "application/json; charset=utf-8";
        await response.Body.WriteAsync(bytes, context.HttpContext.RequestAborted);
    }

    // These DTOs are outbound only. Each route parses its expected ID kind explicitly.
    private sealed class PublicIdConverter : JsonConverter
    {
        public override bool CanRead => false;

        public override bool CanConvert(Type type)
        {
            var valueType = Nullable.GetUnderlyingType(type) ?? type;
            return typeof(IUuidCatalogId).IsAssignableFrom(valueType)
                || valueType == typeof(Relisten.Normalization.VenueId)
                || valueType == typeof(Relisten.Normalization.SongId);
        }

        public override void WriteJson(
            JsonWriter writer,
            object? value,
            JsonSerializer serializer
        ) => writer.WriteValue(value?.ToString());

        public override object ReadJson(
            JsonReader reader,
            Type type,
            object? existing,
            JsonSerializer serializer
        ) => throw new NotSupportedException();
    }
}
