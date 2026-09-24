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
    var handlers = new ICapabilityHandler[]
    {
        new ScreenInfoCapabilityHandler(executor),
        new WindowListCapabilityHandler(executor)
    };
    var broker = new CapabilityBroker(policy, handlers);

    Assert(
        broker.Capabilities.Select(capability => capability.Name).SequenceEqual(
            new[] { "screen.info", "windows.list" }),
        "Broker exposes only the two explicitly registered read-only capabilities.");

    var remoteWhileOff = await broker.ExecuteAsync(
        Request(ActionOrigin.RemoteProvider, "windows.list", EmptyObject()));
    Assert(
        !remoteWhileOff.Success && remoteWhileOff.ErrorCode == "policy_denied",
        "Remote windows.list fails closed while Always Off.");

    var localWhileOff = await broker.ExecuteAsync(
        Request(ActionOrigin.LocalProvider, "windows.list", EmptyObject()));
    Assert(
        !localWhileOff.Success && localWhileOff.ErrorCode == "policy_denied",
        "Local windows.list also fails closed while Always Off.");

    policy.SetPersistentMode(PersistentMode.AlwaysOn, ControlSource.LocalOwner);

    var remoteScreen = await broker.ExecuteAsync(
        Request(ActionOrigin.RemoteProvider, "screen.info", EmptyObject()));
    Assert(remoteScreen.Success, "Always On preserves screen.info.");
    Assert(executor.ScreenCallCount == 1, "screen.info executes exactly once.");

    var remoteWindows = await broker.ExecuteAsync(
        Request(
            ActionOrigin.RemoteProvider,
            "windows.list",
            JsonSerializer.SerializeToElement(new
            {
                titleFilter = "ChatGPT",
                visibleOnly = false
            })));
    Assert(remoteWindows.Success, "Always On permits remote windows.list.");
    Assert(executor.WindowCallCount == 1, "windows.list executes exactly once.");
    Assert(
        executor.LastWindowArguments?.GetProperty("titleFilter").GetString() == "ChatGPT"
        && executor.LastWindowArguments?.GetProperty("visibleOnly").GetBoolean() == false,
        "windows.list forwards normalized arguments to the interactive executor.");

    policy.SetSessionOverride(SessionOverride.LocalOnly, ControlSource.LocalOwner);

    var remoteLocalOnly = await broker.ExecuteAsync(
        Request(ActionOrigin.RemoteProvider, "windows.list", EmptyObject()));
    Assert(
        !remoteLocalOnly.Success && remoteLocalOnly.ErrorCode == "policy_denied",
        "Local Only blocks remote windows.list.");

    var localLocalOnly = await broker.ExecuteAsync(
        Request(ActionOrigin.LocalProvider, "windows.list", EmptyObject()));
    Assert(localLocalOnly.Success, "Local Only preserves local windows.list.");

    policy.SetSessionOverride(SessionOverride.EmergencyBlock, ControlSource.LocalOwner);

    var emergencyLocal = await broker.ExecuteAsync(
        Request(ActionOrigin.LocalProvider, "windows.list", EmptyObject()));
    var emergencyRemote = await broker.ExecuteAsync(
        Request(ActionOrigin.RemoteProvider, "windows.list", EmptyObject()));
    Assert(
        !emergencyLocal.Success && !emergencyRemote.Success,
        "Emergency Block denies windows.list for both provider origins.");

    policy.ClearSessionOverride(ControlSource.LocalOwner);

    var unknownProperty = await broker.ExecuteAsync(
        Request(
            ActionOrigin.RemoteProvider,
            "windows.list",
            JsonSerializer.SerializeToElement(new { surprise = true })));
    Assert(
        !unknownProperty.Success && unknownProperty.ErrorCode == "arguments_invalid",
        "windows.list rejects unknown properties before Session Agent execution.");

    var invalidVisibleOnly = await broker.ExecuteAsync(
        Request(
            ActionOrigin.RemoteProvider,
            "windows.list",
            JsonSerializer.SerializeToElement(new { visibleOnly = "yes" })));
    Assert(
        !invalidVisibleOnly.Success && invalidVisibleOnly.ErrorCode == "arguments_invalid",
        "windows.list rejects non-boolean visibleOnly.");

    var oversizedTitleFilter = await broker.ExecuteAsync(
        Request(
            ActionOrigin.RemoteProvider,
            "windows.list",
            JsonSerializer.SerializeToElement(new { titleFilter = new string('x', 513) })));
    Assert(
        !oversizedTitleFilter.Success && oversizedTitleFilter.ErrorCode == "arguments_invalid",
        "windows.list bounds titleFilter length.");

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

    var invalidScreenArguments = await broker.ExecuteAsync(
        Request(
            ActionOrigin.RemoteProvider,
            "screen.info",
            JsonSerializer.SerializeToElement(new { unexpected = true })));

    Assert(
        !invalidScreenArguments.Success
        && invalidScreenArguments.ErrorCode == "arguments_invalid",
        "Existing screen.info argument validation remains intact.");

    var corruptPolicyPath = Path.Combine(root, "invalid-policy.json");
    File.WriteAllText(corruptPolicyPath, "{ invalid json");
    var corruptPolicy = new PolicyEngine(new FilePolicyStore(corruptPolicyPath));
    var corruptBroker = new CapabilityBroker(
        corruptPolicy,
        new ICapabilityHandler[]
        {
            new ScreenInfoCapabilityHandler(new FakeInteractiveExecutor()),
            new WindowListCapabilityHandler(new FakeInteractiveExecutor())
        });

    var corrupt = await corruptBroker.ExecuteAsync(
        Request(ActionOrigin.RemoteProvider, "windows.list", EmptyObject()));
    Assert(
        !corrupt.Success && corrupt.ErrorCode == "policy_unhealthy",
        "Unhealthy policy configuration fails closed before windows.list dispatch.");

    Console.WriteLine("ERGE_ACTION_BROKER_SELFTEST_OK");
    Console.WriteLine("ERGE_PHASE10_WINDOW_BROKER_SELFTEST_OK");
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

static ActionRequest Request(
    ActionOrigin origin,
    string action,
    JsonElement arguments) =>
    new(
        ActionProtocol.Version,
        Guid.NewGuid().ToString("N"),
        origin,
        action,
        arguments);

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
    public int ScreenCallCount { get; private set; }

    public int WindowCallCount { get; private set; }

    public JsonElement? LastWindowArguments { get; private set; }

    public Task<JsonElement> ExecuteAsync(
        string action,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.Equals(action, "screen.info", StringComparison.Ordinal))
        {
            ScreenCallCount += 1;

            return Task.FromResult(JsonSerializer.SerializeToElement(new
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
            }));
        }

        if (string.Equals(action, "windows.list", StringComparison.Ordinal))
        {
            WindowCallCount += 1;
            LastWindowArguments = arguments.Clone();

            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                windows = new[]
                {
                    new
                    {
                        hwnd = 42L,
                        title = "ChatGPT",
                        className = "Chrome_WidgetWin_1",
                        pid = 100,
                        visible = true,
                        minimized = false,
                        maximized = false,
                        rect = new
                        {
                            left = 10,
                            top = 20,
                            right = 1010,
                            bottom = 820,
                            width = 1000,
                            height = 800
                        }
                    }
                },
                count = 1
            }));
        }

        throw new InvalidOperationException(
            $"Unexpected action reached fake executor: {action}");
    }
}
