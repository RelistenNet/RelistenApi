using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using RelistenUserService.Authentication;

namespace RelistenUserServiceTests;

[TestFixture]
public sealed class TestRefreshTokenEndpointBoundary
{
    [TestCase("/connect/token")]
    [TestCase("/connect/token/")]
    [TestCase("/CONNECT/TOKEN")]
    public void CoversEveryRegisteredTokenEndpointSpelling(string path)
    {
        RefreshTokenReplayMiddleware.IsTokenEndpoint(new PathString(path))
            .Should().BeTrue();
    }

    [TestCase("/connect/token/extra")]
    [TestCase("/connect/tokenized")]
    [TestCase("/connect/authorize")]
    public void DoesNotMatchOtherEndpoints(string path)
    {
        RefreshTokenReplayMiddleware.IsTokenEndpoint(new PathString(path))
            .Should().BeFalse();
    }
}
