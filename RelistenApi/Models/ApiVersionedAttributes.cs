using System;
using System.Linq;
using System.Reflection;
using Microsoft.OpenApi.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Relisten.Api.Models.Api;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Relisten.Models;

public class ApiV3ContractResolver : DefaultContractResolver
{
    protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization serialization)
    {
        var property = base.CreateProperty(member, serialization);

        // Check if the property has a JsonRestricted attribute
        var v2JsonOnlyAttribute = member.GetCustomAttribute<V2JsonOnlyAttribute>();
        if (v2JsonOnlyAttribute != null)
        {
            property.ShouldSerialize = _ => false;
        }

        return property;
    }
}

public class SwaggerSkipV2PropertyFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema?.Properties == null || context.DocumentName == "v2")
        {
            return;
        }

        var skipProperties = context.Type.GetProperties()
            .Where(t => t.GetCustomAttribute<V2JsonOnlyAttribute>() != null);

        foreach (var skipProperty in skipProperties)
        {
            var propertyToSkip = schema.Properties.Keys.SingleOrDefault(x =>
                string.Equals(x, skipProperty.Name, StringComparison.OrdinalIgnoreCase));

            if (propertyToSkip != null)
            {
                schema.Properties.Remove(propertyToSkip);
            }
        }
    }
}
