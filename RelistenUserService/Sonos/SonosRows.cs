using System.Text.Json;
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
    public bool ReturnToMobile { get; set; }
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
    public Guid? LatestCommandId { get; set; }
    public string HouseholdId { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string CredentialHash { get; set; } = "";
    public string ItemsJson { get; set; } = "";
    public string RemovedItemsJson { get; set; } = "[]";
    public long QueueVersion { get; set; } = 1;

    // Sonos acknowledged the refresh request. Playback status separately reports its cached version.
    public long RefreshedQueueVersion { get; set; } = 1;
    public DateTimeOffset? RefreshAfter { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    // After this reservation ends, receipt polling can still recover a load that reached the room.
    public DateTimeOffset? DispatchUntil { get; set; }
    public string Status { get; set; } = "dispatching";
    public long? PlaybackEventSequence { get; set; }
    public string? PlaybackEventState { get; set; }

    public QueueTrack[] ReadItems() =>
        JsonSerializer.Deserialize<QueueTrack[]>(ItemsJson, SonosHandoff.QueueJson)!;

    public RemovedQueueTrack[] ReadRemovedItems() =>
        JsonSerializer.Deserialize<RemovedQueueTrack[]>(RemovedItemsJson, SonosHandoff.QueueJson)!;
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

internal sealed class SonosCommand
{
    public Guid QueueId { get; set; }
    public Guid Id { get; set; }
    public string RequestHash { get; set; } = "";
    public string Action { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string? ErrorCode { get; set; }
    public string ExpectedItemId { get; set; } = "";
    public string? TargetItemId { get; set; }
    public int? TargetValue { get; set; }
    public bool? TargetMuted { get; set; }
    public bool? PlayOnCompletion { get; set; }
    public long? QueueVersion { get; set; }
}

internal sealed class SonosCommandConfiguration : IEntityTypeConfiguration<SonosCommand>
{
    public void Configure(EntityTypeBuilder<SonosCommand> builder)
    {
        builder.ToTable("sonos_commands", "user_data");
        builder.HasKey(row => new { row.QueueId, row.Id });
        builder
            .HasOne<SonosQueue>()
            .WithMany()
            .HasForeignKey(row => row.QueueId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
