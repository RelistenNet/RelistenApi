using System;
using System.Reflection;
using FluentAssertions;
using Relisten.Services.Popularity;

namespace RelistenApiTests.Popularity;

[TestFixture]
public class TestPopularityJobExpiration
{
    [Test]
    public void PopularityJobs_ShouldExpireFiveMinutesAfterSuccess()
    {
        var expiration = typeof(PopularityJobs).GetCustomAttribute<SuccessExpirationAttribute>();

        expiration.Should().NotBeNull();
        expiration!.ExpirationTimeout.Should().Be(TimeSpan.FromMinutes(5));
    }
}
