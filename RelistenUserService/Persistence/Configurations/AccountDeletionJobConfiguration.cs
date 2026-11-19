using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RelistenUserService.Identity.Entities;

namespace RelistenUserService.Persistence.Configurations;

public sealed class AccountDeletionJobConfiguration : IEntityTypeConfiguration<AccountDeletionJob>
{
    public void Configure(EntityTypeBuilder<AccountDeletionJob> builder)
    {
        builder.ToTable(
            "account_deletion_jobs",
            "identity",
            table =>
                table.HasCheckConstraint(
                    "ck_account_deletion_jobs_id_uuid_v7",
                    UuidV7Constraint.Sql
                )
        );
        builder.HasKey(job => job.Id);
        builder.HasIndex(job => job.UserId).IsUnique();
    }
}
