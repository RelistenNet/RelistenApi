using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Relisten.UserApi.Models;
using Relisten.UserApi.Serialization;

namespace RelistenUserApiTests;

[TestFixture]
public class UserLibrarySerializationTests
{
    [Test]
    public void CurrentUserResponse_ShouldSerializeAsSnakeCase()
    {
        var response = new CurrentUserResponse
        {
            UserUuid = Guid.Parse("10000000-0000-0000-0000-000000000001"),
            DisplayName = "A Relisten User",
            Username = "a-relisten-user",
            ScopeId = "user:10000000-0000-0000-0000-000000000001"
        };

        var json = JsonConvert.SerializeObject(response, UserLibraryJson.SerializerSettings);
        var parsed = JObject.Parse(json);

        parsed["user_uuid"]!.Value<string>().Should().Be("10000000-0000-0000-0000-000000000001");
        parsed["display_name"]!.Value<string>().Should().Be("A Relisten User");
        parsed["scope_id"]!.Value<string>().Should().Be("user:10000000-0000-0000-0000-000000000001");
        parsed["UserUuid"].Should().BeNull();
        parsed["DisplayName"].Should().BeNull();
        parsed["ScopeId"].Should().BeNull();
    }
}
