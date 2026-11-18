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

/// <summary>Permanent cloud key high-water mark, retained after domain rows are deleted.</summary>
public sealed class CloudHostedIdSequence
{
    public string EntityType { get; set; } = "";
    public int CurrentId { get; set; }
}

/// <summary>Latest received state, durably retained until its references can be applied.</summary>
public sealed class CloudSyncInboxEntry
{
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public long Cursor { get; set; }
    public bool IsDeleted { get; set; }
    public int SchemaVersion { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string PayloadJson { get; set; } = "{}";
}
