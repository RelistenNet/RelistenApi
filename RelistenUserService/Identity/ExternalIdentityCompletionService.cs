using Microsoft.EntityFrameworkCore;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Identity.Usernames;
using RelistenUserService.Persistence;

namespace RelistenUserService.Identity;

public sealed class ExternalIdentityCompletionService(
    AccountsDbContext dbContext,
    AdvisoryLockService advisoryLocks,
    UsernameReservationService usernames,
    TimeProvider timeProvider)
{
    public async Task<User> CompleteAsync(
        ExternalIdentityProfile profile,
        CancellationToken cancellationToken)
    {
        Validate(profile);

        var existing = await FindAsync(profile, cancellationToken);
        if (existing is not null)
        {
            await RecordLoginAsync(existing, profile, cancellationToken);
            return existing.User;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        // Issuer and subject are the account-linking authority. Email is mutable provider
        // metadata and must never merge Apple and Google identities implicitly.
        await advisoryLocks.LockAsync(
            $"external-identity:{profile.Issuer}\n{profile.Subject}",
            cancellationToken);

        existing = await FindAsync(profile, cancellationToken);
        if (existing is not null)
        {
            await RecordLoginAsync(existing, profile, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return existing.User;
        }

        var now = timeProvider.GetUtcNow();
        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Username = await usernames.AllocateAsync(profile.Email, now, cancellationToken),
            CreatedAt = now,
            UpdatedAt = now,
            LastLoginAt = now
        };
        user.ExternalIdentities.Add(new ExternalIdentity
        {
            Id = Guid.CreateVersion7(),
            Issuer = profile.Issuer,
            ProviderSubject = profile.Subject,
            EmailAtProvider = profile.Email,
            EmailVerifiedAtProvider = profile.EmailVerified,
            EmailIsPrivateRelay = profile.EmailIsPrivateRelay,
            EmailObservedAt = profile.Email is null ? null : now,
            LastLoginAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return user;
    }

    private Task<ExternalIdentity?> FindAsync(
        ExternalIdentityProfile profile,
        CancellationToken cancellationToken) =>
        dbContext.ExternalIdentities
            .Include(identity => identity.User)
            .SingleOrDefaultAsync(
                identity => identity.Issuer == profile.Issuer
                    && identity.ProviderSubject == profile.Subject,
                cancellationToken);

    private async Task RecordLoginAsync(
        ExternalIdentity identity,
        ExternalIdentityProfile profile,
        CancellationToken cancellationToken)
    {
        if (identity.User.Status != UserStatuses.Active)
        {
            throw new AccountUnavailableException();
        }

        var now = timeProvider.GetUtcNow();
        identity.LastLoginAt = now;
        identity.UpdatedAt = now;
        identity.User.LastLoginAt = now;
        identity.User.UpdatedAt = now;

        if (profile.Email is not null)
        {
            identity.EmailAtProvider = profile.Email;
            identity.EmailVerifiedAtProvider = profile.EmailVerified;
            identity.EmailIsPrivateRelay = profile.EmailIsPrivateRelay;
            identity.EmailObservedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static void Validate(ExternalIdentityProfile profile)
    {
        if (!Uri.TryCreate(profile.Issuer, UriKind.Absolute, out _)
            || string.IsNullOrWhiteSpace(profile.Subject)
            || profile.Issuer.Length > 512
            || profile.Subject.Length > 512
            || profile.Email?.Length > 320)
        {
            throw new ArgumentException("The verified external identity is malformed.", nameof(profile));
        }
    }
}

public sealed class AccountUnavailableException : Exception;
