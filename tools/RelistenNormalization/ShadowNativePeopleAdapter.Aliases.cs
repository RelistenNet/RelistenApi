using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

public sealed partial class ShadowNativePeopleAdapter
{
    private readonly RetainedSourceActorAliasExtractor aliasExtractor = new();

    private void ExtractAliases(
        ShadowCatalogInput input,
        ShadowProviderRecordingObservation recording,
        RecordingTextScope scope,
        Dictionary<string, ShadowNativePerson> people,
        List<NormalizationRecordingSourceActorAliasScope> aliases
    )
    {
        if (scope.Text.Count == 0)
        {
            return;
        }

        // Each retained field keeps its own local IDs and coverage. Repeated strings in
        // different fields or files cannot establish one shared actor account.
        var extraction = aliasExtractor.Extract(scope.Input, scope.Text);
        aliases.Add(
            new(
                RetainedSourceActorAliasExtractor.Version,
                scope.Input.Uuid,
                scope.Input.Resource,
                scope.RecordingOrigin,
                extraction,
                scope.FileOrigin
            )
        );

        foreach (var assertion in extraction.Assertions)
        {
            var key = ShadowCatalogIdentity.Key(
                "native_alias_actor",
                JsonSerializer.Serialize(
                    new
                    {
                        scope.Input.Uuid,
                        scope.RecordingOrigin,
                        scope.FileOrigin,
                        assertion.ActorReferenceUuid,
                    }
                )
            );
            var projection = PeopleActorProjection.Project(assertion, input.Artist.Uuid);
            if (input.Artist.Uuid == Guid.Empty)
            {
                projection = projection with
                {
                    Record = projection.Record with { ArtistUuids = new HashSet<Guid>() },
                };
            }

            var evidence = new[]
            {
                assertion.NameEvidence,
                assertion.Context.Heading,
                assertion.Context.Statement,
            }
                .Concat(assertion.Context.Qualifiers)
                .Concat(assertion.Aliases.SelectMany(alias => alias.Evidence))
                .Concat(assertion.Relationships.SelectMany(relationship => relationship.Evidence))
                .Concat(assertion.Unresolved.SelectMany(issue => issue.Evidence))
                .Distinct()
                .ToArray();
            people.Add(
                key,
                new(
                    key,
                    assertion.Name,
                    null,
                    assertion.Aliases.Select(alias => alias.Wording).ToArray(),
                    scope.Input.Uuid,
                    scope.RecordingOrigin,
                    scope.FileOrigin,
                    recording.CatalogSourceUuid,
                    recording.CatalogShowUuid,
                    recording.CatalogBindingReason,
                    evidence,
                    null,
                    projection,
                    SourceActorAlias: assertion
                )
            );
        }
    }
}
