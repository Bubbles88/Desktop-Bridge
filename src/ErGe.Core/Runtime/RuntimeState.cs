namespace ErGe.Core.Runtime;

public enum RuntimeState
{
    Starting,
    AlwaysOff,
    LocalReady,
    DeviceOnlineNoSession,
    RemoteConnecting,
    RemoteReady,
    InteractiveReady,
    Degraded,
    Recovering,
    EmergencyBlock,
    Updating,
    Faulted
}
