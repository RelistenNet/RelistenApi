using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>One accepted version. UUIDs identify passages and relationships independently of their labels or positions.</summary>
public abstract record MusicChange
{
    public Guid Uuid { get; init; }
    public long ExpectedRevision { get; init; }
    public string Basis { get; init; } = "provider_assertion";
    public IReadOnlyList<NormalizedEvidenceReference> Evidence { get; init; } = [];
    public bool Retracted { get; init; }
}

public sealed record RepertoireSongChange : MusicChange
{
    public Guid ArtistUuid { get; init; }
    public string DisplayTitle { get; init; } = "";
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

public sealed record CompositionChange : MusicChange
{
    public string DisplayTitle { get; init; } = "";
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

public sealed record ImportedSongAssignmentChange : MusicChange
{
    public Guid ImportedSongUuid { get; init; }
    public Guid RepertoireSongUuid { get; init; }
}

public sealed record RepertoireCompositionChange : MusicChange
{
    public Guid RepertoireSongUuid { get; init; }
    public Guid CompositionUuid { get; init; }
    public string MatchReason { get; init; } = "qualified_work_identity";
    public NormalizationResourceKey? MatchedWorkIdentity { get; init; }
    public IReadOnlyList<Guid> MatchedAuthorUuids { get; init; } = [];
    public bool CompleteAuthorship { get; init; }
}

public sealed record WorkCreditChange : MusicChange
{
    public Guid CompositionUuid { get; init; }
    public string Role { get; init; } = "writer";
    public string OriginalText { get; init; } = "";
    public Guid? PersonUuid { get; init; }
    public Guid? ArtistUuid { get; init; }
}

/// <summary>Provider identity is qualified by resource; a date alone is not an engagement identity.</summary>
public sealed record SetlistEditionChange : MusicChange
{
    public Guid ArtistUuid { get; init; }
    public Guid? ShowUuid { get; init; }
    public string LiteralDate { get; init; } = "";
    public bool Ordered { get; init; }
    public string Provider { get; init; } = "";
    public string ResourceType { get; init; } = "";
    public string ProviderKey { get; init; } = "";
    public bool Selected { get; init; }
}

public sealed record SongOccurrenceChange : MusicChange
{
    public Guid EditionUuid { get; init; }
    public int? Position { get; init; }
    public string? SetLabel { get; init; }
    public string Kind { get; init; } = "song";
    public Guid? RepertoireSongUuid { get; init; }
    public string OriginalText { get; init; } = "";
    public string TransitionAfter { get; init; } = "unknown";
    public Guid? ParentOccurrenceUuid { get; init; }
    public int? ComponentPosition { get; init; }
    public Guid? SupersedesOccurrenceUuid { get; init; }
    public string? ProviderItemKey { get; init; }
}

public sealed record TrackContentChange : MusicChange
{
    public Guid SourceTrackUuid { get; init; }
    public int Position { get; init; }
    public string Kind { get; init; } = "song";
    public string OriginalText { get; init; } = "";
    public Guid? RepertoireSongUuid { get; init; }
    public Guid? OccurrenceUuid { get; init; }
    public long? StartMs { get; init; }
    public long? EndMs { get; init; }
    public string MatchEvidence { get; init; } = "file_title";
}

public sealed record MusicExternalIdentityChange : MusicChange
{
    public Guid? RepertoireSongUuid { get; init; }
    public Guid? CompositionUuid { get; init; }
    public string Provider { get; init; } = "";
    public string ResourceType { get; init; } = "";
    public string ProviderKey { get; init; } = "";
}

/// <summary>Merge correspondence is explicit. A split supplies explicit relationship replacements and new identities.</summary>
public sealed record MusicMerge(
    string Domain,
    Guid RetiredUuid,
    Guid SurvivorUuid,
    long ExpectedRevision,
    long ExpectedSurvivorRevision
);

/// <summary>A split lists every affected global work owner, including a former redirect chain.</summary>
public sealed record MusicSplitTarget(Guid Uuid, long ExpectedRevision);

public sealed record MusicSplit(
    string Domain,
    Guid RetainedUuid,
    long ExpectedRevision,
    IReadOnlyList<MusicSplitTarget>? Targets = null
);

public sealed record MusicPublication
{
    public IReadOnlyList<RepertoireSongChange> RepertoireSongs { get; init; } = [];
    public IReadOnlyList<CompositionChange> Compositions { get; init; } = [];
    public IReadOnlyList<ImportedSongAssignmentChange> ImportedSongs { get; init; } = [];
    public IReadOnlyList<RepertoireCompositionChange> CompositionLinks { get; init; } = [];
    public IReadOnlyList<WorkCreditChange> WorkCredits { get; init; } = [];
    public IReadOnlyList<SetlistEditionChange> Editions { get; init; } = [];
    public IReadOnlyList<SongOccurrenceChange> Occurrences { get; init; } = [];
    public IReadOnlyList<TrackContentChange> TrackContents { get; init; } = [];
    public IReadOnlyList<MusicExternalIdentityChange> ExternalIdentities { get; init; } = [];
    public IReadOnlyList<MusicMerge> Merges { get; init; } = [];
    public IReadOnlyList<MusicSplit> Splits { get; init; } = [];
}

public sealed record MusicRecord<T>(
    T Value,
    long Revision,
    DateTime UpdatedAt,
    Guid DecisionUuid,
    Guid? RequestedUuid = null,
    Guid? RedirectUuid = null
);
