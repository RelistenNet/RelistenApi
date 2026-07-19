using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Library.Entities;

namespace RelistenUserService.Persistence.Configurations;

public sealed class FavoriteMutationReceiptConfiguration
    : IEntityTypeConfiguration<FavoriteMutationReceipt>
{
    public void Configure(EntityTypeBuilder<FavoriteMutationReceipt> builder)
    {
        builder.ToTable("favorite_mutation_receipts", "user_data", table =>
        {
            table.HasCheckConstraint(
                "ck_favorite_mutation_receipts_id_uuid_v7",
                UuidV7Constraint.Sql);
            table.HasCheckConstraint(
                "ck_favorite_mutation_receipts_catalog_type",
                CatalogTypeConstraint.Sql);
            table.HasCheckConstraint(
                "ck_favorite_mutation_receipts_desired_state",
                "desired_state IN ('favorite', 'not_favorite')");
            table.HasCheckConstraint(
                "ck_favorite_mutation_receipts_submitted_favorite_id",
                "submitted_favorite_id IS NULL OR uuid_extract_version(submitted_favorite_id) = 7");
            table.HasCheckConstraint(
                "ck_favorite_mutation_receipts_canonical_favorite_id",
                "canonical_favorite_id IS NULL OR uuid_extract_version(canonical_favorite_id) = 7");
            table.HasCheckConstraint(
                "ck_favorite_mutation_receipts_payload",
                "octet_length(payload_hash) = 32 AND " +
                "((desired_state = 'favorite' AND submitted_favorite_id IS NOT NULL) OR " +
                "(desired_state = 'not_favorite' AND submitted_favorite_id IS NULL)) AND " +
                "(desired_state <> 'favorite' OR canonical_favorite_id IS NOT NULL) AND " +
                "library_revision >= 0");
        });
        builder.HasKey(receipt => receipt.Id);
        builder.Property(receipt => receipt.CatalogType).HasMaxLength(16);
        builder.Property(receipt => receipt.DesiredState).HasMaxLength(16);
        builder.Property(receipt => receipt.PayloadHash).HasColumnType("bytea");
        builder.HasIndex(receipt => new { receipt.UserId, receipt.CreatedAt });
        builder.HasIndex(receipt => receipt.SubmittedFavoriteId);
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(receipt => receipt.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
