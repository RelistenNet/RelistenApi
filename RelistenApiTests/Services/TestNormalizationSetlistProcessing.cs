using System.Text;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

/// <summary>Complete retained account proposals publish independently of accepted music and playback.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationSetlistProcessing
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationCatalogBindingService bindings = null!;
    private NormalizationSetlistProposalService store = null!;
    private NormalizationSetlistProcessor processor = null!;
    private readonly NormalizationProviderDecoder decoder = new();
    private readonly NormalizationSetlistExtractor extractor = new();

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        bindings = new(fixture.Db);
        store = new(fixture.Db);
        processor = new(store, evidence, bindings, decoder, extractor, fixture.Work);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task PageEnrollmentRetainsRepeatedPassagesEmptySetsAnnotationsAndNoAcceptedWrites()
    {
        var context = await Context();
        var before = await ProtectedState(context.Source);

        var attempt = await Process(context);

        attempt
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Published);
        attempt.Registration.AccountIdentity.Should().Be(context.Identity);
        attempt.Request.Input.Resource.Should().Be(context.Input.Resource);
        attempt.Request.Input.Resource.Should().NotBe(context.Identity);
        var batch = (await processor.GetAsync(attempt.Registration.RegistrationUuid))!;
        batch.Ticket.Inputs.Should().ContainSingle();
        batch
            .Preparation.Inputs.Should()
            .ContainSingle()
            .Which.Contributor.Should()
            .Be(context.Input.Resource);
        var proposal = batch.Extractions.Single().Accounts.Single();
        proposal.Account.Should().BeEquivalentTo(decoder.Decode(context.Input).Setlists.Single());
        proposal.Account.Sets.Select(set => set.Ordinal).Should().Equal(1, 2, 3);
        proposal.Account.Sets[1].EncoreNumber!.Value.Should().Be(1);
        proposal.Account.Passages.Select(passage => passage.SetOrdinal).Should().Equal(1, 1, 3);
        proposal
            .Passages.Select(passage => passage.Passage.Title.Value)
            .Should()
            .Equal("Song", "Song", "Reprise");
        proposal.Passages.Select(passage => passage.ProposalUuid).Should().OnlyHaveUniqueItems();
        proposal
            .Passages.Should()
            .OnlyContain(passage => passage.Passage.OccurrenceIdentity == null);
        proposal.Passages[0].Passage.Tape!.Value.Should().BeTrue();
        proposal
            .Passages[0]
            .Passage.PerformerAnnotations.Select(annotation => annotation.Kind)
            .Should()
            .Equal("cover_artist", "guest_artist");
        proposal
            .Passages[0]
            .Passage.Annotations.Single()
            .Text.Value.Should()
            .Be("Original tease wording");
        (await ProtectedState(context.Source)).Should().Be(before);
    }

    [Test]
    public async Task UnresolvedArtistImportedAccountAndPerformanceStillPublishTheRetainedAccount()
    {
        var input = await Page(Guid.NewGuid().ToString("D"), "unmapped-" + Guid.NewGuid(), 1);
        var account = decoder.Decode(input).Setlists.Single();

        var attempt = await processor.ProcessAsync(
            Guid.NewGuid(),
            Reference(input),
            account.Origin.Item
        );

        attempt
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Published);
        var binding = attempt.Preparation!.Inputs.Single().Accounts.Single().Binding;
        binding.Artist.Status.Should().Be(NormalizationCatalogBindingStatus.Missing);
        binding.ImportedSetlist.Value.Should().BeNull();
        binding.Performance.Value.Should().BeNull();
        (await processor.GetAsync(attempt.Registration.RegistrationUuid))!
            .Extractions.Single()
            .Accounts.Single()
            .Account.Should()
            .BeEquivalentTo(account);
    }

    [Test]
    public async Task ImportedAccountCanBindWhileItsPerformanceRemainsUnassigned()
    {
        var context = await Context();
        var importedUuid = await Imported(context);

        var attempt = await Process(context);

        attempt
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Published);
        var binding = attempt.Preparation!.Inputs.Single().Accounts.Single().Binding;
        binding.Artist.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        binding.ImportedSetlist.Value!.SetlistShowUuid.Should().Be(importedUuid);
        binding.Performance.Reason.Should().Be("missing_setlist_assignment");
    }

    [Test]
    public async Task ExpectedArtistQualifiesTheRegistrationWithoutChangingGlobalLookupScope()
    {
        var context = await Context();
        var artistUuid = await ArtistUuid(context.Source);
        var unscoped = await Process(context);

        var expected = await processor.ProcessAsync(
            Guid.NewGuid(),
            Reference(context.Input),
            context.Identity,
            artistUuid
        );

        expected
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Published);
        expected
            .Registration.RegistrationUuid.Should()
            .NotBe(unscoped.Registration.RegistrationUuid);
        expected.Registration.ExpectedArtistUuid.Should().Be(artistUuid);
        var binding = expected.Preparation!.Inputs.Single().Accounts.Single().Binding;
        binding.Artist.Value!.ArtistUuid.Should().Be(artistUuid);
        binding.ArtistScopeUuid.Should().BeNull();
    }

    [Test]
    public async Task OverlappingPagesKeepEveryDuplicateAccountAndTheirConflictingClaims()
    {
        var context = await Context();
        var first = await Process(context);
        var secondPage = await Page(
            context.ArtistKey,
            context.Identity.ProviderKey,
            2,
            "Conflicting title",
            duplicateTitle: "Another retained title"
        );

        var second = await processor.ProcessAsync(
            Guid.NewGuid(),
            Reference(secondPage),
            context.Identity
        );

        second.Registration.Should().Be(first.Registration);
        second.Result!.Disposition.Should().Be(NormalizationSetlistProcessingDisposition.Published);
        var batch = (await processor.GetAsync(first.Registration.RegistrationUuid))!;
        batch.Ticket.Inputs.Should().HaveCount(2);
        batch.Extractions.Should().HaveCount(2);
        batch
            .Extractions.Single(group => group.InputRevisionUuid == context.Input.Uuid)
            .Accounts.Should()
            .ContainSingle();
        var duplicates = batch
            .Extractions.Single(group => group.InputRevisionUuid == secondPage.Uuid)
            .Accounts;
        duplicates.Should().HaveCount(2);
        duplicates
            .Select(account => account.Passages[0].Passage.Title.Value)
            .Should()
            .Equal("Conflicting title", "Another retained title");
        duplicates
            .Select(account => account.Account.Origin.Evidence.JsonPointer)
            .Should()
            .OnlyHaveUniqueItems();
        batch
            .Extractions.SelectMany(group => group.Accounts)
            .SelectMany(account => account.Passages)
            .Select(passage => passage.ProposalUuid)
            .Should()
            .OnlyHaveUniqueItems();
        (await fixture.Work.GetInputContributionsAsync(Owner(first)))
            .Contributions.Select(contribution => contribution.Contributor)
            .Should()
            .BeEquivalentTo([context.Input.Resource, secondPage.Resource]);
    }

    [Test]
    public async Task MissingInitialMemberReplaysItsAttentionReceiptAfterThePageChanges()
    {
        var context = await Context();
        var empty = await Page(
            context.ArtistKey,
            context.Identity.ProviderKey,
            2,
            includeAccount: false
        );
        var attemptUuid = Guid.NewGuid();
        var missing = await processor.ProcessAsync(attemptUuid, Reference(empty), context.Identity);
        missing
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Unresolved);
        missing.Result.Reason.Should().Be("account_not_in_input");
        await Page(context.ArtistKey, context.Identity.ProviderKey, 2);

        var replay = await processor.ProcessAsync(attemptUuid, Reference(empty), context.Identity);

        replay.Result.Should().Be(missing.Result with { Replayed = true });
        replay.Preparation.Should().BeNull();
        (await processor.GetAsync(missing.Registration.RegistrationUuid)).Should().BeNull();
    }

    [Test]
    public async Task DisappearingPeerKeepsThePreviousCompletePublicationAndStaysPending()
    {
        var context = await Context();
        var first = await Process(context);
        var secondPage = await Page(context.ArtistKey, context.Identity.ProviderKey, 2);
        var complete = await processor.ProcessAsync(
            Guid.NewGuid(),
            Reference(secondPage),
            context.Identity
        );
        var previous = (await processor.GetAsync(first.Registration.RegistrationUuid))!;
        await Page(context.ArtistKey, context.Identity.ProviderKey, 1, includeAccount: false);

        var retry = await processor.RetryAsync(
            Guid.NewGuid(),
            complete.Registration.RegistrationUuid
        );

        retry.Result!.Disposition.Should().Be(NormalizationSetlistProcessingDisposition.Unresolved);
        retry.Result.Reason.Should().Be("peer_account_not_in_input");
        (await processor.GetAsync(first.Registration.RegistrationUuid))
            .Should()
            .BeEquivalentTo(previous);
        (await fixture.Work.GetSubjectAsync(Owner(first)))!
            .PublishedWorkUuid.Should()
            .Be(previous.AttemptUuid);
        (await IsPending(Owner(first))).Should().BeTrue();
        (await fixture.Work.GetInputContributionsAsync(Owner(first)))
            .Contributions.Should()
            .HaveCount(2);
    }

    [TestCase("revision")]
    [TestCase("resource")]
    [TestCase("account")]
    [TestCase("artist")]
    public async Task AttemptReplayRejectsAChangedRequestHeader(string changed)
    {
        var context = await Context();
        var original = await Process(context);
        var input = Reference(context.Input);
        var identity = context.Identity;
        Guid? artist = null;
        switch (changed)
        {
            case "revision":
                input = Reference(
                    await Page(context.ArtistKey, context.Identity.ProviderKey, 1, "Correction")
                );
                break;
            case "resource":
                input = input with
                {
                    Resource = input.Resource with { ProviderKey = context.ArtistKey + ":page=2" },
                };
                break;
            case "account":
                identity = identity with { ProviderKey = "another-account" };
                break;
            case "artist":
                artist = Guid.NewGuid();
                break;
        }

        Func<Task> replay = () =>
            processor.ProcessAsync(original.AttemptUuid, input, identity, artist);

        await replay.Should().ThrowAsync<NormalizationConflictException>();
        (await processor.GetAsync(original.Registration.RegistrationUuid))!
            .AttemptUuid.Should()
            .Be(original.AttemptUuid);
    }

    [Test]
    public async Task PhishNetUnorderedAccountKeepsUnknownPositionsAndExplicitArtistScope()
    {
        var source = await fixture.Source();
        var artistUuid = await ArtistUuid(source);
        var showId = Random.Shared.Next(1, int.MaxValue);
        var input = await Capture(
            new("phish.net", "shows", "fixture-" + Guid.NewGuid()),
            "phish-net-http-v1",
            "application/json",
            JsonSerializer.Serialize(
                new
                {
                    data = new[]
                    {
                        new
                        {
                            showid = showId,
                            artistid = 1,
                            artist_name = "Phish",
                            showdate = "2000-01-01",
                            setlist_notes = "Original account notes",
                        },
                    },
                }
            )
        );
        var account = decoder.Decode(input).Setlists.Single();

        var attempt = await processor.ProcessAsync(
            Guid.NewGuid(),
            Reference(input),
            account.Origin.Item,
            artistUuid
        );

        attempt
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Published);
        var batch = (await processor.GetAsync(attempt.Registration.RegistrationUuid))!;
        var proposal = batch.Extractions.Single().Accounts.Single();
        proposal.Account.Ordered.Should().BeFalse();
        proposal.Account.Sets.Should().BeEmpty();
        proposal.Passages.Should().BeEmpty();
        proposal.Account.Text.Single().Text.Value.Should().Be("Original account notes");
        attempt
            .Preparation!.Inputs.Single()
            .Accounts.Single()
            .Binding.ArtistScopeUuid.Should()
            .Be(artistUuid);
    }

    [TestCase("phantasytour.com")]
    [TestCase("jerrygarcia.com")]
    public async Task DetailAccountsPreserveOriginalFootnotesTransitionsAndDecoderIssues(
        string provider
    )
    {
        NormalizationInputRevision input;
        if (provider == "phantasytour.com")
        {
            var showId = Random.Shared.Next(1, int.MaxValue);
            input = await Capture(
                new(provider, "show", showId.ToString()),
                "phantasy-tour-http-v1",
                "application/json",
                $$$"""
                {"data":{"id":{{{showId}}},"dateTime":"1970-02-03T20:00:00-05:00","band":{"id":7,"name":"Band"},
                  "credits":["Compiled by Someone"],"footnotes":[{"id":2,"note":"Guest guitarist"}],
                  "sets":[{"id":1,"name":"First","songs":[{"id":9,"name":"Song","segue":true,"footnotes":[2,99]},{"id":9,"name":"Song","segue":false}]},{"id":2,"songs":[]}]}}
                """
            );
        }
        else
        {
            input = await Capture(
                new(provider, "mirror_show_page", "jerry/fixture-" + Guid.NewGuid()),
                "jerry-mirror-html:1",
                "text/html",
                """
                <p class="venue-name">Original &amp; Hall</p><p class="venue-name">Jerry García Band</p>
                <section data-set="Early"><ol><li><a href="/song/song/">Song</a> &gt;</li>
                <li><a href="/song/song/">Song</a>*</li></ol></section><ol data-set="Encore"></ol>
                """
            );
        }
        var decoded = decoder.Decode(input);
        var account = decoded.Setlists.Single();

        var attempt = await processor.ProcessAsync(
            Guid.NewGuid(),
            Reference(input),
            account.Origin.Item
        );

        attempt
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Published);
        var extraction = (
            await processor.GetAsync(attempt.Registration.RegistrationUuid)
        )!.Extractions.Single();
        var proposal = extraction.Accounts.Single();
        proposal.Account.Should().BeEquivalentTo(account);
        extraction.DecodeIssues.Should().BeEquivalentTo(decoded.Issues);
        proposal.Account.Sets.Should().HaveCount(2);
        proposal
            .Passages.Select(passage => passage.Passage.Title.Value)
            .Should()
            .Equal("Song", "Song");
        if (provider == "phantasytour.com")
        {
            proposal
                .Passages[0]
                .Passage.FootnoteNumbers.Select(number => number.Value)
                .Should()
                .Equal(2, 99);
            proposal.Passages[0].Passage.Segue!.Value.Should().BeTrue();
            extraction
                .DecodeIssues.Should()
                .ContainSingle(issue => issue.Code == "unresolved_footnote");
        }
        else
        {
            proposal.Passages[0].Passage.TransitionMarker!.Value.Should().Be(">");
            proposal.Passages[1].Passage.Annotations.Single().Text.Value.Should().Contain("*</li>");
        }
    }

    [Test]
    public async Task PreCancelledRequestCanResumeTheSameAttempt()
    {
        var context = await Context();
        var attemptUuid = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Func<Task> cancelled = () =>
            processor.ProcessAsync(
                attemptUuid,
                Reference(context.Input),
                context.Identity,
                cancellationToken: cancellation.Token
            );
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        var recovered = await processor.ProcessAsync(
            attemptUuid,
            Reference(context.Input),
            context.Identity
        );

        recovered
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Published);
        (await processor.GetAsync(recovered.Registration.RegistrationUuid))!
            .AttemptUuid.Should()
            .Be(attemptUuid);
    }

    [TestCase("input")]
    [TestCase("catalog")]
    [TestCase("membership")]
    public async Task SavedPreparationRejectsChangesBeforeAdmission(string changed)
    {
        var context = await Context();
        var published = await Process(context);
        var previous = (await processor.GetAsync(published.Registration.RegistrationUuid))!;
        var prepared = await Prepare(context);
        await Change(changed, context, prepared);

        var result = await processor.ProcessAsync(
            prepared.AttemptUuid,
            prepared.Request.Input,
            context.Identity
        );

        result.Result!.Disposition.Should().Be(NormalizationSetlistProcessingDisposition.Stale);
        (await store.GetAttemptBatchAsync(prepared.AttemptUuid)).Should().BeNull();
        (await processor.GetAsync(published.Registration.RegistrationUuid))
            .Should()
            .BeEquivalentTo(previous);
        (await IsPending(Owner(published))).Should().BeTrue();
    }

    [TestCase("input")]
    [TestCase("catalog")]
    [TestCase("membership")]
    public async Task SavedExtractionRemainsAuditableWhenPublicationBecomesStale(string changed)
    {
        var context = await Context();
        var published = await Process(context);
        var previous = (await processor.GetAsync(published.Registration.RegistrationUuid))!;
        var prepared = await Prepare(context);
        var ticket = await Admit(prepared);
        var batch = await Batch(prepared, ticket);
        await store.SaveExtractionAsync(batch);
        await Change(changed, context, prepared);

        var result = await processor.ProcessAsync(
            prepared.AttemptUuid,
            prepared.Request.Input,
            context.Identity
        );

        result.Result!.Disposition.Should().Be(NormalizationSetlistProcessingDisposition.Stale);
        (await store.GetAttemptBatchAsync(prepared.AttemptUuid)).Should().BeEquivalentTo(batch);
        (await processor.GetAsync(published.Registration.RegistrationUuid))
            .Should()
            .BeEquivalentTo(previous);
        (await fixture.Work.GetSubjectAsync(Owner(published)))!
            .PublishedWorkUuid.Should()
            .Be(previous.AttemptUuid);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SameInputMembershipChangeFencesAdmissionWithoutInventingNewPublicationFacts(
        bool admitted
    )
    {
        var context = await Context();
        var published = await Process(context);
        var prepared = await Prepare(context);
        if (admitted)
        {
            var ticket = await Admit(prepared);
            await store.SaveExtractionAsync(await Batch(prepared, ticket));
        }
        // This contributor owns the same immutable page already consumed by the batch.
        // Membership CAS changes, while the owner's actual fact union remains unchanged.
        await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            Owner(prepared),
            prepared.ProcessingVersion,
            new(
                new("fixture", "same-input-peer", Guid.NewGuid().ToString("D")),
                0,
                [context.Input.Resource],
                []
            )
        );

        var result = await processor.ProcessAsync(
            prepared.AttemptUuid,
            prepared.Request.Input,
            context.Identity
        );

        result
            .Result!.Disposition.Should()
            .Be(
                admitted
                    ? NormalizationSetlistProcessingDisposition.Published
                    : NormalizationSetlistProcessingDisposition.Stale
            );
        (await IsPending(Owner(published))).Should().BeFalse();
        var current = (await processor.GetAsync(published.Registration.RegistrationUuid))!;
        current.AttemptUuid.Should().Be(admitted ? prepared.AttemptUuid : published.AttemptUuid);
        current.Ticket.Inputs.Should().ContainSingle();
        current.Extractions.Should().ContainSingle();
    }

    [Test]
    public async Task PublicationCallbackRollbackKeepsTheCheckpointAndAllowsExactRecovery()
    {
        var context = await Context();
        var attempt = await Prepare(context);
        var ticket = await Admit(attempt);
        var batch = await Batch(attempt, ticket);
        await store.SaveExtractionAsync(batch);
        Func<Task> failingPublication = () =>
            fixture.Work.TryPublishAsync(
                ticket.WorkUuid,
                async (connection, transaction, ct) =>
                {
                    await store.WritePublishedAsync(connection, transaction, batch, ct);
                    throw new InvalidOperationException("Fixture failure after the domain write");
                }
            );

        await failingPublication.Should().ThrowAsync<InvalidOperationException>();

        (await store.GetAttemptAsync(attempt.AttemptUuid))!.Result.Should().BeNull();
        (await store.GetAttemptBatchAsync(attempt.AttemptUuid)).Should().BeEquivalentTo(batch);
        (await processor.GetAsync(attempt.Registration.RegistrationUuid)).Should().BeNull();
        (await fixture.Work.GetSubjectAsync(Owner(attempt)))!.PublishedWorkUuid.Should().BeNull();

        var recovered = await processor.ProcessAsync(
            attempt.AttemptUuid,
            attempt.Request.Input,
            context.Identity
        );
        recovered
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Published);
        var replay = await processor.ProcessAsync(
            attempt.AttemptUuid,
            attempt.Request.Input,
            context.Identity
        );
        replay.Result.Should().Be(recovered.Result with { Replayed = true });
        (await processor.GetAsync(attempt.Registration.RegistrationUuid))
            .Should()
            .BeEquivalentTo(batch);
    }

    [Test]
    public async Task EarlierConsumerVersionProducesAnImmutableAttentionReceipt()
    {
        var context = await Context();
        var attempt = await store.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            new(Reference(context.Input), context.Identity),
            "setlist-processing-old"
        );
        Func<Task> changedVersion = () =>
            store.GetOrCreateAttemptAsync(
                attempt.AttemptUuid,
                attempt.Request,
                NormalizationSetlistProcessor.Version
            );
        await changedVersion.Should().ThrowAsync<NormalizationConflictException>();

        var attention = await processor.ProcessAsync(
            attempt.AttemptUuid,
            attempt.Request.Input,
            context.Identity
        );

        attention
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Unresolved);
        attention.Result.Reason.Should().Be("processing_version_changed");
        attention.Preparation.Should().BeNull();
        var replay = await processor.ProcessAsync(
            attempt.AttemptUuid,
            attempt.Request.Input,
            context.Identity
        );
        replay.Result.Should().Be(attention.Result with { Replayed = true });
    }

    [TestCase("assertion")]
    [TestCase("identity")]
    [TestCase("input")]
    public async Task PreparationCannotSubstituteForgedOrUnrelatedAccountFacts(string changed)
    {
        var context = await Context();
        var attempt = await store.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            new(Reference(context.Input), context.Identity),
            NormalizationSetlistProcessor.Version
        );
        var account = decoder.Decode(context.Input).Setlists.Single();
        var binding = await bindings.BindSetlistAsync(context.Input, account);
        var input = Reference(context.Input);
        if (changed == "assertion")
        {
            account = account with
            {
                LiteralDate = account.LiteralDate! with { Value = "Invented date" },
            };
        }
        if (changed == "identity")
        {
            var origin = account.Origin with
            {
                Item = context.Identity with { ProviderKey = "unrelated-account" },
            };
            account = account with { Origin = origin };
            binding = binding with { Account = origin };
        }
        if (changed == "input")
        {
            input = input with { InputRevisionUuid = Guid.NewGuid() };
        }
        var preparation = new NormalizationSetlistPreparation(
            null,
            [],
            [new(context.Input.Resource, 0, input, [new(account, binding)])]
        );

        Func<Task> save = () => store.SavePreparationAsync(attempt.AttemptUuid, preparation);

        await save.Should().ThrowAsync<ArgumentException>();
        (await store.GetAttemptAsync(attempt.AttemptUuid))!.Preparation.Should().BeNull();
        (await processor.GetAsync(attempt.Registration.RegistrationUuid)).Should().BeNull();
    }

    private async Task<NormalizationSetlistProcessingAttempt> Prepare(AccountContext context)
    {
        var attempt = await store.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            new(Reference(context.Input), context.Identity),
            NormalizationSetlistProcessor.Version
        );
        var membership = await fixture.Work.GetInputContributionsAsync(Owner(attempt));
        var accounts = new List<NormalizationPreparedSetlistAccount>();
        foreach (
            var account in decoder
                .Decode(context.Input)
                .Setlists.Where(account => account.Origin.Item == context.Identity)
        )
        {
            accounts.Add(new(account, await bindings.BindSetlistAsync(context.Input, account)));
        }
        var contribution = membership.Contributions.SingleOrDefault(state =>
            state.Contributor == context.Input.Resource
        );
        var preparation = new NormalizationSetlistPreparation(
            membership.Owner?.DesiredGeneration,
            membership
                .Contributions.Select(state => new NormalizationInputContributionRevision(
                    state.Contributor,
                    state.Revision
                ))
                .ToArray(),
            [
                new(
                    context.Input.Resource,
                    contribution?.Revision ?? 0,
                    Reference(context.Input),
                    accounts
                ),
            ]
        );
        return await store.SavePreparationAsync(attempt.AttemptUuid, preparation);
    }

    private Task<NormalizationWorkTicket> Admit(NormalizationSetlistProcessingAttempt attempt) =>
        fixture.Work.BeginContributedBatchAsync(
            attempt.AttemptUuid,
            Owner(attempt),
            attempt.ProcessingVersion,
            NormalizationSetlistReadSet.Contributions(attempt.Preparation!),
            attempt.Preparation!.ExpectedConfigurationGeneration,
            expectedContributionRevisions: attempt.Preparation.ExpectedContributions
        );

    private async Task<NormalizationSetlistProposalBatch> Batch(
        NormalizationSetlistProcessingAttempt attempt,
        NormalizationWorkTicket ticket
    )
    {
        var extractions = new List<NormalizationSetlistInputExtraction>();
        foreach (var preparedInput in attempt.Preparation!.Inputs)
        {
            var input = (await evidence.GetInputAsync(preparedInput.Input.InputRevisionUuid))!;
            extractions.Add(extractor.Extract(input, decoder.Decode(input), preparedInput));
        }
        return new(
            attempt.AttemptUuid,
            attempt.Registration.RegistrationUuid,
            attempt.ProcessingVersion,
            attempt.Preparation,
            ticket,
            extractions
        );
    }

    private async Task Change(
        string changed,
        AccountContext context,
        NormalizationSetlistProcessingAttempt attempt
    )
    {
        switch (changed)
        {
            case "input":
                await Page(context.ArtistKey, context.Identity.ProviderKey, 1, "Corrected title");
                break;
            case "catalog":
                await fixture.Db.WithWriteConnection(c =>
                    c.ExecuteAsync(
                        "UPDATE artists SET name='Changed after preparation' WHERE id=@ArtistId",
                        context.Source
                    )
                );
                break;
            case "membership":
                var peer = await Page(
                    context.ArtistKey,
                    context.Identity.ProviderKey,
                    2,
                    "Unconsumed peer title"
                );
                var owner = (await fixture.Work.GetSubjectAsync(Owner(attempt)))!;
                await fixture.Work.BeginContributedWorkAsync(
                    Guid.NewGuid(),
                    Owner(attempt),
                    attempt.ProcessingVersion,
                    new(peer.Resource, 0, [peer.Resource], []),
                    owner.DesiredGeneration
                );
                break;
        }
    }

    private Task<NormalizationSetlistProcessingAttempt> Process(AccountContext context) =>
        processor.ProcessAsync(Guid.NewGuid(), Reference(context.Input), context.Identity);

    private static NormalizationSubjectKey Owner(NormalizationSetlistProcessingAttempt attempt) =>
        new("extractions", "setlist", attempt.Registration.RegistrationUuid);

    private static NormalizationBindingInputReference Reference(NormalizationInputRevision input) =>
        new(input.Uuid, input.Resource);

    private async Task<AccountContext> Context()
    {
        var source = await fixture.Source();
        var artistKey = Guid.NewGuid().ToString("D");
        await fixture.Db.WithWriteConnection(async c =>
        {
            var provider =
                await c.QuerySingleOrDefaultAsync<int?>(
                    "SELECT id FROM upstream_sources WHERE name='setlist.fm' ORDER BY id LIMIT 1"
                )
                ?? await c.QuerySingleAsync<int>(
                    "INSERT INTO upstream_sources(id,name,url) VALUES((SELECT GREATEST(1000,COALESCE(max(id),0)+1) FROM upstream_sources),'setlist.fm','https://provider.invalid') RETURNING id"
                );
            await c.ExecuteAsync(
                "UPDATE artists SET musicbrainz_id=@artistKey WHERE id=@ArtistId; INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier) VALUES(@ArtistId,@provider,@artistKey)",
                new
                {
                    source.ArtistId,
                    provider,
                    artistKey,
                }
            );
        });
        var accountKey = "fixture-" + Guid.NewGuid().ToString("N");
        return new(
            source,
            artistKey,
            new("setlist.fm", "setlist", accountKey),
            await Page(artistKey, accountKey, 1)
        );
    }

    private Task<Guid> ArtistUuid(CatalogSource source) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<Guid>("SELECT uuid FROM artists WHERE id=@ArtistId", source)
        );

    private Task<Guid> Imported(AccountContext context) =>
        fixture.Db.WithWriteConnection(async c =>
        {
            var venue = await c.ExecuteScalarAsync<long>(
                "INSERT INTO venues(artist_id,name,location,uuid,updated_at) VALUES(@ArtistId,'Fixture hall','Fixture town',@uuid,now()) RETURNING id",
                new { context.Source.ArtistId, uuid = Guid.NewGuid() }
            );
            return await c.ExecuteScalarAsync<Guid>(
                "INSERT INTO setlist_shows(artist_id,venue_id,date,upstream_identifier,uuid,updated_at) VALUES(@ArtistId,@venue,'2000-01-01',@key,@uuid,now()) RETURNING uuid",
                new
                {
                    context.Source.ArtistId,
                    venue,
                    key = context.Identity.ProviderKey,
                    uuid = Guid.NewGuid(),
                }
            );
        });

    private Task<NormalizationInputRevision> Page(
        string artistKey,
        string accountKey,
        int page,
        string title = "Song",
        bool includeAccount = true,
        string? duplicateTitle = null
    )
    {
        var members = new List<object>();
        if (includeAccount)
        {
            members.Add(Account(artistKey, accountKey, title));
            if (duplicateTitle != null)
            {
                members.Add(Account(artistKey, accountKey, duplicateTitle));
            }
        }
        return Capture(
            new("setlist.fm", "setlist-page", artistKey + ":page=" + page),
            "setlist-fm-http-v1",
            "application/json",
            JsonSerializer.Serialize(
                new
                {
                    page,
                    itemsPerPage = 20,
                    total = members.Count,
                    setlist = members,
                }
            )
        );
    }

    private static object Account(string artistKey, string accountKey, string title) =>
        new
        {
            id = accountKey,
            eventDate = "01-01-2000",
            artist = new { mbid = artistKey, name = "Fixture artist" },
            sets = new
            {
                set = new object[]
                {
                    new
                    {
                        name = "First",
                        song = new object[]
                        {
                            new
                            {
                                name = title,
                                tape = true,
                                info = "Original tease wording",
                                cover = new { mbid = "cover-id", name = "Cover performer" },
                                with = new { mbid = "guest-id", name = "Guest performer" },
                            },
                            new { name = "Song" },
                        },
                    },
                    new { encore = 1, song = Array.Empty<object>() },
                    new { name = "First", song = new[] { new { name = "Reprise" } } },
                },
            },
            info = "Original account notes",
        };

    private async Task<NormalizationInputRevision> Capture(
        NormalizationResourceKey resource,
        string version,
        string mediaType,
        string payload
    )
    {
        var current = await evidence.GetCurrentInputAsync(resource);
        return (
            await evidence.CaptureInputAsync(
                new(
                    Guid.NewGuid(),
                    resource,
                    Encoding.UTF8.GetBytes(payload),
                    mediaType,
                    version,
                    current?.Generation ?? 0,
                    DateTimeOffset.UtcNow
                )
            )
        ).Revision;
    }

    private Task<string> ProtectedState(CatalogSource source) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                """
                SELECT jsonb_build_object(
                    'music_revisions',(SELECT count(*) FROM normalization.music_revisions),
                    'editions',(SELECT count(*) FROM normalization.setlist_editions),
                    'occurrences',(SELECT count(*) FROM normalization.song_occurrences),
                    'source',(SELECT to_jsonb(s) FROM sources s WHERE id=@SourceId),
                    'tracks',(SELECT jsonb_agg(to_jsonb(t) ORDER BY t.uuid) FROM source_tracks t WHERE source_id=@SourceId)
                )::text
                """,
                source
            )
        );

    private async Task<bool> IsPending(NormalizationSubjectKey owner)
    {
        Guid? after = null;
        while (true)
        {
            var page = await fixture.Work.GetPendingSubjectsAsync(after, 1000);
            if (page.Any(subject => subject.Subject == owner))
            {
                return true;
            }
            if (page.Count < 1000)
            {
                return false;
            }
            after = page[^1].SubjectUuid;
        }
    }

    private sealed record AccountContext(
        CatalogSource Source,
        string ArtistKey,
        NormalizationResourceKey Identity,
        NormalizationInputRevision Input
    );
}
