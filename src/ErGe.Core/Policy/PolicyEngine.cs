namespace ErGe.Core.Policy;

public sealed class PolicyEngine
{
    private readonly FilePolicyStore _store;
    private readonly object _sync = new();

    private PersistentMode _persistentMode;
    private SessionOverride _sessionOverride;
    private bool _configurationHealthy;

    public PolicyEngine(FilePolicyStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));

        var loaded = _store.Load();
        _persistentMode = loaded.Mode;
        _configurationHealthy = loaded.Healthy;
        _sessionOverride = SessionOverride.None;
    }

    public PolicySnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return BuildSnapshot();
            }
        }
    }

    public PolicySnapshot SetPersistentMode(
        PersistentMode mode,
        ControlSource source)
    {
        RequireLocalOwner(source);

        lock (_sync)
        {
            _store.Save(mode);
            _persistentMode = mode;
            _configurationHealthy = true;

            return BuildSnapshot();
        }
    }

    public PolicySnapshot SetSessionOverride(
        SessionOverride sessionOverride,
        ControlSource source)
    {
        RequireLocalOwner(source);

        lock (_sync)
        {
            _sessionOverride = sessionOverride;
            return BuildSnapshot();
        }
    }

    public PolicySnapshot ClearSessionOverride(ControlSource source)
    {
        return SetSessionOverride(SessionOverride.None, source);
    }

    private static void RequireLocalOwner(ControlSource source)
    {
        if (source != ControlSource.LocalOwner)
        {
            throw new UnauthorizedAccessException(
                "Persistent policy and session overrides can only be changed by the local owner.");
        }
    }

    private PolicySnapshot BuildSnapshot()
    {
        var effective = _sessionOverride switch
        {
            SessionOverride.EmergencyBlock => EffectiveAccess.EmergencyBlocked,
            SessionOverride.LocalOnly => EffectiveAccess.LocalOnly,
            SessionOverride.ConnectNow => EffectiveAccess.RemoteAllowed,
            _ when _persistentMode == PersistentMode.AlwaysOn => EffectiveAccess.RemoteAllowed,
            _ => EffectiveAccess.AlwaysOff
        };

        return effective switch
        {
            EffectiveAccess.RemoteAllowed => new PolicySnapshot(
                _persistentMode,
                _sessionOverride,
                effective,
                RemoteAiAllowed: true,
                LocalAiAllowed: true,
                _configurationHealthy),

            EffectiveAccess.LocalOnly => new PolicySnapshot(
                _persistentMode,
                _sessionOverride,
                effective,
                RemoteAiAllowed: false,
                LocalAiAllowed: true,
                _configurationHealthy),

            EffectiveAccess.EmergencyBlocked => new PolicySnapshot(
                _persistentMode,
                _sessionOverride,
                effective,
                RemoteAiAllowed: false,
                LocalAiAllowed: false,
                _configurationHealthy),

            _ => new PolicySnapshot(
                _persistentMode,
                _sessionOverride,
                effective,
                RemoteAiAllowed: false,
                LocalAiAllowed: false,
                _configurationHealthy)
        };
    }
}
