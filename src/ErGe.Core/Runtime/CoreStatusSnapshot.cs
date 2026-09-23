using ErGe.Core.Policy;

namespace ErGe.Core.Runtime;

public sealed record CoreStatusSnapshot(
    int SchemaVersion,
    string InstanceId,
    int ProcessId,
    string MachineName,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset HeartbeatAtUtc,
    RuntimeState RuntimeState,
    PersistentMode PersistentMode,
    SessionOverride SessionOverride,
    EffectiveAccess EffectiveAccess,
    bool RemoteAiAllowed,
    bool LocalAiAllowed,
    bool ConfigurationHealthy);
