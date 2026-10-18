using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;
using DomainInput = RelistenApiTests.Services.NormalizationDomainFixture.DomainInput;

namespace RelistenApiTests.Services;

/// <summary>Optional title detail cannot make accepted content pages or their playback references unavailable.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationTrackTitleFactsReads
{
    // The leading guitar uses two UTF-16 code units but one evidence code point.
    private const string SourceTitle = "🎸 Jam (partial)";
    private NormalizationDomainFixture fixture = null!;
    private NormalizedMusicService music = null!;
    private NormalizationReadService reads = null!;
    private AcceptedImage oversized = null!;

    [OneTimeSetUp]
    public async Task Start()
    {
        fixture = new();
        music = new(fixture.Db, fixture.Work);
        reads = new(fixture.Db, Options.Create(new NormalizationReadOptions { Enabled = true }));
        oversized = await AcceptImage(oversizedSecondDetail: true);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task OversizedOptionalDetailsKeepTheContentPageAndPlayableReferences()
    {
        var page = (await reads.ListTrackContentsAsync(oversized.Source.TrackUuid))!;

        page.items.Select(row => row.uuid)
            .Should()
            .Equal(oversized.Contents.Select(value => value.Uuid));
        page.items.Should()
            .OnlyContain(row => row.title_facts == null && row.title_facts_status == "size_limit");
        page.items.Should()
            .OnlyContain(row =>
                row.reference_available && row.source_track_uuid == oversized.Source.TrackUuid
            );
        page.items.Select(row => row.original_text).Should().Equal("Jam", "Jam");
        page.items.Should().OnlyContain(row => row.start_ms == null && row.end_ms == null);
        page.items.Should()
            .OnlyContain(row => row.evidence_status == "current" && row.evidence.Count == 1);
        page.next_cursor.Should().BeNull();
    }

    [Test]
    public async Task OversizedLookaheadIsExcludedFromTheVisiblePagesDetailBudget()
    {
        var first = (await reads.ListTrackContentsAsync(oversized.Source.TrackUuid, limit: 1))!;

        var visible = first.items.Should().ContainSingle().Which;
        visible.uuid.Should().Be(oversized.Contents[0].Uuid);
        visible.title_facts_status.Should().Be("available");
        visible.title_facts!.coverage.Should().Be("partial");
        visible
            .title_facts.qualifiers.Should()
            .ContainSingle()
            .Which.kind.Should()
            .Be("partial_coverage");
        visible.reference_available.Should().BeTrue();
        first.next_cursor.Should().NotBeNull();

        var second = (
            await reads.ListTrackContentsAsync(
                oversized.Source.TrackUuid,
                limit: 1,
                cursor: first.next_cursor
            )
        )!;

        var limited = second.items.Should().ContainSingle().Which;
        limited.uuid.Should().Be(oversized.Contents[1].Uuid);
        limited.title_facts.Should().BeNull();
        limited.title_facts_status.Should().Be("size_limit");
        limited.reference_available.Should().BeTrue();
        second.next_cursor.Should().BeNull();
    }

    [Test]
    public async Task ChangedInputHeadKeepsAcceptedFactsAndMarksTheirOriginalCitationsChanged()
    {
        var image = await AcceptImage(oversizedSecondDetail: false);
        await fixture.Refresh(image.Input, "🎸 Jam (complete)");

        var page = (await reads.ListTrackContentsAsync(image.Source.TrackUuid))!;

        page.items.Should().HaveCount(2);
        foreach (var row in page.items)
        {
            row.title_facts_status.Should().Be("available");
            row.title_facts!.coverage.Should().Be("partial");
            row.title_facts.transition_after.Should().Be("unknown");
            var source = row.title_facts.source_evidence!;
            source.input_revision_uuid.Should().Be(image.Input.Reference.InputRevisionUuid);
            source.json_pointer.Should().Be("/text");
            source.start_code_point.Should().Be(0);
            source.end_code_point.Should().Be(15);
            source.quote.Should().Be(SourceTitle);
            source.evidence_status.Should().Be("changed");
            var qualifier = row.title_facts.qualifiers.Should().ContainSingle().Which.evidence;
            qualifier.input_revision_uuid.Should().Be(image.Input.Reference.InputRevisionUuid);
            qualifier.start_code_point.Should().Be(6);
            qualifier.end_code_point.Should().Be(15);
            qualifier.quote.Should().Be("(partial)");
            qualifier.evidence_status.Should().Be("changed");
            row.reference_available.Should().BeTrue();
            row.start_ms.Should().BeNull();
            row.end_ms.Should().BeNull();
        }
    }

    private async Task<AcceptedImage> AcceptImage(bool oversizedSecondDetail)
    {
        var source = await fixture.Source();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                WITH performance AS (
                    INSERT INTO shows(artist_id,date,display_date,uuid,updated_at,performance_key,is_legacy_survivor)
                    VALUES(@ArtistId,'2026-10-17','2026-10-17',@show,now(),'default',true) RETURNING id
                )
                UPDATE sources SET show_id=performance.id FROM performance WHERE uuid=@SourceUuid
                """,
                new
                {
                    source.ArtistId,
                    source.SourceUuid,
                    show = Guid.NewGuid(),
                }
            )
        );
        var input = await fixture.Capture(SourceTitle, "songs");
        var owner = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("songs", "recording", source.SourceUuid),
            "title-facts-read-fixture-v1",
            [input.Resource],
            []
        );
        var qualifier = input.Reference with
        {
            StartCodePoint = 6,
            EndCodePoint = 15,
            Quote = "(partial)",
        };
        var facts = new TrackContentTitleFacts(
            TitleCoverage.Partial,
            TitleTransition.Unknown,
            [new("partial_coverage", qualifier)],
            input.Reference
        );
        // Qualifier labels are opaque reviewed metadata. Enlarge this one optional label while
        // retaining a short, validated real citation; no provider body or source history is forged.
        var secondFacts = oversizedSecondDetail
            ? facts with
            {
                Qualifiers = [new(new string('q', 32 * 1024 * 1024 + 1), qualifier)],
            }
            : facts;
        var contents = new[]
        {
            Content(source.TrackUuid, 1, input, facts),
            Content(source.TrackUuid, 2, input, secondFacts),
        };
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            owner.SubjectUuid,
            owner.EditorialRevision,
            "accept",
            new() { TrackContents = contents },
            "Reviewed source title annotations for public detail projection",
            "title-facts-fixture"
        );
        return new(source, input, contents);
    }

    private static TrackContentChange Content(
        Guid track,
        int position,
        DomainInput input,
        TrackContentTitleFacts facts
    ) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            SourceTrackUuid = track,
            Position = position,
            Kind = "jam",
            OriginalText = "Jam",
            Evidence = [input.Reference],
            TitleFacts = facts,
        };

    private sealed record AcceptedImage(
        CatalogSource Source,
        DomainInput Input,
        IReadOnlyList<TrackContentChange> Contents
    );
}
