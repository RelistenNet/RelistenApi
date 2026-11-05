using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace RelistenUserService.Persistence;

/// <summary>Persists complete typed strings; the decoded UUID is never the storage representation.</summary>
public sealed class AccountIdConverter<T>()
    : ValueConverter<T, string>(id => id.Value, value => Parse(value))
    where T : struct, IAccountId<T>
{
    private static T Parse(string value) => T.Parse(value);
}

public sealed class AccountIdValueGenerator<T> : ValueGenerator<T>
    where T : struct, IAccountId<T>
{
    public override bool GeneratesTemporaryValues => false;

    public override T Next(EntityEntry entry) => T.New();
}

internal static class AccountIdConstraint
{
    // The eleventh Crockford digit contains the UUID version nibble (7), followed by one random bit.
    public static string Sql(string prefix, string column = "id") =>
        $"{column} ~ '^{prefix}_[0-7][0-9a-hjkmnp-tv-z]{{9}}[ef][0-9a-hjkmnp-tv-z]{{15}}$'";
}
