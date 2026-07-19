namespace RelistenUserService.Persistence;

public static class UuidV7Constraint
{
    public const string Sql = "uuid_extract_version(id) IS NOT DISTINCT FROM 7";
}
