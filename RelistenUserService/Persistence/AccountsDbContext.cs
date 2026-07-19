using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Library.Entities;
using System.Text.Json;

namespace RelistenUserService.Persistence;

public sealed class AccountsDbContext(DbContextOptions<AccountsDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ExternalIdentity> ExternalIdentities => Set<ExternalIdentity>();
    public DbSet<NativeSession> NativeSessions => Set<NativeSession>();
    public DbSet<UsernameHold> UsernameHolds => Set<UsernameHold>();
    public DbSet<UsernameCommandReceipt> UsernameCommandReceipts =>
        Set<UsernameCommandReceipt>();
    public DbSet<Favorite> Favorites => Set<Favorite>();
    public DbSet<FavoriteMutationReceipt> FavoriteMutationReceipts =>
        Set<FavoriteMutationReceipt>();
    public DbSet<LibraryState> LibraryStates => Set<LibraryState>();
    public DbSet<LibraryChange> LibraryChanges => Set<LibraryChange>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AccountsDbContext).Assembly);
        ConfigureOpenIddict(modelBuilder);
        modelBuilder.Entity<DataProtectionKey>().ToTable("data_protection_keys", "identity");
        ApplySnakeCaseColumnNames(modelBuilder);
    }

    private static void ConfigureOpenIddict(ModelBuilder modelBuilder)
    {
        ConfigureOpenIddictEntity<OpenIddictEntityFrameworkCoreApplication<Guid>>(
            modelBuilder,
            "openiddict_applications",
            "ck_openiddict_applications_id_uuid_v7");
        ConfigureOpenIddictEntity<OpenIddictEntityFrameworkCoreAuthorization<Guid>>(
            modelBuilder,
            "openiddict_authorizations",
            "ck_openiddict_authorizations_id_uuid_v7");
        ConfigureOpenIddictEntity<OpenIddictEntityFrameworkCoreScope<Guid>>(
            modelBuilder,
            "openiddict_scopes",
            "ck_openiddict_scopes_id_uuid_v7");
        ConfigureOpenIddictEntity<OpenIddictEntityFrameworkCoreToken<Guid>>(
            modelBuilder,
            "openiddict_tokens",
            "ck_openiddict_tokens_id_uuid_v7");
    }

    private static void ConfigureOpenIddictEntity<TEntity>(
        ModelBuilder modelBuilder,
        string tableName,
        string constraintName)
        where TEntity : class
    {
        var builder = modelBuilder.Entity<TEntity>();
        builder.ToTable(tableName, "identity", table =>
            table.HasCheckConstraint(constraintName, UuidV7Constraint.Sql));
        builder.Property<Guid>("Id")
            .HasValueGenerator<UuidV7ValueGenerator>()
            .ValueGeneratedOnAdd();
    }

    private static void ApplySnakeCaseColumnNames(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name));
            }
        }
    }
}
