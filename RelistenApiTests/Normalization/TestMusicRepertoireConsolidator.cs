using System.Text;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>Scoped title correspondences remain cited proposals over original member keys.</summary>
[TestFixture]
public sealed class TestMusicRepertoireConsolidator
{
    private const string Scope = "captured:grateful-dead";
    private static readonly Guid Artist = Guid.Parse("d20ca0c1-7219-41a5-9982-f4323c286de1");
    private static readonly Guid Scarlet = Guid.Parse("73a1f1ec-0e2c-2b68-839f-946193fb3565");
    private static readonly Guid Fire = Guid.Parse("eb19a366-d71c-4134-b116-52022605b0a2");

    [Test]
    public void NumberedScarletUsesTheExistingArtistDictionaryIdentityAndOriginalWitness()
    {
        var source = Descriptor("09.Scarlet Begonias");
        var result = Resolve(source, DictionaryName("Scarlet Begonias", Scarlet));

        result.Disposition.Should().Be("proposed");
        result.OriginalKey.Should().Be(source.OriginalKey);
        result.OriginalTitle.Should().Be(source.Title);
        result.SourceWitness.Should().Be(source.Witness);
        var target = result.Targets.Should().ContainSingle().Subject;
        target.Song.EntityKey.Should().Be(ShadowRepertoireAdapter.KnownEntityKey(Scarlet));
        target.Song.LegacySongUuid.Should().Be(Scarlet);
        target.Relation.Should().Be(MusicRepertoireRelation.IdentityAlias);
        target.Evidence.Quote.Should().Be("Scarlet Begonias");
        target
            .Qualifiers.Should()
            .Contain(value => value.StructuralKind == TrackTitleStructuralKind.TrackNumber);
        AssertCitations(result);
    }

    [Test]
    public void LiteralNamedRepriseWinsOverTheAlternativeBaseTitle()
    {
        var reprise = Guid.Parse("08b38bcd-1c0d-4883-bb29-d05f661547fb");
        var result = Resolve(
            Descriptor("Tweezer Reprise"),
            DictionaryName("Tweezer", Scarlet),
            DictionaryName("Tweezer Reprise", reprise)
        );

        result.Disposition.Should().Be("proposed");
        var target = result.Targets.Should().ContainSingle().Subject;
        target.Song.LegacySongUuid.Should().Be(reprise);
        target.Relation.Should().Be(MusicRepertoireRelation.IdentityAlias);
        target.Evidence.Quote.Should().Be("Tweezer Reprise");
        AssertCitations(result);
    }

    [Test]
    public void TwoKnownCompoundMembersAreInferredWithoutSegueTimingOrPerformanceClaims()
    {
        var result = Resolve(
            Descriptor("Scarlet Begonias - Fire On The Mountain"),
            DictionaryName("Scarlet Begonias", Scarlet),
            DictionaryName("Fire On The Mountain", Fire)
        );

        result.Disposition.Should().Be("proposed");
        result.SequenceBasis.Should().Be("inferred_title_membership");
        result.Targets.Select(target => target.Song.LegacySongUuid).Should().Equal(Scarlet, Fire);
        result
            .Targets.Should()
            .OnlyContain(target =>
                target.Relation == MusicRepertoireRelation.CompoundMember
                && target.Reason == "inferred_dictionary_supported_compound"
            );
        result
            .Extraction!.Segments.Should()
            .OnlyContain(segment =>
                segment.TransitionAfter == TitleTransition.Unknown
                && segment.OccurrenceUuid == null
                && segment.StartMs == null
                && segment.EndMs == null
            );
        AssertCitations(result);
    }

    [Test]
    public void UnknownCompoundMemberKeepsTheOriginalForReviewWithoutPrefixCollapse()
    {
        var source = Descriptor("Scarlet Begonias - Unknown Passage");
        var result = Resolve(source, DictionaryName("Scarlet Begonias", Scarlet));

        result.Disposition.Should().Be("review");
        var target = result.Targets.Should().ContainSingle().Subject;
        target.Disposition.Should().Be("review");
        target.Reason.Should().Be("compound_has_unresolved_members");
        target.Song.EntityKey.Should().Be(source.OriginalKey);
        target.Song.LegacySongUuid.Should().BeNull();
        target.Relation.Should().Be(MusicRepertoireRelation.CompoundMember);
        AssertCitations(result);
    }

