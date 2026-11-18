namespace RestaurantOrders.Models;

/// <summary>Durable local journal used to retry cloud sync after a network outage.</summary>
public sealed class CloudSyncOutboxEvent
{
    public long Id { get; set; }
    public Guid EventId { get; set; } = Guid.NewGuid();
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string EventType { get; set; } = "";
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string PayloadJson { get; set; } = "{}";
    public int AttemptCount { get; set; }
    public DateTimeOffset? LastAttemptAtUtc { get; set; }
    public DateTimeOffset? AcknowledgedAtUtc { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Records the one-time initial snapshot queued for cloud replication.</summary>
public sealed class CloudSyncSnapshotState
{
    public int Id { get; set; } = 1;
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public int SnapshotVersion { get; set; }
}
