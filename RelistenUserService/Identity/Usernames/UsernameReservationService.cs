using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Persistence;

namespace RelistenUserService.Identity.Usernames;

public sealed class UsernameReservationService(
    AccountsDbContext dbContext,
    AdvisoryLockService advisoryLocks,
    UsernamePolicy policy)
{
    private const string RandomAlphabet = "abcdefghijklmnopqrstuvwxyz234567";
    private const int MaximumAttempts = 12;

    public async Task<string> AllocateAsync(
        string? providerEmail,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var emailCandidate = policy.CandidateFromEmail(providerEmail);
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            var candidate = attempt == 0 && emailCandidate is not null
                ? emailCandidate
                : BuildRandomCandidate(emailCandidate);

            await advisoryLocks.LockUsernamesAsync([candidate], cancellationToken);
            if (await IsAvailableAsync(candidate, now, cancellationToken))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not allocate an available username.");
    }

    public async Task<bool> IsAvailableAsync(
        string username,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var expiredHold = await dbContext.UsernameHolds
            .SingleOrDefaultAsync(
                hold => hold.Username == username && hold.ReleaseAt <= now,
                cancellationToken);
        if (expiredHold is not null)
        {
            dbContext.UsernameHolds.Remove(expiredHold);
        }

        return !await dbContext.Users.AnyAsync(user => user.Username == username, cancellationToken)
            && !await dbContext.UsernameHolds.AnyAsync(
                hold => hold.Username == username && hold.ReleaseAt > now,
                cancellationToken);
    }

    private static string BuildRandomCandidate(string? emailCandidate)
    {
        var suffix = RandomString(emailCandidate is null ? 10 : 6);
        if (emailCandidate is null)
        {
            return $"listener_{suffix}";
        }

        var prefixLength = Math.Min(emailCandidate.Length, 30 - suffix.Length - 1);
        return $"{emailCandidate[..prefixLength]}_{suffix}";
    }

    private static string RandomString(int length)
    {
        Span<byte> bytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(bytes);
        Span<char> characters = stackalloc char[length];
        for (var index = 0; index < bytes.Length; index++)
        {
            characters[index] = RandomAlphabet[bytes[index] % RandomAlphabet.Length];
        }

        return new string(characters);
    }
}
