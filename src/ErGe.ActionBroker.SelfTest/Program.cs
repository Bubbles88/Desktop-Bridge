using System.Text.Json;
using ErGe.Core.Actions;
using ErGe.Core.Policy;

var root = Path.Combine(
    Path.GetTempPath(),
    "ErGe-Action-Broker-SelfTest-" + Guid.NewGuid().ToString("N"));

Directory.CreateDirectory(root);
var policyPath = Path.Combine(root, "policy.json");

try
{
    var policy = new PolicyEngine(new FilePolicyStore(policyPath));
    var executor = new FakeInteractiveExecutor();
    var handler = new ScreenInfoCapabilityHandler(executor);
    var broker = new CapabilityBroker(policy, new[] { handler });

    Assert(
        broker.Capabilities.Count == 1
        && broker.Capabilities.Single().Name == "screen.info",
        "Broker exposes only explicitly registered capabilities.");

    var remoteWhileOff = await broker.ExecuteAsync(Request(ActionOrigin.RemoteProvider));
    Assert(
        !remoteWhileOff.Success && remoteWhileOff.ErrorCode == "policy_denied",
        "Remote provider fails closed while Always Off.");

    var localWhileOff = await broker.ExecuteAsync(Request(ActionOrigin.LocalProvider));
    Assert(
        !localWhileOff.Success && localWhileOff.ErrorCode == "policy_denied",
        "Local provider also fails closed while Always Off.");

    policy.SetPersistentMode(PersistentMode.AlwaysOn, ControlSource.LocalOwner);

    var remoteAllowed = await broker.ExecuteAsync(Request(ActionOrigin.RemoteProvider));
    Assert(remoteAllowed.Success, "Always On permits the registered remote read-only capability.");
    Assert(executor.CallCount == 1, "Authorized request executes exactly once.");

    policy.SetSessionOverride(SessionOverride.LocalOnly, ControlSource.LocalOwner);

    var remoteLocalOnly = await broker.ExecuteAsync(Request(ActionOrigin.RemoteProvider));
    Assert(
        !remoteLocalOnly.Success && remoteLocalOnly.ErrorCode == "policy_denied",
        "Local Only blocks remote provider.");

    var localLocalOnly = await broker.ExecuteAsync(Request(ActionOrigin.LocalProvider));
    Assert(localLocalOnly.Success, "Local Only preserves local provider execution.");

    policy.SetSessionOverride(SessionOverride.EmergencyBlock, ControlSource.LocalOwner);

    var emergencyLocal = await broker.ExecuteAsync(Request(ActionOrigin.LocalProvider));
    var emergencyRemote = await broker.ExecuteAsync(Request(ActionOrigin.RemoteProvider));
    Assert(
        !emergencyLocal.Success && !emergencyRemote.Success,
        "Emergency Block denies both local and remote providers.");

    policy.ClearSessionOverride(ControlSource.LocalOwner);

    var missing = await broker.ExecuteAsync(new ActionRequest(
        ActionProtocol.Version,
        "missing-capability",
        ActionOrigin.RemoteProvider,
        "mouse.click",
        EmptyObject()));

    Assert(
        !missing.Success && missing.ErrorCode == "capability_not_found",
        "Unregistered capability is rejected without reaching the executor.");

    var badVersion = await broker.ExecuteAsync(new ActionRequest(
        ActionProtocol.Version + 1,
        "bad-version",
        ActionOrigin.RemoteProvider,
        "screen.info",
        EmptyObject()));

    Assert(
        !badVersion.Success && badVersion.ErrorCode == "protocol_version_unsupported",
        "Protocol version mismatch fails before capability dispatch.");

    var invalidArguments = await broker.ExecuteAsync(new ActionRequest(
        ActionProtocol.Version,
        "bad-args",
        ActionOrigin.RemoteProvider,
        "screen.info",
        JsonSerializer.SerializeToElement(new { unexpected = true })));

    Assert(
        !invalidArguments.Success && invalidArguments.ErrorCode == "arguments_invalid",
        "Capability validates its own argument contract.");

    var corruptPolicyPath = Path.Combine(root, "invalid-policy.json");
    File.WriteAllText(corruptPolicyPath, "{ invalid json");
    var corruptPolicy = new PolicyEngine(new FilePolicyStore(corruptPolicyPath));
    var corruptBroker = new CapabilityBroker(
        corruptPolicy,
        new[] { new ScreenInfoCapabilityHandler(new FakeInteractiveExecutor()) });

    var corrupt = await corruptBroker.ExecuteAsync(Request(ActionOrigin.RemoteProvider));
    Assert(
        !corrupt.Success && corrupt.ErrorCode == "policy_unhealthy",
        "Unhealthy policy configuration fails closed before dispatch.");

    Console.WriteLine("ERGE_ACTION_BROKER_SELFTEST_OK");
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

static ActionRequest Request(ActionOrigin origin) =>
    new(
        ActionProtocol.Version,
        Guid.NewGuid().ToString("N"),
        origin,
        "screen.info",
        EmptyObject());

static JsonElement EmptyObject() =>
    JsonSerializer.SerializeToElement(new { });

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException("FAILED: " + message);
    }

    Console.WriteLine("PASS: " + message);
}

sealed class FakeInteractiveExecutor : IInteractiveCapabilityExecutor
{
    public int CallCount { get; private set; }

    public Task<JsonElement> ExecuteAsync(
        string action,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(action, "screen.info", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unexpected action reached fake executor.");
        }

        CallCount += 1;

        var data = JsonSerializer.SerializeToElement(new
        {
            monitors = new[]
            {
                new
                {
                    deviceName = @"\\.\DISPLAY1",
                    x = 0,
                    y = 0,
                    width = 2880,
                    height = 1800,
                    primary = true
                }
            }
        });

        return Task.FromResult(data);
    }
}
