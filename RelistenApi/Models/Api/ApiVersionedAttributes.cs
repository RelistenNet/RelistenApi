using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Relisten.Api.Models.Api
{
    [AttributeUsage(AttributeTargets.Property)]
    public class V3JsonOnlyAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Property)]
    public class V2JsonOnlyAttribute : Attribute
    {
    }

    public class ApiV3ContractResolver : DefaultContractResolver
    {
        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization serialization)
        {
            var property = base.CreateProperty(member, serialization);

            var v2JsonOnlyAttribute = member.GetCustomAttribute<V2JsonOnlyAttribute>();
            if (v2JsonOnlyAttribute != null)
            {
                property.ShouldSerialize = _ => false;
            }

            return property;
        }
    }

    public sealed class SkipV2PropertySchemaTransformer : IOpenApiSchemaTransformer
    {
        public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context,
            CancellationToken cancellationToken)
        {
            if (schema.Properties == null || context.DocumentName == "v2")
            {
                return Task.CompletedTask;
            }

            var skipProperties = context.JsonTypeInfo.Type.GetProperties()
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

            return Task.CompletedTask;
        }
    }
}