    [Test]
    public void DictionaryCandidateRejectedAsNonSongKeepsTheWholeCompoundForReview()
    {
        var source = Descriptor("Scarlet Begonias - Banter,");
        var result = Resolve(
            source,
            DictionaryName("Scarlet Begonias", Scarlet),
            DictionaryName("Banter", Fire)
        );

        result.Disposition.Should().Be("review");
        var target = result.Targets.Should().ContainSingle().Subject;
        target.Song.EntityKey.Should().Be(source.OriginalKey);
        target.Song.LegacySongUuid.Should().BeNull();
        target.Reason.Should().Be("compound_has_unresolved_members");
        target.Relation.Should().Be(MusicRepertoireRelation.CompoundMember);
        AssertCitations(result);
    }

    [TestCase("Scarlet Begonias Jam", MusicRepertoireRelation.Jam)]
    [TestCase("Scarlet Begonias (tease)", MusicRepertoireRelation.Tease)]
    [TestCase("Scarlet Begonias Reprise", MusicRepertoireRelation.Reprise)]
    [TestCase("Scarlet Begonias [practice]", MusicRepertoireRelation.Practice)]
    public void NamedVariantsKeepTheirExplicitRelationInsteadOfBecomingIdentityAliases(
        string title,
        MusicRepertoireRelation relation
    )
    {
        var result = Resolve(Descriptor(title), DictionaryName("Scarlet Begonias", Scarlet));

        result.Disposition.Should().Be("proposed");
        var target = result.Targets.Should().ContainSingle().Subject;
        target.Song.LegacySongUuid.Should().Be(Scarlet);
        target.Relation.Should().Be(relation);
        target.Qualifiers.Should().Contain(value => value.Kind.StartsWith("named_"));
        result.OriginalTitle.Should().Be(title);
        AssertCitations(result);
    }

    [Test]
    public void SameTitleDictionaryRivalsRemainVisibleForReview()
    {
        var result = Resolve(
            Descriptor("09.Scarlet Begonias"),
            DictionaryName("Scarlet Begonias", Scarlet),
            DictionaryName("Scarlet Begonias", Fire)
        );

        result.Disposition.Should().Be("review");
        result
            .Targets.Select(target => target.Song.LegacySongUuid)
            .Should()
            .BeEquivalentTo(new Guid?[] { Scarlet, Fire });
        result.Targets.Should().OnlyContain(target => target.Disposition == "review");
        AssertCitations(result);
    }

    [TestCase("09.Scarlet Begonias", "Scarlet Begonias")]
    [TestCase("SawMill", "Saw Mill")]
    public void DictionaryCandidatesStayWithinBothSourceScopeAndArtist(
        string title,
        string dictionaryTitle
    )
    {
        var otherArtist = Guid.Parse("42b9192d-0412-4c2f-80be-718b9ed48e25");
        var sameScope = Descriptor(title);
        var otherScope = sameScope with { Scope = "captured:another-catalogue" };
        var anotherArtist = sameScope with
        {
            ArtistUuid = otherArtist,
            OriginalKey = ShadowRepertoireAdapter.ProvisionalEntityKey(
                otherArtist,
                sameScope.Title!
            ),
        };
        var consolidator = new MusicRepertoireConsolidator();
        consolidator.ObserveDictionary(DictionaryName(dictionaryTitle, Scarlet));
        foreach (var source in new[] { sameScope, otherScope, anotherArtist })
        {
            consolidator.ObserveDescriptor(source);
        }
        consolidator.CompleteObservation();

        consolidator.Resolve(sameScope).Targets.Single().Song.LegacySongUuid.Should().Be(Scarlet);
        foreach (var source in new[] { otherScope, anotherArtist })
        {
            var result = consolidator.Resolve(source);
            result.Scope.Should().Be(source.Scope);
            var target = result.Targets.Should().ContainSingle().Subject;
            target.Song.LegacySongUuid.Should().BeNull();
            target.Song.ArtistUuid.Should().Be(source.ArtistUuid);
        }
    }

