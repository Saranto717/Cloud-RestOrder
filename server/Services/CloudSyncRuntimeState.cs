namespace RestaurantOrders.Services;

public sealed class CloudSyncRuntimeState
{
    private int _enabled;
    public bool Enabled => Volatile.Read(ref _enabled) == 1;
    public void SetEnabled(bool enabled) => Volatile.Write(ref _enabled, enabled ? 1 : 0);
}
