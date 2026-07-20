using RelistenUserService.Identity;

namespace RelistenUserService.Authentication;

public sealed record DevelopmentPersona(
    string Id,
    string Label,
    string Provider,
    ExternalIdentityProfile Profile);

public static class DevelopmentPersonaCatalog
{
    private const string GoogleIssuer = "https://accounts.google.com";
    private const string AppleIssuer = "https://appleid.apple.com";

    public static readonly IReadOnlyList<DevelopmentPersona> All =
    [
        new("google-alice", "Alice · ordinary Google email", "google", new(
            GoogleIssuer, "dev-google-alice", "alice@example.test", true, false)),
        new("google-shared", "Shared email · Google subject", "google", new(
            GoogleIssuer, "dev-google-shared", "shared@example.test", true, false)),
        new("apple-shared", "Shared email · Apple subject", "apple", new(
            AppleIssuer, "dev-apple-shared", "shared@example.test", true, false)),
        new("apple-private-relay", "Apple private relay", "apple", new(
            AppleIssuer,
            "dev-apple-private-relay",
            "listener_42@privaterelay.appleid.com",
            true,
            true)),
        new("google-no-email", "Google subject · no email returned", "google", new(
            GoogleIssuer, "dev-google-no-email", null, null, null))
    ];

    public static DevelopmentPersona? Find(string id, string provider) =>
        All.SingleOrDefault(persona => persona.Id == id && persona.Provider == provider);
}
