using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Relisten.Api.Models.Normalization;
using Relisten.Controllers;
using Relisten.Data;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

/// <summary>Accepted source accounts expose typed, bounded annotations with original evidence and explicit correspondence.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationSetlistAnnotations
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationReadService reads = null!;
    private readonly NormalizationReadOptions options = new() { Enabled = true };

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        reads = new(fixture.Db, Options.Create(options));
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task PagesPreserveSourceSpecificDuplicateDefinitionsMissingReferencesAndUnmatchedPassages()
    {
        var context = await Snapshot();
        var account = (await reads.ListSetlistAnnotationsAsync(context.Edition, 1))!;
        var group = account.items.Should().ContainSingle().Which;
        group.scope.Should().Be("account");
        group.origin!.identity.Should().Be(context.Account.Origin.Item);
        var footnotes = group.footnotes!;
        footnotes.Select(note => note.number).Should().Equal(2, 2, null);
        footnotes
            .Select(note => note.note.text)
            .Should()
            .Equal("First guest", "Conflicting guest", "Unnumbered note");
        group.performers!.Single().relationship.Should().Be("reported_performer");
        group.notes!.Should().Contain(note => note.text == "Before the show");
        var sets = (
            await reads.ListSetlistAnnotationsAsync(context.Edition, 2, account.next_cursor)
        )!;
        sets.items.Select(item => item.scope).Should().Equal("set", "set");
        sets.items[0].set_uuids.Should().Equal(context.Set);
        sets.items[0].notes!.Single().text.Should().Be("Set note");
        sets.items[1].detail_status.Should().Be("not_captured");
        sets.items[1].notes.Should().BeNull();
        var passages = (
            await reads.ListSetlistAnnotationsAsync(context.Edition, 2, sets.next_cursor)
        )!;
        var mapped = passages.items[0];
        mapped.scope.Should().Be("passage");
        mapped.correspondence.Should().Be("mapped");
        mapped.occurrence_uuids.Should().Equal(context.Targets);
        var references = mapped.footnote_references!;
        references.Select(reference => reference.resolution).Should().Equal("ambiguous", "missing");
        references[0].definition_indexes.Should().Equal(0, 1);
        references[1].definition_indexes.Should().BeEmpty();
        mapped.segue!.value.Should().BeFalse();
        references[0].evidence.json_pointer.Should().Be("/data/sets/0/songs/0/footnotes/0");
        passages.items[1].correspondence.Should().Be("unresolved");
        passages.items[1].passage_title!.text.Should().Be("Unknown tune");
        passages.items[1].occurrence_uuids.Should().BeEmpty();
        passages.next_cursor.Should().BeNull();
        mapped.prerecorded_playback.Should().BeNull();
        group.prerecorded_playback.Should().BeNull();
    }

    [Test]
    public async Task SmallPagesVisitEveryOffPagePassageAndKeepUnresolvedCorrespondence()
    {
        var context = await Snapshot(passageCount: 40);
        var items = new List<NormalizedSetlistAnnotationGroup>();
        string? cursor = null;
        do
        {
            var page = (await reads.ListSetlistAnnotationsAsync(context.Edition, 7, cursor))!;
            items.AddRange(page.items);
            cursor = page.next_cursor;
        } while (cursor != null);
        items
            .Select(group => group.index)
            .Should()
            .Equal(Enumerable.Range(0, 43).Select(index => (long)index));
        var passages = items.Where(group => group.scope == "passage").ToArray();
        passages
            .Select(group => group.source_passage_uuid)
            .Should()
            .Equal(context.Passages.Select(passage => (Guid?)passage.ProposalUuid));
        passages[0].correspondence.Should().Be("mapped");
        passages.Skip(1).Should().OnlyContain(group => group.correspondence == "unresolved");
        passages.Should().OnlyContain(group => group.footnote_references!.Count == 2);
    }

    [Test]
    public async Task OriginalEvidenceSurvivesProviderChangesWithoutBorrowingWholeAccountProse()
    {
        var context = await Snapshot();
        var first = (await reads.ListSetlistAnnotationsAsync(context.Edition))!.items;
        var citation = first[0].footnotes![0].note.evidence;
        citation.evidence_status.Should().Be("current");
        citation.quote.Should().BeNull();
        citation.json_pointer.Should().Be("/data/footnotes/0/note");
        await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                context.Input.Resource,
                JsonSerializer.SerializeToUtf8Bytes(new { replaced = "Fresh provider response" }),
                "application/json",
                context.Input.CaptureVersion,
                1,
                DateTimeOffset.UtcNow
            )
        );
        var changed = (await reads.ListSetlistAnnotationsAsync(context.Edition))!.items;
        changed[0]
            .footnotes![0]
            .note.evidence.Should()
            .BeEquivalentTo(
                citation,
                configuration => configuration.Excluding(value => value.evidence_status)
            );
        changed[0].footnotes![0].note.evidence.evidence_status.Should().Be("changed");
        changed[3]
            .footnote_references!.Should()
            .OnlyContain(reference => reference.evidence.input_revision_uuid == context.Input.Uuid);
        changed[3].notes!.Should().NotContain(note => note.text == "Before the show");
        var missing = await Snapshot(captureCurrent: false);
        (await reads.ListSetlistAnnotationsAsync(missing.Edition))!
            .items[0]
            .origin!.evidence.evidence_status.Should()
            .Be("missing");
    }

    [TestCase("legacy")]
    [TestCase("unknown_format")]
    [TestCase("markup_without_derivation")]
    public async Task RawMarkupIsNeverAnImplicitDisplayFallback(string format)
    {
        var context = await Snapshot(noteFormat: format);
        var note = (await reads.ListSetlistAnnotationsAsync(context.Edition))!
            .items[0]
            .notes!.Single(note => note.kind == "pre_show");
        note.detail_status.Should().Be("not_decoded");
        note.text.Should().BeNull();
        note.evidence.quote.Should().BeNull();
        note.evidence.json_pointer.Should().Be("/data/prenotes");
    }

    [Test]
    public async Task MarkupDisplayUsesOnlyTheExplicitPlainTextDerivation()
    {
        var context = await Snapshot(noteFormat: "markup_derived");
        var note = (await reads.ListSetlistAnnotationsAsync(context.Edition))!
            .items[0]
            .notes!.Single(note => note.kind == "pre_show");
        note.detail_status.Should().Be("available");
        note.text.Should().Be("Before & after");
        note.evidence.json_pointer.Should().Be("/data/prenotes");
        note.evidence.input_revision_uuid.Should().Be(context.Input.Uuid);
        note.evidence.quote.Should().BeNull();
    }

    [Test]
    public async Task RealJerryShowFactsRemainCitedAccountNotesInTheServiceAndHttpResponse()
    {
        var context = await JerrySnapshot();
        var groups = new List<NormalizedSetlistAnnotationGroup>();
        string? cursor = null;
        do
        {
            var page = (await reads.ListSetlistAnnotationsAsync(context.Edition, 7, cursor))!;
            groups.AddRange(page.items);
            cursor = page.next_cursor;
        } while (cursor != null);

        var account = groups.Single(group => group.scope == "account");
        var sourceNote = context.Account.Text.Single(note => note.Field == "show_facts");
        var note = account.notes!.Should().ContainSingle().Which;
        note.kind.Should().Be("show_facts");
        note.detail_status.Should().Be("available");
        note.text.Should().Be(sourceNote.PlainText);
        note.text!.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(14);
        note.text.Should().Contain("Final \"Broken Arrow\".");
        note.text.Should().Contain("Second show scheduled for the following night was cancelled.");
        note.text.Should().NotContain("<br").And.NotContain("<p>").And.NotContain("</p>");
        note.evidence.input_revision_uuid.Should().Be(context.Input.Uuid);
        note.evidence.json_pointer.Should().Be(sourceNote.Text.Evidence.JsonPointer);
        note.evidence.start_code_point.Should().Be(sourceNote.Text.Evidence.StartCodePoint);
        note.evidence.end_code_point.Should().Be(sourceNote.Text.Evidence.EndCodePoint);
        note.evidence.quote.Should().Be(sourceNote.Text.Value);
        note.evidence.quote.Should().Contain("<br />");
        note.evidence.evidence_status.Should().Be("current");

        groups.Count.Should().Be(22);
        groups.Where(group => group.scope == "set").Should().HaveCount(3);
        var passages = groups.Where(group => group.scope == "passage").ToArray();
        passages
            .Select(group => group.passage_title!.text)
            .Should()
            .Equal(context.Account.Passages.Select(passage => passage.Title.Value));
        passages
            .Should()
            .HaveCount(18)
            .And.OnlyContain(group => group.correspondence == "unresolved");
        passages[4].passage_title!.text.Should().Be("Brokedown Palace");
        // Retaining provider prose does not turn its conflicting Broken Arrow claim into a
        // new musical passage or an accepted identity assignment.
        (await reads.ListOccurrencesAsync(context.Edition))!
            .items.Should()
            .BeEmpty();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(reads);
        builder
            .Services.AddControllers()
            .AddApplicationPart(typeof(NormalizationMusicController).Assembly)
            .AddNewtonsoftJson();
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        using var client = new HttpClient
        {
            BaseAddress = new Uri(
                app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!
                    .Addresses.Single()
            ),
        };
        using var response = await client.GetAsync(
            $"/api/v3/normalization/setlists/{context.Edition}/annotations?limit=50"
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = JObject.Parse(await response.Content.ReadAsStringAsync())["items"]!.ToArray();
        items.Should().HaveCount(22);
        var publicNote = items.Single(item => item["scope"]!.Value<string>() == "account")[
            "notes"
        ]!.Single()!;
        publicNote["kind"]!.Value<string>().Should().Be("show_facts");
        publicNote["detail_status"]!.Value<string>().Should().Be("available");
        publicNote["text"]!.Value<string>().Should().Be(note.text);
        publicNote["evidence"]!["quote"]!.Value<string>().Should().Be(sourceNote.Text.Value);
        items.Where(item => item["scope"]!.Value<string>() == "passage").Should().HaveCount(18);
        await app.StopAsync();
    }

    [Test]
    public async Task UnknownNoteKindsRemainUnsupportedEvenWithDecodedText()
    {
        var context = await Snapshot(noteField: "future_provider_note");
        var note = (await reads.ListSetlistAnnotationsAsync(context.Edition))!
            .items[0]
            .notes!.Single(note => note.kind == "unsupported");
        note.detail_status.Should().Be("unsupported_kind");
        note.text.Should().BeNull();
        note.evidence.input_revision_uuid.Should().Be(context.Input.Uuid);
        note.evidence.json_pointer.Should().Be("/data/prenotes");
    }

    [Test]
    public async Task OversizedTextAndCorrespondenceRemainVisibleWithoutPartialOrUnboundedDetails()
    {
        var text = await Snapshot(largeNote: true);
        var groups = (await reads.ListSetlistAnnotationsAsync(text.Edition))!.items;
        groups[0].detail_status.Should().Be("size_limit");
        groups[0].notes.Should().BeNull();
        groups[0].origin.Should().BeNull();
        groups[1].notes!.Single().text.Should().Be("Set note");
        var split = await Snapshot(
            targetCount: NormalizationReadService.MaximumAnnotationTargets + 1
        );
        var mapped = (await reads.ListSetlistAnnotationsAsync(split.Edition))!.items.Single(group =>
            group.source_passage_uuid == split.Passages[0].ProposalUuid
        );
        mapped.detail_status.Should().Be("size_limit");
        mapped.target_count.Should().Be(NormalizationReadService.MaximumAnnotationTargets + 1);
        mapped.occurrence_uuids.Should().BeNull();
        mapped.notes.Should().BeNull();
    }

    [TestCase("repeated_references")]
    [TestCase("escaped_unicode")]
    public async Task DerivedPublicDetailsCannotExceedTheGroupBudget(string expansion)
    {
        var context =
            expansion == "repeated_references"
                ? await Snapshot(duplicateDefinitions: 250, repeatedReferences: 35)
                : await Snapshot(noteText: new string('\u2028', 2600));
        var first = (await reads.ListSetlistAnnotationsAsync(context.Edition, 3))!;
        var cursor = expansion == "repeated_references" ? first.next_cursor : null;
        var group = (
            await reads.ListSetlistAnnotationsAsync(context.Edition, 1, cursor)
        )!.items.Single();
        group.detail_status.Should().Be("size_limit");
        group.scope.Should().Be(expansion == "repeated_references" ? "passage" : "account");
        group.input_revision_uuid.Should().Be(context.Input.Uuid);
        group.origin.Should().BeNull();
        group.notes.Should().BeNull();
        group.footnote_references.Should().BeNull();
        group.occurrence_uuids.Should().BeNull();
        group.target_count.Should().Be(expansion == "repeated_references" ? 1 : 0);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(reads);
        builder
            .Services.AddControllers()
            .AddApplicationPart(typeof(NormalizationMusicController).Assembly)
            .AddNewtonsoftJson();
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        using var client = new HttpClient
        {
            BaseAddress = new Uri(
                app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!
                    .Addresses.Single()
            ),
        };
        using var response = await client.GetAsync(
            $"/api/v3/normalization/setlists/{context.Edition}/annotations?limit=1"
                + (cursor == null ? "" : "&cursor=" + cursor)
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var item = JObject.Parse(await response.Content.ReadAsStringAsync())["items"]!.Single()!;
        item["detail_status"]!.Value<string>().Should().Be("size_limit");
        item["footnote_references"]!.Type.Should().Be(JTokenType.Null);
        item["notes"]!.Type.Should().Be(JTokenType.Null);
        Encoding
            .UTF8.GetByteCount(item.ToString(Newtonsoft.Json.Formatting.None))
            .Should()
            .BeLessThanOrEqualTo(NormalizationReadService.MaximumAnnotationGroupBytes);
        await app.StopAsync();
    }

    [Test]
    public async Task HttpContractIncludesTypedDetailsAndRejectsChangedRevisionCursors()
    {
        var context = await Snapshot();
        var empty = await Snapshot(includeStructure: false);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(reads);
        builder
            .Services.AddControllers()
            .AddApplicationPart(typeof(NormalizationMusicController).Assembly)
            .AddNewtonsoftJson();
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        using var client = new HttpClient
        {
            BaseAddress = new Uri(
                app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!
                    .Addresses.Single()
            ),
        };
        var route = $"/api/v3/normalization/setlists/{context.Edition}/annotations";
        using var response = await client.GetAsync(route + "?limit=1");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var json = JObject.Parse(await response.Content.ReadAsStringAsync());
        var item = json["items"]![0]!;
        item["scope"]!.Value<string>().Should().Be("account");
        item["footnotes"]![0]!["number"]!.Value<int>().Should().Be(2);
        item["prerecorded_playback"]!.Type.Should().Be(JTokenType.Null);
        item["footnotes"]![0]!["note"]!["evidence"]!["quote"]!.Type.Should().Be(JTokenType.Null);
        item["footnotes"]![0]!["note"]!["evidence"]!["json_pointer"]!
            .Value<string>()
            .Should()
            .Be("/data/footnotes/0/note");
        item["Sources"].Should().BeNull();
        item["Account"].Should().BeNull();
        var cursor = json["next_cursor"]!.Value<string>()!;
        (await client.GetAsync($"/api/v3/normalization/setlists/{empty.Edition}/annotations"))
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);
        (await reads.ListSetlistAnnotationsAsync(empty.Edition))!.items.Should().BeEmpty();
        (await client.GetAsync($"/api/v3/normalization/setlists/{Guid.NewGuid()}/annotations"))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);
        (await client.GetAsync(route + "?limit=201"))
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest);
        (
            await client.GetAsync(
                $"/api/v3/normalization/setlists/{empty.Edition}/annotations?cursor={cursor}"
            )
        )
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest);
        await fixture.Db.WithWriteConnection(async c =>
        {
            var next = await Revision(
                c,
                context.Structure.Uuid,
                "setlist_structures",
                8,
                JsonSerializer.Serialize(context.Structure)
            );
            await c.ExecuteAsync(
                "UPDATE normalization.setlist_structures SET revision_uuid=@next WHERE uuid=@uuid",
                new { next, uuid = context.Structure.Uuid }
            );
        });
        (await client.GetAsync(route + "?cursor=" + cursor))
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE normalization.setlist_editions SET retracted=true WHERE uuid=@uuid",
                new { uuid = context.Edition }
            )
        );
        (await client.GetAsync(route)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        options.Enabled = false;
        try
        {
            using var disabled = await client.GetAsync(route);
            disabled.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            JObject.Parse(await disabled.Content.ReadAsStringAsync())["code"]!
                .Value<string>()
                .Should()
                .Be("normalization_unavailable");
        }
        finally
        {
            options.Enabled = true;
        }
        await app.StopAsync();
    }

    // Publication validation has dedicated service tests. These fixtures insert accepted images
    // directly so the read contract can isolate historical formats and oversized retained details.
    private async Task<SnapshotContext> Snapshot(
        bool captureCurrent = true,
        bool includeStructure = true,
        string? noteFormat = null,
        bool largeNote = false,
        int targetCount = 1,
        int passageCount = 2,
        string? noteText = null,
        int duplicateDefinitions = 0,
        int repeatedReferences = 0,
        string? noteField = null
    )
    {
        var source = await fixture.Source();
        var artist = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<Guid>("SELECT uuid FROM artists WHERE id=@ArtistId", source)
        );
        var showId = Random.Shared.Next(1, int.MaxValue);
        var note =
            noteText
            ?? (
                noteFormat != null ? "<b>Before &amp; after</b>"
                : largeNote
                    ? new string('x', NormalizationReadService.MaximumAnnotationGroupBytes + 1)
                : "Before the show"
            );
        var raw = new
        {
            data = new
            {
                id = showId,
                dateTime = "2000-01-01T20:00:00-05:00",
                band = new { id = 7, name = "Band" },
                prenotes = note,
                footnotes = duplicateDefinitions > 0
                    ? Enumerable
                        .Range(0, duplicateDefinitions)
                        .Select(_ => (object)new { id = (int?)2, note = "Duplicate" })
                        .ToArray()
                    : new object[]
                    {
                        new { id = (int?)2, note = "First guest" },
                        new { id = (int?)2, note = "Conflicting guest" },
                        new { id = (int?)null, note = "Unnumbered note" },
                    },
                sets = new object[]
                {
                    new
                    {
                        id = 1,
                        name = "First",
                        info = "Set note",
                        songs = Enumerable
                            .Range(0, passageCount)
                            .Select(index => new
                            {
                                id = 9 + index,
                                name = index == 0 ? "Song" : "Unknown tune",
                                segue = false,
                                footnotes = repeatedReferences > 0
                                    ? Enumerable.Repeat(2, repeatedReferences).ToArray()
                                    : new[] { 2, 99 },
                            })
                            .ToArray(),
                    },
                    new { id = 2, songs = Array.Empty<object>() },
                },
            },
        };
        var input = (
            await evidence.CaptureInputAsync(
                new(
                    Guid.NewGuid(),
                    new("phantasytour.com", "show", showId.ToString()),
                    JsonSerializer.SerializeToUtf8Bytes(raw),
                    "application/json",
                    "phantasy-tour-http-v1",
                    captureCurrent ? 0 : 1,
                    DateTimeOffset.UtcNow
                )
            )
        ).Revision;
        var account = new NormalizationProviderDecoder().Decode(input).Setlists.Single();
        if (duplicateDefinitions > 0)
        {
            // A valid accepted account can retain references without copied inline footnote
            // text. Keep each original definition/reference citation and exercise the public
            // reference-link expansion independently of the raw-note size guard.
            account = account with
            {
                Passages = account
                    .Passages.Select(passage => passage with { Annotations = [] })
                    .ToArray(),
            };
        }
        if (noteFormat != null)
        {
            account = account with
            {
                Text = account
                    .Text.Select(text =>
                        text.Field == "prenotes"
                            ? text with
                            {
                                Format =
                                    noteFormat == "unknown_format"
                                        ? (ProviderTextFormat)999
                                        : ProviderTextFormat.SourceMarkup,
                                PlainText =
                                    noteFormat == "markup_derived" ? "Before & after" : null,
                            }
                            : text
                    )
                    .ToArray(),
            };
        }
        if (noteField != null)
        {
            account = account with
            {
                Text = account
                    .Text.Select(text =>
                        text.Field == "prenotes" ? text with { Field = noteField } : text
                    )
                    .ToArray(),
            };
        }
        var proposal = new NormalizationSetlistAccountProposal(
            account,
            account
                .Passages.Select(passage => new NormalizationSetlistPassageProposal(
                    Guid.NewGuid(),
                    passage
                ))
                .ToArray()
        );
        var attempt = await new NormalizationSetlistProposalService(
            fixture.Db
        ).GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            new(new(input.Uuid, input.Resource), account.Origin.Item),
            NormalizationSetlistProcessor.Version
        );
        var edition = new SetlistEditionChange
        {
            Uuid = Guid.NewGuid(),
            ArtistUuid = artist,
            LiteralDate = "2000-01-01",
            Provider = account.Origin.Item.Provider,
            ResourceType = account.Origin.Item.ResourceType,
            ProviderKey = account.Origin.Item.ProviderKey,
        };
        var set = Guid.NewGuid();
        var targets = Enumerable
            .Range(0, targetCount)
            .Select(_ => Guid.NewGuid())
            .Order()
            .ToArray();
        var structure = new SetlistStructureChange
        {
            Uuid = Guid.NewGuid(),
            Edition = edition,
            Sources = [new(attempt.AttemptUuid, input.Uuid, 0, proposal)],
            Sets =
            [
                new()
                {
                    Uuid = set,
                    Position = 1,
                    Label = "First",
                },
            ],
            SetCorrespondence = [new(0, 0, set)],
            PassageCorrespondence = [new(0, proposal.Passages[0].ProposalUuid, targets)],
            UnresolvedPassages = proposal
                .Passages.Skip(1)
                .Select(passage => new SetlistUnresolvedPassage(
                    0,
                    passage.ProposalUuid,
                    "Operator-private explanation"
                ))
                .ToArray(),
        };
        var payload = JsonSerializer.Serialize(structure);
        if (noteFormat == "legacy")
        {
            var value = JsonNode.Parse(payload)!;
            foreach (var text in value["Sources"]![0]!["Account"]!["Account"]!["Text"]!.AsArray())
            {
                text!.AsObject().Remove("Format");
            }
            payload = value.ToJsonString();
        }
        await StoreSnapshot(structure, payload, includeStructure);
        return new(edition.Uuid, structure, input, account, set, proposal.Passages, targets);
    }

    private async Task<SnapshotContext> JerrySnapshot()
    {
        var source = await fixture.Source();
        var artist = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<Guid>("SELECT uuid FROM artists WHERE id=@ArtistId", source)
        );
        var raw = await File.ReadAllBytesAsync(
            Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "Fixtures",
                "Jerry",
                "1995-07-02-real-show-excerpt.html"
            )
        );
        var input = (
            await evidence.CaptureInputAsync(
                new(
                    Guid.NewGuid(),
                    new(
                        "jerrygarcia.com",
                        "mirror_show_page",
                        "grateful_dead/1995-07-02-deer-creek-music-center-noblesville-in-usa"
                    ),
                    raw,
                    "text/html; charset=utf-8",
                    "jerry-mirror-html:1",
                    0,
                    DateTimeOffset.UtcNow
                )
            )
        ).Revision;
        var account = new NormalizationProviderDecoder().Decode(input).Setlists.Single();
        var proposal = new NormalizationSetlistAccountProposal(
            account,
            account
                .Passages.Select(passage => new NormalizationSetlistPassageProposal(
                    Guid.NewGuid(),
                    passage
                ))
                .ToArray()
        );
        var attempt = await new NormalizationSetlistProposalService(
            fixture.Db
        ).GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            new(new(input.Uuid, input.Resource), account.Origin.Item),
            NormalizationSetlistProcessor.Version
        );
        var edition = new SetlistEditionChange
        {
            Uuid = Guid.NewGuid(),
            ArtistUuid = artist,
            LiteralDate = account.LiteralDate!.Value,
            Ordered = account.Ordered,
            Provider = account.Origin.Item.Provider,
            ResourceType = account.Origin.Item.ResourceType,
            ProviderKey = account.Origin.Item.ProviderKey,
        };
        var sets = account
            .Sets.Select(set => new SetlistStructureSet
            {
                Uuid = Guid.NewGuid(),
                Position = set.Ordinal,
                Label = set.Label!.Value,
            })
            .ToArray();
        var structure = new SetlistStructureChange
        {
            Uuid = Guid.NewGuid(),
            Edition = edition,
            Sources = [new(attempt.AttemptUuid, input.Uuid, 0, proposal)],
            Sets = sets,
            SetCorrespondence = sets.Select(
                    (set, index) => new SetlistSetCorrespondence(0, index, set.Uuid)
                )
                .ToArray(),
            UnresolvedPassages = proposal
                .Passages.Select(passage => new SetlistUnresolvedPassage(
                    0,
                    passage.ProposalUuid,
                    "No canonical song assignment in this read fixture"
                ))
                .ToArray(),
        };
        await StoreSnapshot(structure, JsonSerializer.Serialize(structure));
        return new(edition.Uuid, structure, input, account, sets[0].Uuid, proposal.Passages, []);
    }

    private Task StoreSnapshot(
        SetlistStructureChange structure,
        string payload,
        bool includeStructure = true
    ) =>
        fixture.Db.WithWriteConnection(async c =>
        {
            var edition = structure.Edition;
            var editionRevision = await Revision(
                c,
                edition.Uuid,
                "setlist_editions",
                1,
                JsonSerializer.Serialize(edition)
            );
            await c.ExecuteAsync(
                "INSERT INTO normalization.setlist_editions(uuid,artist_uuid,literal_date,ordered,provider,resource_type,provider_key,selected,revision_uuid) VALUES(@Uuid,@ArtistUuid,@LiteralDate,@Ordered,@Provider,@ResourceType,@ProviderKey,false,@revision)",
                new
                {
                    edition.Uuid,
                    edition.ArtistUuid,
                    edition.LiteralDate,
                    edition.Ordered,
                    edition.Provider,
                    edition.ResourceType,
                    edition.ProviderKey,
                    revision = editionRevision,
                }
            );
            if (!includeStructure)
            {
                return;
            }
            var revision = await Revision(c, structure.Uuid, "setlist_structures", 7, payload);
            await c.ExecuteAsync(
                "INSERT INTO normalization.setlist_structures(uuid,edition_uuid,revision_uuid) VALUES(@Uuid,@edition,@revision)",
                new
                {
                    structure.Uuid,
                    edition = edition.Uuid,
                    revision,
                }
            );
            foreach (var set in structure.Sets)
            {
                await c.ExecuteAsync(
                    "INSERT INTO normalization.setlist_sets(uuid,edition_uuid,position,label,revision_uuid) VALUES(@Uuid,@edition,@Position,@Label,@revision)",
                    new
                    {
                        set.Uuid,
                        edition = edition.Uuid,
                        set.Position,
                        set.Label,
                        revision,
                    }
                );
            }
            // Each read fixture retains one source account; correspondence remains explicit.
            var source = structure.Sources[0];
            foreach (
                var mapping in structure
                    .SetCorrespondence.Select(mapping => new
                    {
                        kind = "set",
                        key = mapping.SetIndex.ToString(
                            System.Globalization.CultureInfo.InvariantCulture
                        ),
                        target = mapping.SetUuid,
                    })
                    .Concat(
                        structure.PassageCorrespondence.SelectMany(mapping =>
                            mapping.OccurrenceUuids.Select(target => new
                            {
                                kind = "passage",
                                key = mapping.ProposalUuid.ToString("D"),
                                target,
                            })
                        )
                    )
            )
            {
                await c.ExecuteAsync(
                    "INSERT INTO normalization.setlist_structure_correspondences(revision_uuid,source_index,source_attempt_uuid,input_revision_uuid,account_index,kind,source_key,target_uuid,edition_uuid) VALUES(@revision,0,@attempt,@input,0,@kind,@key,@target,@edition)",
                    new
                    {
                        revision,
                        attempt = source.ProposalAttemptUuid,
                        input = source.InputRevisionUuid,
                        mapping.kind,
                        mapping.key,
                        mapping.target,
                        edition = edition.Uuid,
                    }
                );
            }
        });

    private static Task<Guid> Revision(
        System.Data.IDbConnection c,
        Guid entity,
        string kind,
        int number,
        string payload
    ) =>
        c.QuerySingleAsync<Guid>(
            "INSERT INTO normalization.music_revisions(uuid,entity_uuid,entity_kind,revision,decision_uuid,basis,payload,evidence) VALUES(gen_random_uuid(),@entity,@kind,@number,gen_random_uuid(),'reviewed',@payload::jsonb,'[]') RETURNING uuid",
            new
            {
                entity,
                kind,
                number,
                payload,
            }
        );

    private sealed record SnapshotContext(
        Guid Edition,
        SetlistStructureChange Structure,
        NormalizationInputRevision Input,
        ProviderSetlistObservation Account,
        Guid Set,
        IReadOnlyList<NormalizationSetlistPassageProposal> Passages,
        Guid[] Targets
    );
}
