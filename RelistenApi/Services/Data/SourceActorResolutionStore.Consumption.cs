namespace Relisten.Data;

public sealed partial class SourceActorResolutionStore
{
    /// <summary>Complete PostgreSQL source-actor view before transfer; distinct from compact checkpoint bytes.</summary>
    public const int MaximumSourceConsumptionBytes = 32 * 1024 * 1024;

    // One exact expression measures and reads the consumed view. Source actors do not consume
    // recording credits, unresolved credit passages or title proposals. Every other producer
    // value remains intact, including malformed additional input shapes that must still fail.
    // The durable/full public batch and its producer identity are never rewritten.
    internal const string SourceActorConsumptionProjectionSql = """
        CASE WHEN jsonb_typeof(batch->'AdditionalExtractions')='array' THEN
            jsonb_set(
                batch #- '{Extraction,Credits}' #- '{Extraction,UnresolvedCredits}' #- '{Extraction,Titles}',
                '{AdditionalExtractions}',
                (SELECT COALESCE(jsonb_agg(
                    additional.value #- '{Extraction,Credits}' #- '{Extraction,UnresolvedCredits}' #- '{Extraction,Titles}'
                    ORDER BY additional.ordinality
                ),'[]'::jsonb)
                FROM jsonb_array_elements(batch->'AdditionalExtractions') WITH ORDINALITY AS additional(value,ordinality))
            )
        ELSE batch #- '{Extraction,Credits}' #- '{Extraction,UnresolvedCredits}' #- '{Extraction,Titles}' END
        """;
}
