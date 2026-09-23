using ErGe.Core.Policy;

var root = System.IO.Path.Combine(
    System.IO.Path.GetTempPath(),
    "ErGe-Policy-SelfTest-" + Guid.NewGuid().ToString("N"));

Directory.CreateDirectory(root);
var path = System.IO.Path.Combine(root, "policy.json");

try
{
    var store = new FilePolicyStore(path);
    var engine = new PolicyEngine(store);

    Assert(engine.Snapshot.PersistentMode == PersistentMode.AlwaysOff, "Missing config fails closed.");
    Assert(engine.Snapshot.EffectiveAccess == EffectiveAccess.AlwaysOff, "Default effective state is off.");
    Assert(engine.Snapshot.ConfigurationHealthy, "Missing config is a healthy first-run state.");

    engine.SetPersistentMode(PersistentMode.AlwaysOn, ControlSource.LocalOwner);
    Assert(engine.Snapshot.RemoteAiAllowed, "Always On allows remote AI.");

    var reloaded = new PolicyEngine(new FilePolicyStore(path));
    Assert(reloaded.Snapshot.PersistentMode == PersistentMode.AlwaysOn, "Persistent mode survives process restart.");
    Assert(reloaded.Snapshot.SessionOverride == SessionOverride.None, "Session override never persists.");

    reloaded.SetSessionOverride(SessionOverride.LocalOnly, ControlSource.LocalOwner);
    Assert(!reloaded.Snapshot.RemoteAiAllowed && reloaded.Snapshot.LocalAiAllowed, "Local Only blocks remote AI.");

    reloaded.SetSessionOverride(SessionOverride.EmergencyBlock, ControlSource.LocalOwner);
    Assert(!reloaded.Snapshot.RemoteAiAllowed && !reloaded.Snapshot.LocalAiAllowed, "Emergency Block blocks all AI.");

    reloaded.ClearSessionOverride(ControlSource.LocalOwner);
    Assert(reloaded.Snapshot.RemoteAiAllowed, "Clearing override resumes persistent Always On.");

    reloaded.SetPersistentMode(PersistentMode.AlwaysOff, ControlSource.LocalOwner);
    reloaded.SetSessionOverride(SessionOverride.ConnectNow, ControlSource.LocalOwner);
    Assert(reloaded.Snapshot.RemoteAiAllowed, "Connect Now temporarily overrides Always Off.");

    var afterRestart = new PolicyEngine(new FilePolicyStore(path));
    Assert(afterRestart.Snapshot.PersistentMode == PersistentMode.AlwaysOff, "Always Off remains persistent.");
    Assert(afterRestart.Snapshot.EffectiveAccess == EffectiveAccess.AlwaysOff, "Connect Now ends on restart.");

    AssertThrows<UnauthorizedAccessException>(
        () => afterRestart.SetPersistentMode(PersistentMode.AlwaysOn, ControlSource.RemoteProvider),
        "Remote provider cannot change persistent mode.");

    AssertThrows<UnauthorizedAccessException>(
        () => afterRestart.SetSessionOverride(SessionOverride.ConnectNow, ControlSource.RemoteProvider),
        "Remote provider cannot change session override.");

    File.WriteAllText(path, "{ invalid json");
    var invalid = new PolicyEngine(new FilePolicyStore(path));
    Assert(invalid.Snapshot.PersistentMode == PersistentMode.AlwaysOff, "Invalid config fails closed.");
    Assert(!invalid.Snapshot.ConfigurationHealthy, "Invalid config is reported unhealthy.");
    Assert(!invalid.Snapshot.RemoteAiAllowed, "Invalid config never enables remote access.");

    Console.WriteLine("ERGE_POLICY_SELFTEST_OK");
}
finally
{
    try
    {
        Directory.Delete(root, recursive: true);
    }
    catch
    {
        // The test has already completed; cleanup is best effort.
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException("FAILED: " + message);
    }

    Console.WriteLine("PASS: " + message);
}

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        Console.WriteLine("PASS: " + message);
        return;
    }

    throw new InvalidOperationException("FAILED: " + message);
}
