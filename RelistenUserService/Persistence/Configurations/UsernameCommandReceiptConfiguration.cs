using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RelistenUserService.Identity.Entities;

namespace RelistenUserService.Persistence.Configurations;

public sealed class UsernameCommandReceiptConfiguration
    : IEntityTypeConfiguration<UsernameCommandReceipt>
{
    public void Configure(EntityTypeBuilder<UsernameCommandReceipt> builder)
    {
        builder.ToTable(
            "username_command_receipts",
            "identity",
            table =>
                table.HasCheckConstraint(
                    "ck_username_command_receipts_id_typed_id",
                    AccountIdConstraint.Sql("command")
                )
        );
        builder.HasKey(receipt => receipt.Id);
        builder.Property(receipt => receipt.PayloadHash).HasColumnType("bytea");
        builder.Property(receipt => receipt.StoredResult).HasColumnType("jsonb");
        builder.HasIndex(receipt => new { receipt.UserId, receipt.CreatedAt });
        builder
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(receipt => receipt.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
