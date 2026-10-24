namespace Relisten.Normalization.Temporal;

/// Normalization runs on the shipped ingestion workers: its workflows on the workflow queue,
/// its database steps on the database queue (RelistenApi's IngestionQueues uses these values).
public static class NormalizationQueues
{
    public const string Workflows = "ingest-workflows";
    public const string Database = "ingest-db";
}