    [Test]
    public void LaterObservedQualifiedWorkContradictionPreventsAnAliasProposal()
    {
        var source = Descriptor("09.Scarlet Begonias") with { WorkKeys = ["composition:source"] };
        var consolidator = new MusicRepertoireConsolidator();
        consolidator.ObserveDescriptor(source);
        consolidator.ObserveDictionary(
            DictionaryName("Scarlet Begonias", Scarlet) with
            {
                WorkKeys = ["composition:target"],
            }
        );
        consolidator.CompleteObservation();

        var result = consolidator.Resolve(source);
        result.Disposition.Should().Be("review");
        var target = result.Targets.Should().ContainSingle().Subject;
        target.Disposition.Should().Be("review");
        target.Reason.Should().Be("qualified_work_contradiction");
        target.Song.LegacySongUuid.Should().Be(Scarlet);
    }

    [Test]
    public void CompleteFirstPassChoosesTheExistingBaseSpellingIndependentlyOfInputOrder()
    {
        var numbered = Descriptor("09.SCARLET BEGONIAS");
        var existingBase = Descriptor("Scarlet Begonias");
        var first = Observe([numbered, existingBase]);
        var second = Observe([existingBase, numbered]);

        var result = first.Resolve(numbered);
        result.Should().BeEquivalentTo(second.Resolve(numbered));
        var target = result.Targets.Should().ContainSingle().Subject;
        target.Song.EntityKey.Should().Be(existingBase.OriginalKey);
        target.Song.Name.Should().Be(existingBase.Title);
        target.Song.LegacySongUuid.Should().BeNull();

        var incomplete = new MusicRepertoireConsolidator();
        incomplete.ObserveDescriptor(numbered);
        var resolveBeforeComplete = () => incomplete.Resolve(numbered);
        resolveBeforeComplete.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void MissingTitleRetainsItsOriginalKeyAndWitnessWithoutInventingATarget()
    {
        var source = Descriptor(null);
        var result = Resolve(source);

        result.Disposition.Should().Be("unresolved");
        result.Reason.Should().Be("indexed_reference_without_title");
        result.OriginalKey.Should().Be(source.OriginalKey);
        result.OriginalTitle.Should().BeNull();
        result.SourceWitness.Should().Be(source.Witness);
        result.Targets.Should().BeEmpty();
        result.Extraction.Should().BeNull();
    }

    [TestCase("gatrios2011-12-17s2t05.matrix.kindrecordings.mp3")]
    [TestCase("gd78-04-16d1t09_matrix.kindrecordings")]
    public void OpaqueRecordingFilenameTailDoesNotBecomeAProvisionalSongWithoutDictionarySupport(
        string title
    )
    {
        var source = Descriptor(title);
        var result = Resolve(source);

        result.Disposition.Should().Be("unresolved");
        result.OriginalKey.Should().Be(source.OriginalKey);
        result.OriginalTitle.Should().Be(source.Title);
        result.SourceWitness.Should().Be(source.Witness);
        result.Targets.Should().BeEmpty();
    }

    [Test]
    public void DuplicateOriginalKeysAreRejectedWithinTheirScope()
    {
        var source = Descriptor("Scarlet Begonias");
        var consolidator = new MusicRepertoireConsolidator();
        consolidator.ObserveDescriptor(source);
        consolidator.ObserveDescriptor(source with { Scope = "captured:another-catalogue" });

        var duplicate = () => consolidator.ObserveDescriptor(source);
        duplicate.Should().Throw<InvalidDataException>();
        consolidator.DescriptorCount.Should().Be(2);
    }

    [TestCase("catalog_context", true)]
    [TestCase("retained_result", false)]
    public void OnlyCatalogContextMayDeriveADictionaryKeyFromItsRetainedSongUuid(
        string documentKind,
        bool mayDerive
    )
    {
        var source = Descriptor("09.Scarlet Begonias");
        var name = DictionaryName("Scarlet Begonias", Scarlet) with
        {
            SongKey = null,
            Witness = Witness("/songs/0/name") with { DocumentKind = documentKind },
        };
        if (!mayDerive)
        {
            var observe = () => new MusicRepertoireConsolidator().ObserveDictionary(name);
            observe.Should().Throw<InvalidDataException>();
            return;
        }

        var target = Resolve(source, name).Targets.Should().ContainSingle().Subject;
        target.Song.EntityKey.Should().Be(ShadowRepertoireAdapter.KnownEntityKey(Scarlet));
        target.Song.LegacySongUuid.Should().Be(Scarlet);
    }

    [TestCase("2001")]
    [TestCase("99 Luftballons")]
    public void NumberLeadingLiteralDictionaryTitlesKeepTheirWholeIdentity(string title)
    {
        var result = Resolve(Descriptor(title), DictionaryName(title, Scarlet));

        result.Disposition.Should().Be("proposed");
        var target = result.Targets.Should().ContainSingle().Subject;
        target.Song.LegacySongUuid.Should().Be(Scarlet);
        target.Evidence.Quote.Should().Be(title);
        target.Relation.Should().Be(MusicRepertoireRelation.IdentityAlias);
        AssertCitations(result);
    }

    [TestCase("SawMill", "Saw Mill")]
    [TestCase("Turn On Your LoveLight", "Turn On Your Love Light")]
    [TestCase("WestL.A.Fadeaway", "West L.A. Fadeaway")]
    public void BoundarySpellingUsesTheUniqueScopedDictionaryIdentityWithoutChangingTheOriginalKey(
        string title,
        string dictionaryTitle
    )
    {
        var source = Descriptor(title);
        var result = Resolve(source, DictionaryName(dictionaryTitle, Scarlet));

        result.OriginalKey.Should().Be(source.OriginalKey);
        result.OriginalTitle.Should().Be(title);
        result.SourceWitness.Should().Be(source.Witness);
        var target = result.Targets.Should().ContainSingle().Subject;
        target.Disposition.Should().Be("proposed");
        target.Song.LegacySongUuid.Should().Be(Scarlet);
        target.Song.EntityKey.Should().Be(ShadowRepertoireAdapter.KnownEntityKey(Scarlet));
        target.Song.Name.Should().Be(dictionaryTitle);
        target.Relation.Should().Be(MusicRepertoireRelation.IdentityAlias);
        target.Evidence.Quote.Should().Be(title);
        AssertCitations(result);
    }

    [TestCase("A B")]
    [TestCase("a   b")]
    public void ExistingLiteralOrNormalizedDictionaryMatchWinsBeforeBoundaryRivals(string title)
    {
        var result = Resolve(
            Descriptor(title),
            DictionaryName("A B", Scarlet),
            DictionaryName("AB", Fire)
        );

        var target = result.Targets.Should().ContainSingle().Subject;
        target.Disposition.Should().Be("proposed");
        target.Song.LegacySongUuid.Should().Be(Scarlet);
        target.Relation.Should().Be(MusicRepertoireRelation.IdentityAlias);
        AssertCitations(result);
    }

    [Test]
    public void BoundaryCollisionsRetainEveryDictionaryIdentityForReview()
    {
        var source = Descriptor("ABC");
        var result = Resolve(source, DictionaryName("AB C", Scarlet), DictionaryName("A BC", Fire));

        result.Disposition.Should().Be("review");
        result.OriginalKey.Should().Be(source.OriginalKey);
        result
            .Targets.Select(target => target.Song.LegacySongUuid)
            .Should()
            .BeEquivalentTo(new Guid?[] { Scarlet, Fire });
        result.Targets.Should().OnlyContain(target => target.Disposition == "review");
        AssertCitations(result);
    }

    [TestCase("Cafe", "Café")]
    [TestCase("SawMills", "Saw Mill")]
    public void BoundaryFallbackDoesNotFoldDiacriticsOrChangeLetters(
        string title,
        string dictionaryTitle
    )
    {
        var source = Descriptor(title);
        var result = Resolve(source, DictionaryName(dictionaryTitle, Scarlet));

        var target = result.Targets.Should().ContainSingle().Subject;
        target.Song.LegacySongUuid.Should().BeNull();
        target.Song.EntityKey.Should().Be(source.OriginalKey);
        target.Song.Name.Should().Be(title);
        AssertCitations(result);
    }

    [TestCase("SawMill Jam", MusicRepertoireRelation.Jam)]
    [TestCase("SawMill (tease)", MusicRepertoireRelation.Tease)]
    [TestCase("SawMill Reprise", MusicRepertoireRelation.Reprise)]
    public void BoundaryFallbackKeepsMusicalVariantsSeparateFromIdentityAliases(
        string title,
        MusicRepertoireRelation relation
    )
    {
        var result = Resolve(Descriptor(title), DictionaryName("Saw Mill", Scarlet));

        var target = result.Targets.Should().ContainSingle().Subject;
        target.Disposition.Should().Be("proposed");
        target.Song.LegacySongUuid.Should().Be(Scarlet);
        target.Relation.Should().Be(relation);
        target.Qualifiers.Should().NotBeEmpty();
        AssertCitations(result);
    }

    [TestCase("Ban ter", "Banter")]
    [TestCase("In tro", "Intro")]
    [TestCase("Soun dcheck", "Soundcheck")]
    [TestCase("Ta lk", "Talk")]
    [TestCase("En core Break", "Encore Break")]
    public void BoundaryFallbackDoesNotTurnASplitControlLabelIntoAMusicalTarget(
        string title,
        string dictionaryTitle
    )
    {
        var source = Descriptor(title);
        var result = Resolve(source, DictionaryName(dictionaryTitle, Scarlet));

        result.OriginalKey.Should().Be(source.OriginalKey);
        result.Targets.Should().BeEmpty();
    }

    [Test]
    public void BoundaryFallbackKeepsBothKnownCompoundMembersWithoutInventingTransitions()
    {
        var result = Resolve(
            Descriptor("SawMill - LoveLight"),
            DictionaryName("Saw Mill", Scarlet),
            DictionaryName("Love Light", Fire)
        );

        result.Targets.Select(target => target.Song.LegacySongUuid).Should().Equal(Scarlet, Fire);
        result
            .Targets.Should()
            .OnlyContain(target =>
                target.Disposition == "proposed"
                && target.Relation == MusicRepertoireRelation.CompoundMember
            );
        result
            .Extraction!.Segments.Should()
            .OnlyContain(segment =>
                segment.TransitionAfter == TitleTransition.Unknown
                && segment.OccurrenceUuid == null
                && segment.StartMs == null
                && segment.EndMs == null
            );
        AssertCitations(result);
    }

    private static MusicRepertoireCorrespondence Resolve(
        MusicRepertoireDescriptor source,
        params MusicDictionaryName[] names
    )
    {
        var consolidator = new MusicRepertoireConsolidator();
        foreach (var name in names)
        {
            consolidator.ObserveDictionary(name);
        }
        consolidator.ObserveDescriptor(source);
        consolidator.CompleteObservation();
        return consolidator.Resolve(source);
    }

    private static MusicRepertoireConsolidator Observe(MusicRepertoireDescriptor[] sources)
    {
        var consolidator = new MusicRepertoireConsolidator();
        foreach (var source in sources)
        {
            consolidator.ObserveDescriptor(source);
        }
        consolidator.CompleteObservation();
        return consolidator;
    }

    private static MusicRepertoireDescriptor Descriptor(string? title) =>
        new(
            Scope,
            Artist,
            title == null
                ? "repertoire:indexed-reference-without-title"
                : ShadowRepertoireAdapter.ProvisionalEntityKey(Artist, title),
            title,
            "title_derived",
            "proposed",
            null,
            [],
            Witness("/proposal/segments/0/song/name")
        );

    private static MusicDictionaryName DictionaryName(string title, Guid uuid) =>
        new(
            Scope,
            Artist,
            ShadowRepertoireAdapter.KnownEntityKey(uuid),
            uuid,
            title,
            "repertoire_song:" + uuid.ToString("D"),
            [],
            Witness("/proposal/song/name")
        );

    private static MusicTitleWitness Witness(string pointer) =>
        new("/synthetic/captured-run", "repertoire/000001.jsonl.gz", 1, 41, pointer, new('a', 64));

    private static void AssertCitations(MusicRepertoireCorrespondence result)
    {
        foreach (var target in result.Targets)
        {
            AssertEvidence(result.OriginalTitle!, target.Evidence);
            foreach (var qualifier in target.Qualifiers)
            {
                AssertEvidence(result.OriginalTitle!, qualifier.Evidence);
            }
        }
    }

    private static void AssertEvidence(string title, NormalizationTextEvidence evidence)
    {
        evidence.Field.Should().Be("title");
        evidence.JsonPointer.Should().Be("/title");
        evidence.StartCodePoint.Should().BeGreaterThanOrEqualTo(0);
        evidence.EndCodePoint.Should().BeLessThanOrEqualTo(title.EnumerateRunes().Count());
        var quote = string.Concat(
            title
                .EnumerateRunes()
                .Skip(evidence.StartCodePoint)
                .Take(evidence.EndCodePoint - evidence.StartCodePoint)
                .Select(rune => rune.ToString())
        );
        quote.Should().Be(evidence.Quote);
    }
}
