using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelistenUserService.Persistence.Migrations;

[DbContext(typeof(AccountsDbContext))]
[Migration("20260719193000_ConfigureProductionIosClient")]
public sealed class ConfigureProductionIosClient : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Client registrations are security-sensitive deployment data. Installing this row
        // in the migration keeps runtime pods from needing permission to rewrite applications
        // and avoids a create-or-update race when multiple replicas start together.
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
                '019f7ce6-f405-79df-bcc3-21f472018d76',
                'relisten-mobile-ios',
                'public',
                '0c516614-1574-4938-9ba1-6c925d139e28',
                'implicit',
                'Relisten iOS',
                '["ept:authorization","ept:token","gt:authorization_code","gt:refresh_token","rst:code","scp:profile","scp:user.read","scp:library.read","scp:library.write","scp:account.manage"]',
                '["net.relisten.mobile:/oauth2redirect/ios"]',
                '["ft:pkce"]')
            ON CONFLICT (client_id) DO UPDATE SET
                client_type = EXCLUDED.client_type,
                concurrency_token = EXCLUDED.concurrency_token,
                consent_type = EXCLUDED.consent_type,
                display_name = EXCLUDED.display_name,
                permissions = EXCLUDED.permissions,
                redirect_uris = EXCLUDED.redirect_uris,
                requirements = EXCLUDED.requirements;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DELETE FROM identity.openiddict_applications
            WHERE client_id = 'relisten-mobile-ios';
            """);
    }
}
