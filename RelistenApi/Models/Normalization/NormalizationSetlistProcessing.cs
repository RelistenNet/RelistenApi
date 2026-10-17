using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>The input is the actual retained page; AccountIdentity names its qualified provider member.</summary>
public sealed record NormalizationSetlistProcessingRequest(
    NormalizationBindingInputReference Input,
    NormalizationResourceKey AccountIdentity,
    Guid? ExpectedArtistUuid = null
);

/// <summary>A requested account/scope identity, independent of an accepted music edition.</summary>
public sealed record NormalizationSetlistProcessingRegistration(
    Guid RegistrationUuid,
    NormalizationResourceKey AccountIdentity,
    Guid? ExpectedArtistUuid
);

public sealed record NormalizationPreparedSetlistAccount(
    ProviderSetlistObservation Account,
    NormalizationSetlistBindingSnapshot Binding
);

/// <summary>All matching account observations within one actual input, including duplicate accounts.</summary>
public sealed record NormalizationSetlistPreparedInput(
    NormalizationResourceKey Contributor,
    long ExpectedContributionRevision,
    NormalizationBindingInputReference Input,
    IReadOnlyList<NormalizationPreparedSetlistAccount> Accounts
);

/// <summary>Original membership, account assertions and binding clocks; saved once before admission.</summary>
public sealed record NormalizationSetlistPreparation(
    long? ExpectedConfigurationGeneration,
    IReadOnlyList<NormalizationInputContributionRevision> ExpectedContributions,
    IReadOnlyList<NormalizationSetlistPreparedInput> Inputs
);

public enum NormalizationSetlistProcessingDisposition
{
    Published,
    Unresolved,
    Stale,
}

/// <summary>Published means evidenced proposals became current, even when catalog bindings remain unresolved.</summary>
public sealed record NormalizationSetlistProcessingResult(
    Guid AttemptUuid,
    Guid RegistrationUuid,
    NormalizationSetlistProcessingDisposition Disposition,
    string Reason,
    bool Replayed = false
);

public sealed record NormalizationSetlistProcessingAttempt(
    Guid AttemptUuid,
    NormalizationSetlistProcessingRegistration Registration,
    NormalizationSetlistProcessingRequest Request,
    string ProcessingVersion,
    NormalizationSetlistPreparation? Preparation,
    NormalizationSetlistProcessingResult? Result
);

/// <summary>An evidenced passage proposal, never an accepted occurrence identity or inferred position.</summary>
public sealed record NormalizationSetlistPassageProposal(
    Guid ProposalUuid,
    ProviderPassageObservation Passage
);

/// <summary>The complete account retains empty sets, annotations and unknown positions alongside passage proposals.</summary>
public sealed record NormalizationSetlistAccountProposal(
    ProviderSetlistObservation Account,
    IReadOnlyList<NormalizationSetlistPassageProposal> Passages
);

public sealed record NormalizationSetlistInputExtraction(
    Guid InputRevisionUuid,
    IReadOnlyList<NormalizationSetlistAccountProposal> Accounts,
    IReadOnlyList<ProviderDecodeIssue> DecodeIssues
);

public sealed record NormalizationSetlistProposalBatch(
    Guid AttemptUuid,
    Guid RegistrationUuid,
    string ProcessingVersion,
    NormalizationSetlistPreparation Preparation,
    NormalizationWorkTicket Ticket,
    IReadOnlyList<NormalizationSetlistInputExtraction> Extractions
);
