namespace RelistenApiTests;

public class TestUtils
{
    public static string ReadFixture(string path)
    {
        return File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, $@"Fixtures/{path}"));
    }
}
