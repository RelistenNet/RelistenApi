using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using Relisten.Api;
using Relisten.Api.Models;

namespace RelistenApiTests.Popularity;

[TestFixture]
public class TestArtistListApiRequestHandling
{
    [Test]
    public async Task ApiRequest_WhenQueryAllDisabledAndArtistIdsIsEmpty_ShouldReturnCallbackDataWithEmptyArtistList()
    {
        var controller = new TestRelistenBaseController();
        IReadOnlyList<Artist>? callbackArtists = null;

        var result = await controller.CallArtistListApiRequest(Array.Empty<string>(), false, artists =>
        {
            callbackArtists = artists;
            return Task.FromResult("ok");
        });

        callbackArtists.Should().NotBeNull();
        callbackArtists!.Should().BeEmpty();
        result.Should().BeOfType<JsonResult>();
        ((JsonResult)result).Value.Should().Be("ok");
    }

    private class TestRelistenBaseController : RelistenBaseController
    {
        public TestRelistenBaseController() : base(null!, null!, null!)
        {
        }

        public Task<IActionResult> CallArtistListApiRequest<T>(
            IReadOnlyList<string> artistIdsOrSlugs,
            bool queryAllWhenEmpty,
            Func<IReadOnlyList<Artist>, Task<T>> cb)
        {
            return ApiRequest(artistIdsOrSlugs, cb, queryAllWhenEmpty);
        }
    }
}
