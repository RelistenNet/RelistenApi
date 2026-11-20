using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RelistenUserService.Identity.Entities;

namespace RelistenUserService.Sonos;

internal sealed class SonosGrant
{
    public UserId UserId { get; set; }
    public string ProtectedToken { get; set; } = "";
}

internal sealed class SonosOAuthState
{
    public string Hash { get; set; } = "";
    public UserId UserId { get; set; }
    public SessionId SessionId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

internal sealed class SonosQueue
{
    public Guid Id { get; set; }
    public UserId UserId { get; set; }
    public SessionId SessionId { get; set; }
    public string GroupId { get; set; } = "";
    public string? GroupName { get; set; }
    public string? SonosSessionId { get; set; }
    public string HouseholdId { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string CredentialHash { get; set; } = "";
    public string ItemsJson { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public string Status { get; set; } = "dispatching";
    public long? PlaybackEventSequence { get; set; }
    public string? PlaybackEventState { get; set; }
}

internal sealed class SonosGrantConfiguration : IEntityTypeConfiguration<SonosGrant>
{
    public void Configure(EntityTypeBuilder<SonosGrant> builder)
    {
        builder.ToTable("sonos_grants", "user_data");
        builder.HasKey(row => row.UserId);
        builder
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(row => row.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SonosOAuthStateConfiguration : IEntityTypeConfiguration<SonosOAuthState>
{
    public void Configure(EntityTypeBuilder<SonosOAuthState> builder)
    {
        builder.ToTable("sonos_oauth_states", "user_data");
        builder.HasKey(row => row.Hash);
        builder.HasIndex(row => row.ExpiresAt);
        builder
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(row => row.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SonosQueueConfiguration : IEntityTypeConfiguration<SonosQueue>
{
    public void Configure(EntityTypeBuilder<SonosQueue> builder)
    {
        builder.ToTable("sonos_queues", "user_data");
        builder.HasKey(row => row.Id);
        builder.HasIndex(row => row.GroupId);
        builder
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(row => row.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
