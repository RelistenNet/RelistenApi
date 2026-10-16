using RelistenApiTests;

// No namespace: NUnit runs this setup once around every fixture in the assembly,
// including the transaction tests under Services and the ingestion tests.
[SetUpFixture]
public class LocalIntegrationSetup
{
    [OneTimeSetUp]
    public async Task CreateCatalog()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            return;
        }

        try
        {
            await LocalTestDatabase.CreateAsync();
        }
        catch
        {
            await LocalTestDatabase.DropAsync();
            throw;
        }
    }

    [OneTimeTearDown]
    public Task DropCatalog() => LocalTestDatabase.DropAsync();
}
