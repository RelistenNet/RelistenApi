using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelistenUserService.Persistence.Migrations;

[DbContext(typeof(AccountsDbContext))]
[Migration("20261105035500_ConfigureProductionIosHttpsCallback")]
public sealed class ConfigureProductionIosHttpsCallback : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Update already-installed clients without changing their identity or permissions.
        // The private scheme belongs to the separate development client, not release iOS.
        migrationBuilder.Sql(
            """
            UPDATE identity.openiddict_applications
            SET redirect_uris = '["https://relisten.net/auth/mobile/ios/callback"]',
                concurrency_token = '05f3173e-272d-4981-9429-6f5c44d3396e'
            WHERE client_id = 'relisten-mobile-ios';
            """
        );
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE identity.openiddict_applications
            SET redirect_uris = '["net.relisten.mobile:/oauth2redirect/ios"]',
                concurrency_token = 'fa656f09-36f7-482d-814b-caaee9a451c0'
            WHERE client_id = 'relisten-mobile-ios';
            """
        );
    }
}
