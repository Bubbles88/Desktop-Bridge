using ErGe.OwnerControl.Client;
using ErGe.Core.Ipc;

if (args.Length != 1)
{
    PrintUsage();
    return 2;
}

var client = new OwnerControlClient();

OwnerControlResponse response = args[0] switch
{
    "--status" => await client.GetStatusAsync(),
    "--always-on" => await client.SetPersistentModeAsync("AlwaysOn"),
    "--always-off" => await client.SetPersistentModeAsync("AlwaysOff"),
    "--connect-now" => await client.SetSessionOverrideAsync("ConnectNow"),
    "--local-only" => await client.SetSessionOverrideAsync("LocalOnly"),
    "--emergency-block" => await client.SetSessionOverrideAsync("EmergencyBlock"),
    "--clear-override" => await client.ClearSessionOverrideAsync(),
    _ => throw new ArgumentException($"Unknown Owner Control command: {args[0]}")
};

Console.WriteLine(OwnerControlProtocol.Serialize(response));

if (!response.Success)
{
    Console.Error.WriteLine(
        $"{response.ErrorCode ?? "owner_control_failed"}: {response.Error}");
    return 1;
}

Console.WriteLine("ERGE_OWNER_CONTROL_OK");
return 0;

static void PrintUsage()
{
    Console.Error.WriteLine(
        "Usage: ErGe.OwnerControl.Cli --status|--always-on|--always-off|--connect-now|--local-only|--emergency-block|--clear-override");
}
