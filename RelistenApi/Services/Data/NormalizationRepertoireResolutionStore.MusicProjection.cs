namespace Relisten.Data;

public sealed partial class NormalizationRepertoireResolutionStore
{
    // Music authenticates the complete producer coordinates and title accounts, but does not
    // consume role-free people aliases. Keep that optional output in the durable/public batch
    // without charging it to this domain's snapshot budget or copying it into its preparation.
    // Apply this exact view at both initial read and publication recheck. Removing only these
    // two known paths preserves legacy absent/null properties and every other source value.
    internal const string RecordingMusicConsumptionProjectionSql = """
        CASE WHEN jsonb_typeof(batch->'AdditionalExtractions')='array' THEN
            jsonb_set(
                batch #- '{Extraction,SourceActorAliases}',
                '{AdditionalExtractions}',
                (SELECT COALESCE(jsonb_agg(
                    additional.value #- '{Extraction,SourceActorAliases}'
                    ORDER BY additional.ordinality
                ),'[]'::jsonb)
                FROM jsonb_array_elements(batch->'AdditionalExtractions') WITH ORDINALITY AS additional(value,ordinality))
            )
        ELSE batch #- '{Extraction,SourceActorAliases}' END
        """;
}
