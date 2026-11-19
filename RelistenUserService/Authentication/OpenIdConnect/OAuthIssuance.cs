using Microsoft.EntityFrameworkCore;
using OpenIddict.Server;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Persistence;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace RelistenUserService.Authentication.OpenIdConnect;

internal static class OAuthIssuance
{
    // The purge takes FOR UPDATE. Read the user's current status while acquiring the
    // conflicting lock and hold it until OpenIddict's token records have committed.
    public static Task<User?> LockActiveUserAsync(
        AccountsDbContext database,
        UserId userId,
        CancellationToken cancellationToken
    ) =>
        database
            .Users.FromSqlInterpolated(
                $"""
                SELECT * FROM identity.users
                WHERE id = {userId.ToString()} AND status = {UserStatuses.Active}
                FOR SHARE
                """
            )
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);

    public static void Configure(OpenIddictServerBuilder options) =>
        options.AddEventHandler<ProcessSignInContext>(handler =>
            handler
                .UseScopedHandler<CommitIssuance>()
                .SetOrder(OpenIddictServerHandlers.AttachSignInParameters.Descriptor.Order + 1)
        );

    internal sealed class CommitIssuance(AccountsDbContext database)
        : IOpenIddictServerHandler<ProcessSignInContext>
    {
        public async ValueTask HandleAsync(ProcessSignInContext context)
        {
            // At this point OpenIddict has persisted the authorization code or tokens.
            // Commit before its response handlers send the redirect or token response.
            if (database.Database.CurrentTransaction is { } transaction)
            {
                await transaction.CommitAsync(context.CancellationToken);
            }
        }
    }
}
