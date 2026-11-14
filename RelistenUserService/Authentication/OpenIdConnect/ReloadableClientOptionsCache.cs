using Microsoft.Extensions.Options;
using OpenIddict.Client;

namespace RelistenUserService.Authentication.OpenIdConnect;

/// <summary>
/// Supplies the normal options-monitor cache contract with atomic publication.
/// Candidate construction happens outside this cache, so a failed reload leaves
/// the previous valid options available to every request.
/// </summary>
public sealed class ReloadableClientOptionsCache : IOptionsMonitorCache<OpenIddictClientOptions>
{
    private readonly object _gate = new();
    private readonly OptionsCache<OpenIddictClientOptions> _cache = new();

    public OpenIddictClientOptions GetOrAdd(
        string? name,
        Func<OpenIddictClientOptions> createOptions
    )
    {
        lock (_gate)
        {
            return _cache.GetOrAdd(name, createOptions);
        }
    }

    public bool TryAdd(string? name, OpenIddictClientOptions options)
    {
        lock (_gate)
        {
            return _cache.TryAdd(name, options);
        }
    }

    public bool TryRemove(string? name)
    {
        lock (_gate)
        {
            return _cache.TryRemove(name);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _cache.Clear();
        }
    }

    internal void Publish(OpenIddictClientOptions options)
    {
        lock (_gate)
        {
            _cache.TryRemove(Options.DefaultName);
            _cache.TryAdd(Options.DefaultName, options);
        }
    }
}
