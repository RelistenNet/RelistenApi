using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestCompositionProgramQueryPlanner
{
    [Test]
    public void ReproducedUnicodeTitleAndEnclosingWordingPermitRetrievalWithoutWorkCandidates()
    {
        var fixture = ProgramFixture.Create();

        var plan = CompositionProgramQueryPlanner.Plan(
            [fixture.Target],
            [fixture.Reference],
            Identity()
        );

        var query = plan.Queries.Should().ContainSingle().Subject;
        query.References.Should().Equal(fixture.Reference);
        var retained = query.References.Single();
        retained.TitleEvidence.StartCodePoint.Should().Be(16);
        retained.TitleEvidence.EndCodePoint.Should().Be(29);
        retained.TitleEvidence.Quote.Should().Be("Night 🌙 & Day");
        retained.ReferenceEvidence.Quote.Should().Be(ProgramFixture.Text);
        retained.Origin.Should().Be(fixture.SourceText.Origin);
        retained.TextSha256.Should().Be(fixture.SourceText.TextSha256);
        plan.Unresolved.Should().BeEmpty();
        plan.PublicationPermitted.Should().BeFalse();
        plan.ExternalUniverseComplete.Should().BeFalse();
    }

    [Test]
    public void IdenticalSearchAcrossTargetsRetainsEachTargetsOwnSourceReference()
    {
        var first = ProgramFixture.Create();
        var second = ProgramFixture.Create();

        var plan = CompositionProgramQueryPlanner.Plan(
            [first.Target, second.Target],
            [first.Reference, second.Reference],
            Identity()
        );

        var query = plan.Queries.Should().ContainSingle().Subject;
        query.References.Should().Equal(first.Reference, second.Reference);
        query.References.Select(reference => reference.Target).Should().OnlyHaveUniqueItems();
        query.References.Select(reference => reference.Origin).Should().OnlyHaveUniqueItems();
        plan.Unresolved.Should().BeEmpty();
    }

    [TestCase("unknown_target")]
    [TestCase("wrong_context_target")]
    [TestCase("other_recording_origin")]
    [TestCase("other_field_pointer")]
    [TestCase("wrong_text_sha")]
    [TestCase("changed_source_text")]
    [TestCase("utf16_title_offsets")]
    [TestCase("wrong_title_quote")]
    [TestCase("wrong_enclosing_quote")]
    public void UnreproducibleOrUnownedReferenceRefusesTheWholePlan(string mutation)
    {
        var fixture = ProgramFixture.Create();
        var target = fixture.Target;
        var reference = fixture.Reference;
        switch (mutation)
        {
            case "unknown_target":
                reference = reference with
                {
                    Target = reference.Target with { SongEntityKey = "repertoire:other" },
                };
                break;
            case "wrong_context_target":
                target = target with
                {
                    Context = target.Context with
                    {
                        Key = target.Context.Key with { ArtistUuid = Guid.NewGuid() },
                    },
                };
                break;
            case "other_recording_origin":
                reference = reference with { Origin = reference.Origin with { Line = 2 } };
                break;
            case "other_field_pointer":
                reference = reference with
                {
                    TitleEvidence = reference.TitleEvidence with { JsonPointer = "/lineage" },
                };
                break;
            case "wrong_text_sha":
                reference = reference with { TextSha256 = new string('b', 64) };
                break;
            case "changed_source_text":
                target = target with
                {
                    Context = target.Context with
                    {
                        Witnesses =
                        [
                            target.Context.Witnesses.Single() with
                            {
                                Texts =
                                [
                                    fixture.SourceText with
                                    {
                                        Value = ProgramFixture.Text + "!",
                                    },
                                ],
                            },
                        ],
                    },
                };
                break;
            case "utf16_title_offsets":
                reference = reference with
                {
                    TitleEvidence = reference.TitleEvidence with
                    {
                        StartCodePoint = ProgramFixture.Text.IndexOf(
                            "Night",
                            StringComparison.Ordinal
                        ),
                        EndCodePoint =
                            ProgramFixture.Text.IndexOf("Night", StringComparison.Ordinal)
                            + reference.ProgramTitle.Length,
                    },
                };
                break;
            case "wrong_title_quote":
                reference = reference with
                {
                    TitleEvidence = reference.TitleEvidence with { Quote = "Another program" },
                };
                break;
            case "wrong_enclosing_quote":
                reference = reference with
                {
                    ReferenceEvidence = reference.ReferenceEvidence with
                    {
                        Quote = "From an album",
                    },
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        var plan = () => CompositionProgramQueryPlanner.Plan([target], [reference], Identity());

        plan.Should().Throw<InvalidDataException>();
    }

    [Test]
    public void UnknownRelationshipRemainsCitedAndUnresolvedWithoutAQuery()
    {
        var fixture = ProgramFixture.Create();
        var reference = fixture.Reference with { Kind = CompositionProgramReferenceKind.Unknown };

        var plan = CompositionProgramQueryPlanner.Plan([fixture.Target], [reference], Identity());

        plan.Queries.Should().BeEmpty();
        var unresolved = plan.Unresolved.Should().ContainSingle().Subject;
        unresolved.Reference.Should().Be(reference);
        unresolved.Reason.Should().Be("program_relationship_unresolved");
        plan.PublicationPermitted.Should().BeFalse();
    }

    [Test]
    public void IndependentlyReproducedWordingCannotSupportATitleOutsideItsSpan()
    {
        var fixture = ProgramFixture.Create();
        var reference = fixture.Reference with
        {
            ReferenceEvidence = ProgramFixture.Span("a soundtrack edition."),
        };

        var plan = () =>
            CompositionProgramQueryPlanner.Plan([fixture.Target], [reference], Identity());

        plan.Should().Throw<InvalidDataException>();
    }

    private static MusicBrainzSearchPlanIdentity Identity() =>
        new(Guid.NewGuid(), new string('a', 64));

    private sealed record ProgramFixture(
        CompositionProgramTarget Target,
        CompositionProgramReference Reference,
        CompositionSelectionTargetText SourceText
    )
    {
        public const string Text = "😀 Recorded for “Night 🌙 & Day”, a soundtrack edition.";

        public static ProgramFixture Create()
        {
            var artist = Guid.NewGuid();
            var songKey = "repertoire:" + Guid.NewGuid().ToString("D");
            var key = new CompositionSelectionTargetKey("catalogue", artist, songKey);
            var origin = new CompositionSelectionTargetOrigin(
                "/retained/recordings",
                "sources/" + Guid.NewGuid().ToString("D") + ".jsonl.gz",
                1,
                new string('c', 64),
                "/description"
            );
            var text = new CompositionSelectionTargetText(
                "description",
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text))),
                Text,
                origin
            );
            var song = new CompositionSelectionSong(
                key.Scope,
                artist,
                "Observed performer",
                songKey,
                "Don't Let Go",
                "title_derived",
                null,
                [],
                [],
                new(origin.Run, origin.Chunk, 1, 1, origin.JsonPointer, origin.Sha256, "source"),
                1
            );
            var context = new CompositionSelectionTargetContext(
                key,
                new("Observed performer", null, []),
                new(0, 1, 0, 0, true, 0, true),
                [
                    new(
                        "catalog_recording",
                        origin,
                        null,
                        Guid.NewGuid(),
                        null,
                        null,
                        "recording:original",
                        null,
                        null,
                        [songKey],
                        [text],
                        [],
                        []
                    ),
                ]
            );
            return new(
                new(song, context),
                new(
                    key,
                    "Night 🌙 & Day",
                    CompositionProgramReferenceKind.Soundtrack,
                    origin,
                    text.TextSha256,
                    new("description", origin.JsonPointer, "Night 🌙 & Day", 16, 29),
                    Span(Text)
                ),
                text
            );
        }

        public static NormalizationTextEvidence Span(string quote)
        {
            var start = Text.IndexOf(quote, StringComparison.Ordinal);
            var codePointStart = Text[..start].EnumerateRunes().Count();
            return new(
                "description",
                "/description",
                quote,
                codePointStart,
                codePointStart + quote.EnumerateRunes().Count()
            );
        }
    }
}
