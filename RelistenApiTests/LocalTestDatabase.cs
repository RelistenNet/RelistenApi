using Dapper;
using Npgsql;

namespace RelistenApiTests;

// Each test process owns a catalog database. Tests never read connection settings
// from the application, so a local test run cannot replace a developer's catalog.
internal static class LocalTestDatabase
{
    public static string Name { get; } = "relisten_test_" + Guid.NewGuid().ToString("N");

    public static string DatabaseUrl =>
        $"postgresql://relisten:local_dev_password@127.0.0.1:15432/{Name}";

    public static string ConnectionString =>
        $"Host=127.0.0.1;Port=15432;Database={Name};Username=relisten;Password=local_dev_password";

    private static bool created;

    public static async Task CreateAsync()
    {
        await using var admin = OpenAdminConnection();
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE DATABASE {Name} TEMPLATE template0");
        created = true;

        await using var catalog = new NpgsqlConnection(ConnectionString);
        await catalog.OpenAsync();
        var schema = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog-schema.sql")
        );
        await catalog.ExecuteAsync(schema, commandTimeout: 120);
        await catalog.ExecuteAsync(Migrations.AddArchiveIntake.SchemaSql, commandTimeout: 120);
        await catalog.ExecuteAsync(
            Migrations.AddRelistenCollections.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddArchivePerformerReviews.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(Migrations.AddShowPerformances.SchemaSql, commandTimeout: 120);
        await catalog.ExecuteAsync(
            Migrations.AddExistingArchiveArtistPublication.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddNormalizationEvidence.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(Migrations.AddNormalizationWork.SchemaSql, commandTimeout: 120);
        await catalog.ExecuteAsync(
            Migrations.AddCanonicalPeopleAndVenues.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(Migrations.AddCanonicalMusic.SchemaSql, commandTimeout: 120);
        await catalog.ExecuteAsync(
            Migrations.AddNormalizationExtractionCache.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddNormalizationCandidateIndexes.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddNormalizationCatalogBindings.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddNormalizationMappingRegistry.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddNormalizationRecordingProcessing.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddNormalizationRecordingPages.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.FenceNormalizationArtistMappings.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddNormalizationImportedVenueBindings.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddNormalizationCandidateUniverses.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddNormalizationOwnerObservations.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddNormalizationCandidateFactIndexes.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddNormalizationSourceUuidBindings.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddNormalizationRecordingLookups.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddNormalizationRecordingInputGroups.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddNormalizationSetlistProcessing.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddRecordingCreditContributorSupport.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddCompositionAuthorshipAssertions.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddNormalizationPeopleResolution.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddImportedVenueResolutionIndexes.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddAcceptedSetlistStructures.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddSetlistCorrespondenceAuthorizations.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddMusicBrainzAcquisition.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddNormalizationExtractionFailureDiagnostics.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddNormalizationExtractionJobAttemptLimit.SchemaSql,
            commandTimeout: 120
        );
        await catalog.ExecuteAsync(
            Migrations.AddNormalizationPeopleAcceptance.SchemaSql,
            commandTimeout: 120
        );
    }

    public static async Task DropAsync()
    {
        if (!created)
        {
            return;
        }

        // Fixtures have stopped their workers before this assembly-level cleanup.
        // FORCE also closes pooled connections left by the application services.
        await using var admin = OpenAdminConnection();
        await admin.OpenAsync();
        await admin.ExecuteAsync($"DROP DATABASE {Name} WITH (FORCE)");
        created = false;
    }

    private static NpgsqlConnection OpenAdminConnection() =>
        new(
            "Host=127.0.0.1;Port=15432;Database=postgres;Username=relisten;Password=local_dev_password"
        );
}
