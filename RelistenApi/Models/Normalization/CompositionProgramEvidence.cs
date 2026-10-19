namespace Relisten.Api.Models.Normalization;

/// <summary>Original song and recording context; existing composition candidates are not required.</summary>
public sealed record CompositionProgramTarget(
    CompositionSelectionSong Song,
    CompositionSelectionTargetContext Context
);

/// <summary>The relationship proposed by a source reference, not a verified release identity.</summary>
public enum CompositionProgramReferenceKind
{
    Unknown,
    Program,
    Tribute,
    Soundtrack,
    Album,
}

/// <summary>
/// A retrieval proposal tied to one scoped song and original source string.
/// TitleEvidence quotes ProgramTitle exactly; ReferenceEvidence supplies the
/// enclosing wording that supports the proposed relationship. Both use the
/// original field's Unicode code-point offsets and pointer. A valid citation
/// permits retrieval, not acceptance of the program, composition or audio identity.
/// </summary>
public sealed record CompositionProgramReference(
    CompositionSelectionTargetKey Target,
    string ProgramTitle,
    CompositionProgramReferenceKind Kind,
    CompositionSelectionTargetOrigin Origin,
    string TextSha256,
    NormalizationTextEvidence TitleEvidence,
    NormalizationTextEvidence ReferenceEvidence
);
