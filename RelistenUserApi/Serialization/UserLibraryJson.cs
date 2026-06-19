using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Relisten.UserApi.Serialization;

public static class UserLibraryJson
{
    public static readonly JsonSerializerSettings SerializerSettings = Apply(new JsonSerializerSettings());

    public static JsonSerializerSettings Apply(JsonSerializerSettings settings)
    {
        settings.DateFormatString = "yyyy'-'MM'-'dd'T'HH':'mm':'ssK";
        settings.DateTimeZoneHandling = DateTimeZoneHandling.Utc;
        settings.ContractResolver = new DefaultContractResolver
        {
            NamingStrategy = new SnakeCaseNamingStrategy()
        };

        return settings;
    }
}
