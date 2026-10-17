using System;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// An owner observed with candidate facts, not a causal work dependency. Capture the complete
/// stamp in the same primary snapshot as facts and candidate generations. Receipts fence an
/// in-flight decision; durable pending checks use configuration/editorial revisions and the
/// consumed candidate pools, so reciprocal observations do not create no-op feedback loops.
/// </summary>
public sealed record NormalizationSubjectObservation(
    Guid SubjectUuid,
    long DesiredGeneration,
    long EditorialRevision,
    Guid? PublishedWorkUuid
);
