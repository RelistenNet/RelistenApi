namespace RelistenApiTests.Reality;

internal static class RealityFixtures
{
    public const string FeatureMd5 = "9787672c804305d12c894564c66b651e";
    public static readonly Guid CornellEvent = Guid.Parse("01a14202-2800-7292-b696-2ae15226d1c8");

    public static string PathOf(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "Fixtures", "Reality", .. parts]);

    public static byte[] FeatureBlob(string component) =>
        File.ReadAllBytes(PathOf("features", FeatureMd5, $"{component}-v1.bin"));

    public static byte[] CornellReality() =>
        File.ReadAllBytes(PathOf("cornell-1977-05-08", "reality.binpb"));
}
