using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Library.Entities;

namespace RelistenUserService.Persistence.Configurations;

public sealed class LibraryStateConfiguration : IEntityTypeConfiguration<LibraryState>
{
    public void Configure(EntityTypeBuilder<LibraryState> builder)
    {
        builder.ToTable("library_states", "user_data", table =>
        {
            table.HasCheckConstraint("ck_library_states_id_uuid_v7", UuidV7Constraint.Sql);
            table.HasCheckConstraint("ck_library_states_revision", "revision >= 0");
        });
        builder.HasKey(state => state.Id);
        builder.Property(state => state.Id)
            .HasValueGenerator<UuidV7ValueGenerator>()
            .ValueGeneratedOnAdd();
        builder.HasIndex(state => state.UserId).IsUnique();
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(state => state.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
