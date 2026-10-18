using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestSourceActorReviewCorrespondence
{
    private const string Prose =
        "The Band:\n-- and some or all of the following --\n"
        + "Andy Moresi aka Hot Dog - keyboards\nZoë aka Lark - vocals\n"
        + "Recording notes: an aka outside the roster stays unresolved.";
    private readonly SourceActorReviewCorrespondence checker = new();

    [Test]
    public void EqualWholeFieldsCarryOriginalHumanAuthorityThroughACompleteRevisionMap()
    {
        var fixture = Create();
        checker
            .TryCreate(
                fixture.Review,
                fixture.Original,
                fixture.Current,
                fixture.Target,
                out var proof,
                out var reason
            )
            .Should()
            .BeTrue();

        reason.Should().Be("reviewed_source_acceptance");
        proof!.Version.Should().Be(SourceActorReviewCorrespondence.Version);
        proof.OriginalReview.Should().BeEquivalentTo(fixture.Review);
        proof.OriginalReview.LastOperationUuid.Should().Be(fixture.Review.LastOperationUuid);
        proof.OriginalReview.ReviewRevision.Should().Be(7);
        proof.OriginalReview.SelectedPersonUuid.Should().Be(fixture.Target.OriginalPersonUuid);
        proof.Target.ResolvedPersonUuid.Should().NotBe(fixture.Target.OriginalPersonUuid);
        proof.Original.Should().BeEquivalentTo(fixture.Original);
        proof.Current.Should().BeEquivalentTo(fixture.Current);
        proof.LocalIds.Select(id => id.OriginalUuid).Should().OnlyHaveUniqueItems();
        proof.LocalIds.Select(id => id.CurrentUuid).Should().OnlyHaveUniqueItems();
        proof.LocalIds.Should().OnlyContain(id => id.OriginalUuid != id.CurrentUuid);
        proof
            .LocalIds.Select(id => id.Kind)
            .Distinct()
            .Should()
            .BeEquivalentTo(Enum.GetValues<SourceActorReviewLocalIdKind>());
        proof
            .LocalIds.Count(id => id.Kind == SourceActorReviewLocalIdKind.Assertion)
            .Should()
            .Be(2);
        proof
            .Evidence.Should()
            .Contain(map =>
                map.Original == fixture.Original.FieldEvidence
                && map.Current == fixture.Current.FieldEvidence
            );
        foreach (var map in proof.Evidence)
        {
            (map.Original with { InputRevisionUuid = fixture.Current.Scope.InputRevisionUuid })
                .Should()
                .Be(map.Current);
        }
        proof.Current.Scope.Extraction.Unresolved.Should().NotBeEmpty();
        proof
            .Current.Scope.Extraction.Scopes.Should()
            .ContainSingle()
            .Which.Complete.Should()
            .BeFalse();
    }

    [TestCase("The Band:\nAndy Moresi aka Hot Dog - keyboards\n")]
    [TestCase(Prose + "\n")]
    [TestCase(
        "The Band:\r\n-- and some or all of the following --\r\nAndy Moresi aka Hot Dog - keyboards"
    )]
    public void EqualNamesDoNotCarryChangedSurroundingText(string changed)
    {
        var fixture = Create();
        var current = Field(
            changed,
            fixture.Current.Coordinates.SourceUuid,
            fixture.Current.Coordinates.ResourceUuid
        );
        Refuses(fixture with { Current = current }, "source_field_text_changed");
    }

    [Test]
    public void AnEqualAdvertisedHashCannotReplaceWholeTextEquality()
    {
        var fixture = Create();
        var current = Field(
            Prose + " ",
            fixture.Current.Coordinates.SourceUuid,
            fixture.Current.Coordinates.ResourceUuid
        );
        current = current with
        {
            Coordinates = current.Coordinates with
            {
                FieldSha256 = fixture.Original.Coordinates.FieldSha256,
            },
        };
        Refuses(fixture with { Current = current }, "source_field_hash_mismatch");
    }

    [Test]
    public void QualifiersEndpointsRolesCoverageAndUnresolvedFactsCannotBeEditedOrDropped()
    {
        var fixture = Create();
        var extraction = fixture.Current.Scope.Extraction;
        var first = extraction.Assertions[0];
        var relationship = first.Relationships[0];
        SourceActorAliasExtraction<NormalizedEvidenceReference>[] changed =
        [
            extraction with
            {
                Assertions =
                [
                    first with
                    {
                        Context = first.Context with { Qualifiers = [] },
                    },
                    extraction.Assertions[1],
                ],
            },
            extraction with
            {
                Assertions =
                [
                    first with
                    {
                        Relationships = [relationship with { ReferenceUuids = [Guid.NewGuid()] }],
                    },
                    extraction.Assertions[1],
                ],
            },
            extraction with
            {
                Assertions =
                [
                    first with
                    {
                        Relationships = [relationship with { Roles = [RecordingCreditRole.Taper] }],
                    },
                    extraction.Assertions[1],
                ],
            },
            extraction with
            {
                Assertions =
                [
                    first with
                    {
                        Aliases = [first.Aliases[0] with { ReferenceUuid = Guid.NewGuid() }],
                    },
                    extraction.Assertions[1],
                ],
            },
            extraction with
            {
                Assertions =
                [
                    first with
                    {
                        NameEvidence = first.NameEvidence with
                        {
                            StartCodePoint = first.NameEvidence.StartCodePoint + 1,
                        },
                    },
                    extraction.Assertions[1],
                ],
            },
            extraction with
            {
                Assertions = [first],
            },
            extraction with
            {
                Assertions = extraction.Assertions.Reverse().ToArray(),
            },
            extraction with
            {
                Unresolved = [],
            },
            extraction with
            {
                Scopes = [extraction.Scopes[0] with { Complete = true }],
            },
        ];
        foreach (var value in changed)
        {
            Refuses(
                fixture with
                {
                    Current = fixture.Current with
                    {
                        Scope = fixture.Current.Scope with { Extraction = value },
                    },
                },
                "current_projection_mismatch"
            );
        }
    }

    [Test]
    public void ARepeatedOrForeignLocalIdCannotAcquireTheReviewedBinding()
    {
        var fixture = Create();
        var extraction = fixture.Current.Scope.Extraction;
        var first = extraction.Assertions[0];
        var second = extraction.Assertions[1] with
        {
            ActorReferenceUuid = first.ActorReferenceUuid,
        };
        Refuses(
            fixture with
            {
                Current = fixture.Current with
                {
                    Scope = fixture.Current.Scope with
                    {
                        Extraction = extraction with { Assertions = [first, second] },
                    },
                },
            },
            "current_projection_mismatch"
        );
    }

    [Test]
    public void SelectingAnotherAssertionInTheSameFieldRequiresItsOwnReview()
    {
        var fixture = Create();
        var second = fixture.Current.Scope.Extraction.Assertions[1];
        Refuses(
            fixture with
            {
                Current = fixture.Current with
                {
                    Coordinates = fixture.Current.Coordinates with
                    {
                        AssertionUuid = second.AssertionUuid,
                        ActorReferenceUuid = second.ActorReferenceUuid,
                    },
                },
            },
            "selected_claim_changed"
        );
    }

    [Test]
    public void AFieldCannotMoveToAnotherSourceContributorResourceOrProducer()
    {
        var fixture = Create();
        var coordinates = fixture.Current.Coordinates;
        SourceActorReviewField[] changed =
        [
            fixture.Current with
            {
                Coordinates = coordinates with { SourceUuid = Guid.NewGuid() },
            },
            fixture.Current with
            {
                Coordinates = coordinates with
                {
                    Contributor = new("archive.org", "item", "another-account"),
                },
            },
            fixture.Current with
            {
                Coordinates = coordinates with { ResourceUuid = Guid.NewGuid() },
            },
            fixture.Current with
            {
                Coordinates = coordinates with { FieldJsonPointer = "/other" },
            },
        ];
        foreach (var value in changed)
        {
            Refuses(fixture with { Current = value }, "source_field_identity_changed");
        }
        Refuses(
            fixture with
            {
                Current = fixture.Current with
                {
                    Scope = fixture.Current.Scope with { ProducerVersion = "unknown-version" },
                },
            },
            "unsupported_source_alias_producer"
        );
    }

    [Test]
    public void SelectedRootFileAndWholeFieldSpanMustRemainExact()
    {
        var fixture = Create();
        var origin = fixture.Current.Scope.RecordingOrigin;
        Refuses(
            fixture with
            {
                Current = fixture.Current with
                {
                    Scope = fixture.Current.Scope with
                    {
                        RecordingOrigin = origin with
                        {
                            Item = new("taperssection", "account", "another-root"),
                        },
                    },
                },
            },
            "source_field_origin_changed"
        );
        Refuses(
            fixture with
            {
                Current = fixture.Current with
                {
                    Scope = fixture.Current.Scope with { FileOrigin = origin },
                },
            },
            "source_field_origin_changed"
        );
        Refuses(
            fixture with
            {
                Current = fixture.Current with
                {
                    FieldEvidence = fixture.Current.FieldEvidence with { EndCodePoint = 4 },
                },
            },
            "invalid_whole_field_evidence"
        );
    }

    [Test]
    public void RejectedHistoryOrChangedOriginalHumanSnapshotCannotSupplyAuthority()
    {
        var fixture = Create();
        Refuses(
            fixture with
            {
                Review = fixture.Review with { State = "retracted", SelectedPersonUuid = null },
            },
            "original_review_not_accepted"
        );
        Refuses(
            fixture with
            {
                Review = fixture.Review with { LastOperationUuid = Guid.Empty },
            },
            "original_review_not_accepted"
        );
        Refuses(
            fixture with
            {
                Review = fixture.Review with { AssertionSha256 = new string('0', 64) },
            },
            "original_review_assertion_mismatch"
        );
        Refuses(
            fixture with
            {
                Review = fixture.Review with
                {
                    OriginalAssertion = fixture.Review.OriginalAssertion with
                    {
                        Name = "Another actor",
                    },
                },
            },
            "original_review_assertion_mismatch"
        );
    }

    [Test]
    public void TheSelectedIdentityNeedsOneCompleteAcyclicResolutionAndRetainedOwners()
    {
        var fixture = Create();
        var target = fixture.Target;
        Refuses(
            fixture with
            {
                Target = target with { Members = [target.Members[0]] },
            },
            "invalid_selected_identity_component"
        );
        Refuses(
            fixture with
            {
                Target = target with { Members = [target.Members[0], target.Members[0]] },
            },
            "invalid_selected_identity_component"
        );
        Refuses(
            fixture with
            {
                Target = target with { Redirects = [] },
            },
            "invalid_selected_identity_redirects"
        );
        Refuses(
            fixture with
            {
                Target = target with { OwnerObservations = [] },
            },
            "invalid_selected_identity_owners"
        );
        Refuses(
            fixture with
            {
                Target = target with { OriginalPersonUuid = target.ResolvedPersonUuid },
            },
            "original_review_not_accepted"
        );
    }

    [TestCase("relationship")]
    [TestCase("alias")]
    [TestCase("actor")]
    [TestCase("assertion")]
    public void CrossFieldReferencesCannotBeDiscardedDuringFieldProjection(string foreignReference)
    {
        var fixture = Create();
        var other = Field(
            "Personnel:\nAnother Person aka Other Name - vocals",
            fixture.Current.Coordinates.SourceUuid,
            fixture.Current.Coordinates.ResourceUuid,
            "/notes"
        );
        var assertion = other.Scope.Extraction.Assertions.Single();
        var relationship = assertion.Relationships.Single() with
        {
            ReferenceUuids =
            [
                assertion.ActorReferenceUuid,
                fixture.Current.Coordinates.ActorReferenceUuid,
            ],
        };
        assertion = foreignReference switch
        {
            "relationship" => assertion with { Relationships = [relationship] },
            "alias" => assertion with
            {
                Aliases =
                [
                    assertion.Aliases[0] with
                    {
                        ReferenceUuid = fixture.Current.Coordinates.ActorReferenceUuid,
                    },
                ],
            },
            "actor" => assertion with
            {
                ActorReferenceUuid = fixture.Current.Coordinates.ActorReferenceUuid,
            },
            "assertion" => assertion with
            {
                AssertionUuid = fixture.Current.Coordinates.AssertionUuid,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(foreignReference)),
        };
        var extraction = fixture.Current.Scope.Extraction;
        var current = fixture.Current with
        {
            Scope = fixture.Current.Scope with
            {
                Extraction = extraction with
                {
                    Assertions = [.. extraction.Assertions, assertion],
                    Scopes = [.. extraction.Scopes, .. other.Scope.Extraction.Scopes],
                },
            },
        };
        Refuses(fixture with { Current = current }, "cross_field_reference");
    }

    [Test]
    public void OnePreparedPairSelectsOneThousandClaimsAndBuildsExactReviewedProofs()
    {
        const int count = 1_000;
        var prose =
            "The Band:\n"
            + string.Concat(Enumerable.Repeat("Andy Moresi aka Hot Dog - keyboards\n", count));
        var source = Guid.NewGuid();
        var resource = Guid.NewGuid();
        var original = Field(prose, source, resource);
        var current = Field(prose, source, resource);
        original.Scope.Extraction.Assertions.Should().HaveCount(count);
        current.Scope.Extraction.Assertions.Should().HaveCount(count);
        checker.TryPrepareFields(original, current, out var pair, out _).Should().BeTrue();
        var selected = new HashSet<Guid>();
        var target = Create().Target;
        for (var index = 0; index < count; index++)
        {
            var assertion = original.Scope.Extraction.Assertions[index];
            pair!
                .TryGetCurrentFor(
                    assertion.AssertionUuid,
                    assertion.ActorReferenceUuid,
                    out var destination,
                    out _
                )
                .Should()
                .BeTrue();
            destination!
                .Coordinates.AssertionUuid.Should()
                .Be(current.Scope.Extraction.Assertions[index].AssertionUuid);
            destination
                .Coordinates.ActorReferenceUuid.Should()
                .Be(current.Scope.Extraction.Assertions[index].ActorReferenceUuid);
            selected.Add(destination.Coordinates.AssertionUuid).Should().BeTrue();
            if (index != 0 && index != count / 2 && index != count - 1)
            {
                continue;
            }
            var review = new SourceActorAliasReviewState(
                Guid.NewGuid(),
                original.Coordinates with
                {
                    AssertionUuid = assertion.AssertionUuid,
                    ActorReferenceUuid = assertion.ActorReferenceUuid,
                },
                Hash(JsonSerializer.SerializeToUtf8Bytes(assertion)),
                assertion,
                original.Scope,
                Guid.NewGuid(),
                1,
                "accepted",
                target.OriginalPersonUuid,
                Guid.NewGuid()
            );
            pair.TryCreate(review, target, out var proof, out _).Should().BeTrue();
            proof!.Current.Coordinates.Should().Be(destination!.Coordinates);
            proof.OriginalReview.LastOperationUuid.Should().Be(review.LastOperationUuid);
            proof.OriginalReview.SelectedPersonUuid.Should().Be(target.OriginalPersonUuid);
        }
        selected.Should().HaveCount(count);
    }

    [Test]
    public void CallerListMutationCannotChangeAPreparedPairOrAnAlreadyReturnedProof()
    {
        var fixture = Create();
        var mutableMembers = fixture.Target.Members.ToArray();
        fixture = fixture with { Target = fixture.Target with { Members = mutableMembers } };
        checker
            .TryPrepareFields(fixture.Original, fixture.Current, out var pair, out _)
            .Should()
            .BeTrue();
        pair!.TryCreate(fixture.Review, fixture.Target, out var first, out _).Should().BeTrue();
        var originalAssertions =
            (IList<SourceActorAliasAssertion<NormalizedEvidenceReference>>)
                fixture.Original.Scope.Extraction.Assertions;
        var currentAssertions =
            (IList<SourceActorAliasAssertion<NormalizedEvidenceReference>>)
                fixture.Current.Scope.Extraction.Assertions;
        var qualifiers =
            (IList<NormalizedEvidenceReference>)currentAssertions[0].Context.Qualifiers;
        qualifiers[0] = qualifiers[0] with { Quote = "changed caller qualifier" };
        originalAssertions[0] = originalAssertions[0] with { Name = "changed caller original" };
        currentAssertions[0] = currentAssertions[0] with { Name = "changed caller current" };
        mutableMembers[0] = mutableMembers[0] with { Revision = 999 };

        pair.TryCreate(first!.OriginalReview, first.Target, out var recovered, out _)
            .Should()
            .BeTrue();
        recovered!.Original.Scope.Extraction.Assertions[0].Name.Should().Be("Andy Moresi");
        recovered.Current.Scope.Extraction.Assertions[0].Name.Should().Be("Andy Moresi");
        recovered
            .Current.Scope.Extraction.Assertions[0]
            .Context.Qualifiers[0]
            .Quote.Should()
            .Be("-- and some or all of the following --");
        recovered.Target.Members[0].Revision.Should().Be(3);
        var exposed =
            (IList<SourceActorAliasAssertion<NormalizedEvidenceReference>>)
                first.Current.Scope.Extraction.Assertions;
        Action mutate = () => exposed[0] = exposed[0] with { Name = "mutate returned proof" };
        mutate.Should().Throw<NotSupportedException>();
        pair.TryGetCurrentFor(
                first.Original.Coordinates.AssertionUuid,
                first.Original.Coordinates.ActorReferenceUuid,
                out var destination,
                out _
            )
            .Should()
            .BeTrue();
        destination!.Scope.Extraction.Assertions[0].Name.Should().Be("Andy Moresi");
    }

    private void Refuses(Fixture fixture, string reason)
    {
        checker
            .TryCreate(
                fixture.Review,
                fixture.Original,
                fixture.Current,
                fixture.Target,
                out var proof,
                out var actual
            )
            .Should()
            .BeFalse();
        proof.Should().BeNull();
        actual.Should().Be(reason);
    }

    private static Fixture Create()
    {
        var source = Guid.NewGuid();
        var resource = Guid.NewGuid();
        var original = Field(Prose, source, resource);
        var current = Field(Prose, source, resource);
        var person = Guid.NewGuid();
        var resolved = Guid.NewGuid();
        var assertion = original.Scope.Extraction.Assertions[0];
        var review = new SourceActorAliasReviewState(
            Guid.NewGuid(),
            original.Coordinates,
            Hash(JsonSerializer.SerializeToUtf8Bytes(assertion)),
            assertion,
            original.Scope,
            Guid.NewGuid(),
            7,
            "accepted",
            person,
            Guid.NewGuid()
        );
        var target = new SourceActorReviewTarget(
            person,
            resolved,
            [new(person, resolved, 3), new(resolved, resolved, 4)],
            [new(person, resolved, Guid.NewGuid())],
            [new(Guid.NewGuid(), 1, 0, null), new(Guid.NewGuid(), 2, 1, Guid.NewGuid())]
        );
        return new(review, original, current, target);
    }

    private static SourceActorReviewField Field(
        string prose,
        Guid source,
        Guid resource,
        string pointer = "/description"
    )
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new Dictionary<string, string> { [pointer[1..]] = prose }
        );
        var input = new NormalizationInputRevision
        {
            Uuid = Guid.NewGuid(),
            Resource = new("archive.org", "item-metadata", "one-recording"),
            Payload = bytes,
            Sha256 = Hash(bytes),
            MediaType = "application/json",
            CaptureVersion = "test-v1",
        };
        var evidence = new NormalizedEvidenceReference(
            input.Uuid,
            pointer,
            0,
            prose.EnumerateRunes().Count(),
            prose
        );
        var extraction = new RetainedSourceActorAliasExtractor().Extract(
            input,
            [new("description", new(prose, evidence))]
        );
        var origin = new ProviderObservationOrigin(
            input.Resource,
            new(input.Uuid, ""),
            ProviderObservationScope.ResourceItem
        );
        var scope = new NormalizationRecordingSourceActorAliasScope(
            RetainedSourceActorAliasExtractor.Version,
            input.Uuid,
            input.Resource,
            origin,
            extraction
        );
        var assertion = extraction.Assertions[0];
        var coordinates = new SourceActorAliasCoordinates(
            source,
            input.Resource,
            resource,
            input.Uuid,
            pointer,
            scope.ProducerVersion,
            assertion.AssertionUuid,
            assertion.ActorReferenceUuid,
            assertion.FieldSha256
        );
        return new(Guid.NewGuid(), coordinates, scope, prose, evidence);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record Fixture(
        SourceActorAliasReviewState Review,
        SourceActorReviewField Original,
        SourceActorReviewField Current,
        SourceActorReviewTarget Target
    );
}
