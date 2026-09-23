using System.Text.Json;
using System.Text.Json.Serialization;
using ErGe.Core.Policy;
using ErGe.Core.Runtime;

var root = System.IO.Path.Combine(
    System.IO.Path.GetTempPath(),
    "ErGe-Core-Service-SelfTest-" + Guid.NewGuid().ToString("N"));

Directory.CreateDirectory(root);

var policyPath = System.IO.Path.Combine(root, "policy.json");
var statusPath = System.IO.Path.Combine(root, "runtime-status.json");

try
{
    var policyStore = new FilePolicyStore(policyPath);
    var policy = new PolicyEngine(policyStore);
    var statusStore = new CoreStatusStore(statusPath);

    var first = BuildSnapshot(policy, "selftest-a", DateTimeOffset.UtcNow);
    statusStore.Save(first);

    Assert(File.Exists(statusPath), "Runtime status file is created.");

    var parsed = ReadStatus(statusPath);
    Assert(parsed.RuntimeState == RuntimeState.AlwaysOff, "Default runtime is Always Off.");
    Assert(!parsed.RemoteAiAllowed, "Default runtime denies remote AI.");

    policy.SetPersistentMode(PersistentMode.AlwaysOn, ControlSource.LocalOwner);
    var second = BuildSnapshot(policy, "selftest-a", DateTimeOffset.UtcNow.AddSeconds(2));
    statusStore.Save(second);

    var updated = ReadStatus(statusPath);
    Assert(updated.RuntimeState == RuntimeState.LocalReady, "Always On reaches Local Ready before remote transport exists.");
    Assert(updated.RemoteAiAllowed, "Always On policy permits remote AI at the policy layer.");
    Assert(updated.HeartbeatAtUtc > parsed.HeartbeatAtUtc, "Heartbeat advances.");

    var reloaded = new PolicyEngine(new FilePolicyStore(policyPath));
    Assert(reloaded.Snapshot.PersistentMode == PersistentMode.AlwaysOn, "Persistent policy survives Core restart.");
    Assert(reloaded.Snapshot.SessionOverride == SessionOverride.None, "Temporary overrides do not survive Core restart.");

    Console.WriteLine("ERGE_CORE_SERVICE_SELFTEST_OK");
}
finally
{
    try
    {
        Directory.Delete(root, recursive: true);
    }
    catch
    {
        // Best-effort cleanup after verification.
    }
}

static CoreStatusSnapshot BuildSnapshot(
    PolicyEngine policyEngine,
    string instanceId,
    DateTimeOffset heartbeatAtUtc)
{
    var policy = policyEngine.Snapshot;
    var state = policy.EffectiveAccess switch
    {
        EffectiveAccess.EmergencyBlocked => RuntimeState.EmergencyBlock,
        EffectiveAccess.AlwaysOff => RuntimeState.AlwaysOff,
        _ => RuntimeState.LocalReady
    };

    return new CoreStatusSnapshot(
        1,
        instanceId,
        Environment.ProcessId,
        Environment.MachineName,
        heartbeatAtUtc.AddSeconds(-2),
        heartbeatAtUtc,
        state,
        policy.PersistentMode,
        policy.SessionOverride,
        policy.EffectiveAccess,
        policy.RemoteAiAllowed,
        policy.LocalAiAllowed,
        policy.ConfigurationHealthy);
}

static CoreStatusSnapshot ReadStatus(string path)
{
    var options = new JsonSerializerOptions
    {
        Converters = { new JsonStringEnumConverter() }
    };

    return JsonSerializer.Deserialize<CoreStatusSnapshot>(
        File.ReadAllText(path),
        options) ?? throw new InvalidOperationException("Runtime status could not be parsed.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException("FAILED: " + message);
    }

    Console.WriteLine("PASS: " + message);
}
