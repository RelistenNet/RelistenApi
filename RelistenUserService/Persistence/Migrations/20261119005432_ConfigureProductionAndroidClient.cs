using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RelistenUserService.Persistence.Migrations;

[DbContext(typeof(AccountsDbContext))]
[Migration("20261119005432_ConfigureProductionAndroidClient")]
public sealed class ConfigureProductionAndroidClient : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Give the production Android app its own public client with a claimed HTTPS
        // callback. The private scheme remains registered only for the development client.
        migrationBuilder.Sql(
            """
            INSERT INTO identity.openiddict_applications (
                id,
                client_id,
                client_type,
                concurrency_token,
                consent_type,
                display_name,
                permissions,
                redirect_uris,
                requirements)
            VALUES (
                '01a1f14b-2dea-756b-a988-d5ee38e0a43b',
                'relisten-mobile-android',
                'public',
                '35768caa-7dbf-4885-bef1-8274a6797a97',
                'implicit',
                'Relisten Android',
                '["ept:authorization","ept:token","gt:authorization_code","gt:refresh_token","rst:code","scp:profile","scp:user.read","scp:library.read","scp:library.write","scp:account.manage"]',
                '["https://relisten.net/auth/mobile/android/callback"]',
                '["ft:pkce"]');
            """
        );
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DELETE FROM identity.openiddict_applications
            WHERE client_id = 'relisten-mobile-android';
            """
        );
    }
}
