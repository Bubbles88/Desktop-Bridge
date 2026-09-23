namespace ErGe.Core.Policy;

public sealed record PolicySnapshot(
    PersistentMode PersistentMode,
    SessionOverride SessionOverride,
    EffectiveAccess EffectiveAccess,
    bool RemoteAiAllowed,
    bool LocalAiAllowed,
    bool ConfigurationHealthy);
