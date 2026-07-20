using Relisten.Accounts.Contracts.Accounts;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Identity.Usernames;

namespace RelistenUserService.Identity;

public static class AccountProfileFactory
{
    public static AccountProfileResponse Create(User user, Guid nativeSessionId)
    {
        DateTimeOffset? changeAvailableAt = user.UsernameChangedAt is null
            ? null
            : user.UsernameChangedAt.Value + UsernamePolicy.ChangeCooldown;

        return new AccountProfileResponse(
            1,
            user.Id,
            user.Username,
            user.UsernameVersion,
            user.UsernameReviewedAt is null,
            user.UsernameReviewedAt,
            changeAvailableAt,
            nativeSessionId);
    }
}
