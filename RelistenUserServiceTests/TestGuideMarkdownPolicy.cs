using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Relisten.Accounts.Contracts.Identifiers;
using Relisten.Accounts.Contracts.Playlists;
using RelistenUserService.Playlists;

namespace RelistenUserServiceTests;

[TestFixture]
public sealed class TestGuideMarkdownPolicy
{
    private static IEnumerable<TestCaseData> SharedVectors()
    {
        using var fixture = JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(
                    TestContext.CurrentContext.TestDirectory,
                    "Fixtures/playlists/guide-markdown-vectors.json"
                )
            )
        );

        foreach (var vector in fixture.RootElement.GetProperty("vectors").EnumerateArray())
        {
            yield return new TestCaseData(
                vector.GetProperty("markdown").GetString(),
                vector.GetProperty("server").GetString() switch
                {
                    "accept" => true,
                    "reject" => false,
                    var other => throw new InvalidDataException($"Unknown verdict {other}."),
                }
            ).SetName($"Shared vector: {vector.GetProperty("name").GetString()}");
        }
    }

    [TestCaseSource(nameof(SharedVectors))]
    public void Matches_the_shared_verdict(string markdown, bool accepted)
    {
        var violation = GuideMarkdownPolicy.Violation(markdown);

        if (accepted)
        {
            violation.Should().BeNull();
        }
        else
        {
            violation
                .Should()
                .BeOneOf(
                    GuideMarkdownPolicy.HtmlRefusal,
                    GuideMarkdownPolicy.ImageRefusal,
                    GuideMarkdownPolicy.LinkRefusal
                );
        }
    }

    [Test]
    public void Refuses_nesting_deeper_than_the_parser_allows()
    {
        GuideMarkdownPolicy
            .Violation(new string('>', 2_000) + " x")
            .Should()
            .Be(GuideMarkdownPolicy.NestingRefusal);
        GuideMarkdownPolicy
            .Violation(string.Concat(Enumerable.Repeat("- ", 1_000)) + "x")
            .Should()
            .Be(GuideMarkdownPolicy.NestingRefusal);
    }

    [Test]
    public void Refuses_HTML_in_a_playlist_description_update()
    {
        var update = new PlaylistOperation(
            PlaylistOperationId.New(),
            1,
            "update_metadata",
            new UpdatePlaylistMetadata(
                Description: new(true, "Hello <img src=\"https://tracker.example/p.gif\">")
            )
        );

        var refusal = () => PlaylistValidation.Normalize(new PlaylistBatchRequest(1, [update]));

        refusal
            .Should()
            .Throw<PlaylistException>()
            .Where(error =>
                error.Status == 422 && error.Message == GuideMarkdownPolicy.HtmlRefusal
            );
    }

    [Test]
    public void Refuses_an_image_in_a_create_request_before_storing_anything()
    {
        var request = new CreatePlaylistRequest(
            1,
            CommandId.New(),
            PlaylistId.Parse("playlist_01k8000000e008000000000001"),
            new("Guide"),
            [
                new(
                    PlaylistSegmentId.Parse("playlist_segment_01k8000000e008000000000002"),
                    [],
                    "Great show ![pixel](https://tracker.example/p.gif)"
                ),
            ]
        );

        var refusal = () => PlaylistValidation.Normalize(request);

        refusal
            .Should()
            .Throw<PlaylistException>()
            .Where(error =>
                error.Status == 422
                && error.Code == "invalid_playlist_operation"
                && error.Message == GuideMarkdownPolicy.ImageRefusal
            );
    }
}
