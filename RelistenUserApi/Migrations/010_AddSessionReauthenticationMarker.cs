using SimpleMigrations;

namespace Relisten.UserApi.Migrations;

[Migration(10, "Add session reauthentication marker for Relisten user API")]
public sealed class AddSessionReauthenticationMarker : Migration
{
    protected override void Up()
    {
        Execute(
            """
            ALTER TABLE user_data.user_sessions
                ADD COLUMN IF NOT EXISTS reauthenticated_at TIMESTAMPTZ;
            """);
    }

    protected override void Down()
    {
        throw new NotSupportedException("Session reauthentication marker migration is not reversible in automated migrations.");
    }
}
