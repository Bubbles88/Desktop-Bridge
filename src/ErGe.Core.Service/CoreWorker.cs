using ErGe.Core.Policy;
using ErGe.Core.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ErGe.Core.Service;

public sealed class CoreWorker : BackgroundService
{
    private const int StatusSchemaVersion = 1;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);

    private readonly PolicyEngine _policyEngine;
    private readonly CoreStatusStore _statusStore;
    private readonly ILogger<CoreWorker> _logger;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;

    public CoreWorker(
        PolicyEngine policyEngine,
        CoreStatusStore statusStore,
        ILogger<CoreWorker> logger)
    {
        _policyEngine = policyEngine;
        _statusStore = statusStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "ErGe Core started. Instance {InstanceId}. Policy path {PolicyPath}. Status path {StatusPath}.",
            _instanceId,
            CorePaths.PolicyPath,
            _statusStore.Path);

        while (!stoppingToken.IsCancellationRequested)
        {
            WriteHeartbeat();

            try
            {
                await Task.Delay(HeartbeatInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("ErGe Core stopping. Instance {InstanceId}.", _instanceId);
    }

    public CoreStatusSnapshot CreateSnapshot(DateTimeOffset heartbeatAtUtc)
    {
        var policy = _policyEngine.Snapshot;

        var runtimeState = policy.EffectiveAccess switch
        {
            EffectiveAccess.EmergencyBlocked => RuntimeState.EmergencyBlock,
            EffectiveAccess.AlwaysOff => RuntimeState.AlwaysOff,
            _ => RuntimeState.LocalReady
        };

        return new CoreStatusSnapshot(
            StatusSchemaVersion,
            _instanceId,
            Environment.ProcessId,
            Environment.MachineName,
            _startedAtUtc,
            heartbeatAtUtc,
            runtimeState,
            policy.PersistentMode,
            policy.SessionOverride,
            policy.EffectiveAccess,
            policy.RemoteAiAllowed,
            policy.LocalAiAllowed,
            policy.ConfigurationHealthy);
    }

    public void WriteHeartbeat()
    {
        var snapshot = CreateSnapshot(DateTimeOffset.UtcNow);
        _statusStore.Save(snapshot);
    }
}
