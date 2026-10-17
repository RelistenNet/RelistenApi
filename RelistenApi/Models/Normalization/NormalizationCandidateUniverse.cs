using System;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// A candidate pool whose membership or matching facts can change. Global domains use the
/// empty UUID; repertoire is scoped to one artist. Keys identify pools, not accepted entities.
/// </summary>
public sealed record NormalizationCandidateUniverseKey(string Domain, Guid ScopeUuid)
{
    public static NormalizationCandidateUniverseKey People { get; } = new("people", Guid.Empty);
    public static NormalizationCandidateUniverseKey Venues { get; } = new("venues", Guid.Empty);
    public static NormalizationCandidateUniverseKey Compositions { get; } =
        new("compositions", Guid.Empty);

    public static NormalizationCandidateUniverseKey ForRepertoire(Guid artistUuid)
    {
        if (artistUuid == Guid.Empty)
        {
            throw new ArgumentException(
                "Repertoire candidate scopes require an artist UUID.",
                nameof(artistUuid)
            );
        }
        return new("repertoire", artistUuid);
    }
}

/// <summary>
/// The generation read with a candidate pool and its facts, including empty results. It
/// protects against new rivals that cannot appear in an existing candidate's owner read set.
/// Missing repertoire clocks read as zero; admission initializes and checks that observation.
/// </summary>
public sealed record NormalizationCandidateUniverseDependency(
    NormalizationCandidateUniverseKey Universe,
    long Generation
);
