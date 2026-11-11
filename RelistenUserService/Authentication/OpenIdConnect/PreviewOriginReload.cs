using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Client;
using OpenIddict.Core;
using OpenIddict.EntityFrameworkCore.Models;
using RelistenUserService.Configuration;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Persistence;

namespace RelistenUserService.Authentication.OpenIdConnect;

/// <summary>
/// Activates an exact preview-origin revision only after its OAuth callbacks and
/// client options are ready. The fixed production/local clients are never replaced.
/// </summary>
public sealed class PreviewOriginReload(
    IServiceScopeFactory scopes,
    AccountsRuntimeConfiguration runtime,
    ReloadableClientOptionsCache clientOptionsCache,
    IOptionsFactory<OpenIddictClientOptions> clientOptionsFactory,
    IOptionsMonitor<OpenIddictClientOptions> clientOptions,
    TimeProvider clock
)
{
    public async Task ApplyAsync(
        long revision,
        IEnumerable<string> origins,
        CancellationToken cancellationToken = default
    )
    {
        var candidate = WebOriginRegistry.Parse(revision, origins);
        var registry = runtime.BrowserOrigins;
        await registry.SessionCreationAndReload.WaitAsync(cancellationToken);
        var previous = registry.Current;
        try
        {
            if (
                candidate.Revision == previous.Revision
                && candidate
                    .Previews.Select(value => value.Origin)
                    .SequenceEqual(previous.Previews.Select(value => value.Origin))
            )
            {
                return;
            }
            if (candidate.Revision <= previous.Revision)
            {
                throw new InvalidOperationException(
                    "A preview revision cannot move backwards or change in place."
                );
            }

            // Preserve fixed/continuously active identities. A newly added origin
            // gets a fresh incarnation, so a retired login cannot resume on re-add.
            candidate = registry.PrepareRegistrations(candidate);
            // Prepare client options before touching persistence. New origins
            // stay unreachable until the active origin policy is published.
            _ = clientOptions.CurrentValue;
            registry.Configure(candidate);
            var preparedOptions = clientOptionsFactory.Create(Options.DefaultName);
            await ReconcilePersistenceAsync(previous, candidate, cancellationToken);
            clientOptionsCache.Publish(preparedOptions);
            registry.Activate(candidate);
        }
        catch
        {
            registry.Configure(previous);
            throw;
        }
        finally
        {
            registry.SessionCreationAndReload.Release();
        }
    }

    private async Task ReconcilePersistenceAsync(
        WebOriginSnapshot previous,
        WebOriginSnapshot candidate,
        CancellationToken cancellationToken
    )
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        var applications =
            scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var application =
            await applications.FindByClientIdAsync(
                AuthenticationConstants.WebClientId,
                cancellationToken
            )
            ?? throw new InvalidOperationException(
                "The baseline web client must be initialized first."
            );

        await WebClientApplicationInitializer.ValidateAsync(
            applications,
            application,
            runtime,
            cancellationToken
        );
        var descriptor = new OpenIddictApplicationDescriptor();
        await applications.PopulateAsync(descriptor, application, cancellationToken);
        var persistedOrigins = descriptor
            .RedirectUris.Select(uri => uri.AbsoluteUri)
            .Where(WebOriginRegistry.IsPreviewCallback)
            .Select(uri => uri[..^"/auth/session/callback".Length])
            .ToArray();
        descriptor.RedirectUris.RemoveWhere(uri =>
            WebOriginRegistry.IsPreviewCallback(uri.AbsoluteUri)
        );
        descriptor.RedirectUris.UnionWith(candidate.Previews.Select(value => value.Callback));

        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken
        );
        try
        {
            await applications.UpdateAsync(application, descriptor, cancellationToken);

            var retiredOrigins = previous
                .Previews.Select(value => value.Origin)
                .Union(persistedOrigins)
                .Except(candidate.Previews.Select(value => value.Origin))
                .ToArray();
            var now = clock.GetUtcNow();
            await database
                .Sessions.Where(session =>
                    session.Purpose == IdentitySessionPurposes.Web
                    && session.RevokedAt == null
                    && retiredOrigins.Contains(session.WebOrigin!)
                )
                .ExecuteUpdateAsync(
                    setters =>
                        setters
                            .SetProperty(session => session.RevokedAt, now)
                            .SetProperty(session => session.UpdatedAt, now),
                    cancellationToken
                );

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            // The manager caches after SaveChanges, before our transaction commits.
            // A rolled-back cached entity could reject a valid previous callback.
            var cache = scope.ServiceProvider.GetRequiredService<
                IOpenIddictApplicationCache<OpenIddictEntityFrameworkCoreApplication<Guid>>
            >();
            await cache.RemoveAsync(
                (OpenIddictEntityFrameworkCoreApplication<Guid>)application,
                CancellationToken.None
            );
            throw;
        }
    }
}
