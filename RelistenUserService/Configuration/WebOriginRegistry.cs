using System.Collections.Immutable;
using System.Text.RegularExpressions;
using RelistenUserService.Authentication;

namespace RelistenUserService.Configuration;

/// <summary>
/// Keeps the fixed browser clients compatible while publishing exact preview origins.
/// Callers see immutable snapshots; a reload never mutates an options list in use.
/// </summary>
public sealed partial class WebOriginRegistry
{
    public const int MaximumPreviews = 4;

    private readonly ImmutableArray<string> _baselineOrigins;
    private ActiveOrigins _active;

    public WebOriginRegistry(IEnumerable<string> baselineOrigins)
    {
        _baselineOrigins = baselineOrigins.Distinct(StringComparer.Ordinal).ToImmutableArray();
        _active = new(WebOriginSnapshot.Empty, _baselineOrigins);
    }

    private WebOriginSnapshot _registrations = WebOriginSnapshot.Empty;

    public WebOriginSnapshot Current => Volatile.Read(ref _active).Snapshot;

    internal WebOriginSnapshot ConfiguredRegistrations => Volatile.Read(ref _registrations);

    // A session inserted after retirement's revocation query could otherwise become
    // valid when the same origin is re-added. Serialize creation with retirement.
    internal SemaphoreSlim SessionCreationAndReload { get; } = new(1, 1);

    public IReadOnlyList<string> Origins => Volatile.Read(ref _active).Origins;

    public string? RegistrationId(string origin)
    {
        var active = Volatile.Read(ref _active);
        if (!active.Origins.Contains(origin, StringComparer.Ordinal))
        {
            return null;
        }

        return origin switch
        {
            AuthenticationConstants.CanonicalWebOrigin =>
                AuthenticationConstants.CanonicalWebRegistration,
            AuthenticationConstants.LocalWebOrigin => AuthenticationConstants.LocalWebRegistration,
            AuthenticationConstants.StagingWebOrigin =>
                AuthenticationConstants.StagingWebRegistration,
            _ => active
                .Snapshot.Previews.FirstOrDefault(value => value.Origin == origin)
                ?.RegistrationId,
        };
    }

    internal WebOriginSnapshot PrepareRegistrations(WebOriginSnapshot candidate) =>
        candidate with
        {
            Previews = candidate
                .Previews.Select(preview =>
                    Current.Previews.FirstOrDefault(current => current.Origin == preview.Origin)
                    ?? preview with
                    {
                        RegistrationId = $"{preview.RegistrationId}-{Guid.NewGuid():N}",
                    }
                )
                .ToImmutableArray(),
        };

    internal void Configure(WebOriginSnapshot snapshot) =>
        Volatile.Write(ref _registrations, snapshot);

    internal void Activate(WebOriginSnapshot snapshot) =>
        Volatile.Write(
            ref _active,
            new ActiveOrigins(
                snapshot,
                _baselineOrigins
                    .Concat(snapshot.Previews.Select(value => value.Origin))
                    .ToImmutableArray()
            )
        );

    private sealed record ActiveOrigins(WebOriginSnapshot Snapshot, ImmutableArray<string> Origins);

    public static WebOriginSnapshot Parse(long revision, IEnumerable<string> origins)
    {
        if (revision < 1)
        {
            throw new ArgumentException("A preview revision must be positive.", nameof(revision));
        }

        var boundedOrigins = origins.Take(MaximumPreviews + 1).ToArray();
        if (boundedOrigins.Length > MaximumPreviews)
        {
            throw new ArgumentException(
                "At most four preview origins may be active.",
                nameof(origins)
            );
        }

        var previews = boundedOrigins
            .Select(origin =>
            {
                var match = PreviewOrigin().Match(origin);
                if (!match.Success)
                {
                    throw new ArgumentException(
                        "Preview origins must be exact https://relisten-web-previews-pr-<number>.tail09dbf.ts.net origins.",
                        nameof(origins)
                    );
                }

                return new PreviewWebOrigin(
                    origin,
                    $"relisten-web-preview-pr-{match.Groups[1].Value}"
                );
            })
            .OrderBy(value => value.Origin, StringComparer.Ordinal)
            .ToImmutableArray();
        if (previews.Select(value => value.Origin).Distinct().Count() != previews.Length)
        {
            throw new ArgumentException("Preview origins must be unique.", nameof(origins));
        }

        return new WebOriginSnapshot(revision, previews);
    }

    internal static bool IsPreviewCallback(string callback) =>
        callback.EndsWith("/auth/session/callback", StringComparison.Ordinal)
        && PreviewOrigin().IsMatch(callback[..^"/auth/session/callback".Length]);

    [GeneratedRegex(
        @"\Ahttps://relisten-web-previews-pr-([1-9][0-9]{0,9})\.tail09dbf\.ts\.net\z",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex PreviewOrigin();
}

public sealed record PreviewWebOrigin(string Origin, string RegistrationId)
{
    public Uri Callback => new($"{Origin}/auth/session/callback");
}

public sealed record WebOriginSnapshot(long Revision, ImmutableArray<PreviewWebOrigin> Previews)
{
    internal static WebOriginSnapshot Empty { get; } = new(0, []);
}
