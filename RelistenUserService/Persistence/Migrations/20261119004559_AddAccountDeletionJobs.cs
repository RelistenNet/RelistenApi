using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelistenUserService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountDeletionJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_deletion_jobs",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    completed_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_deletion_jobs", x => x.id);
                    table.CheckConstraint(
                        "ck_account_deletion_jobs_id_uuid_v7",
                        "uuid_extract_version(id) IS NOT DISTINCT FROM 7"
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_account_deletion_jobs_user_id",
                schema: "identity",
                table: "account_deletion_jobs",
                column: "user_id",
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "account_deletion_jobs", schema: "identity");
        }
    }
}
