using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Relisten.UserApi.Controllers;
using Relisten.UserApi.Models;

namespace RelistenUserApiTests;

[TestFixture]
public class UserLibraryRouteContractTests
{
    [Test]
    public void UsersController_ShouldExposeV3LibraryUsersMe()
    {
        typeof(UsersController).GetCustomAttribute<RouteAttribute>()!
            .Template
            .Should()
            .Be("api/v3/library/users");

        typeof(UsersController).GetCustomAttribute<AuthorizeAttribute>()
            .Should()
            .NotBeNull();

        var method = typeof(UsersController).GetMethod(nameof(UsersController.CurrentUser));
        method.Should().NotBeNull();

        method!.GetCustomAttribute<HttpGetAttribute>()!
            .Template
            .Should()
            .Be("me");

        method.GetCustomAttributes<ProducesResponseTypeAttribute>()
            .Single(attribute => attribute.StatusCode == 200)
            .Type
            .Should()
            .Be(typeof(CurrentUserResponse));
    }
}
