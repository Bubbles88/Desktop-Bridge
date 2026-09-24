using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ErGe.Core.Ipc;

var probeScreen = args.Contains("--probe-screen", StringComparer.OrdinalIgnoreCase);
var probeWindows = args.Contains("--probe-windows", StringComparer.OrdinalIgnoreCase);
var action = probeScreen
    ? "screen.info"
    : probeWindows
        ? "windows.list"
        : ReadOption(args, "--action");
var argumentsJson = ReadOption(args, "--arguments-json") ?? "{}";

if (string.IsNullOrWhiteSpace(action))
{
    Console.Error.WriteLine(
        "Usage: ErGe.LocalProvider.Cli --probe-screen | --probe-windows | --action <name> [--arguments-json <json>]");
    return 2;
}

JsonElement arguments;

try
{
    using var document = JsonDocument.Parse(argumentsJson);
    arguments = document.RootElement.Clone();
}
catch (JsonException ex)
{
    Console.Error.WriteLine($"arguments_json_invalid: {ex.Message}");
    return 2;
}

await using var pipe = new NamedPipeClientStream(
    ".",
    LocalProviderProtocol.PipeName,
    PipeDirection.InOut,
    PipeOptions.Asynchronous,
    TokenImpersonationLevel.Identification);

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
await pipe.ConnectAsync(5000, timeout.Token);

using var reader = new StreamReader(
    pipe,
    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
    detectEncodingFromByteOrderMarks: false,
    bufferSize: 4096,
    leaveOpen: true);

using var writer = new StreamWriter(
    pipe,
    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
    bufferSize: 4096,
    leaveOpen: true)
{
    AutoFlush = true
};

using var process = Process.GetCurrentProcess();
using var identity = WindowsIdentity.GetCurrent();

var hello = new LocalProviderHello(
    Type: "hello",
    ProtocolVersion: LocalProviderProtocol.Version,
    ProcessId: Environment.ProcessId,
    SessionId: process.SessionId,
    UserName: identity.Name ?? Environment.UserName,
    ClientName: "ErGe.LocalProvider.Cli");

await writer.WriteLineAsync(LocalProviderProtocol.Serialize(hello));

var handshakeLine = await ReadLineWithTimeoutAsync(
    reader,
    TimeSpan.FromSeconds(5),
    timeout.Token);

var handshake =
    LocalProviderProtocol.Deserialize<LocalProviderHandshake>(handshakeLine);

if (!handshake.Accepted)
{
    Console.Error.WriteLine(
        handshake.Error ?? "Core rejected local provider authentication.");
    return 1;
}

var requestId = Guid.NewGuid().ToString("N");
var request = new LocalProviderActionRequest(
    Type: "action",
    RequestId: requestId,
    Action: action,
    Arguments: arguments);

await writer.WriteLineAsync(LocalProviderProtocol.Serialize(request));

var responseLine = await ReadLineWithTimeoutAsync(
    reader,
    TimeSpan.FromSeconds(10),
    timeout.Token);

var response =
    LocalProviderProtocol.Deserialize<LocalProviderActionResponse>(responseLine);

if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
{
    Console.Error.WriteLine("Local provider response correlation failed.");
    return 1;
}

Console.WriteLine(LocalProviderProtocol.Serialize(response));

if (!response.Success)
{
    Console.Error.WriteLine(
        $"{response.ErrorCode ?? "action_failed"}: {response.Error}");
    return 1;
}

if (probeScreen)
{
    Console.WriteLine("ERGE_LOCAL_PROVIDER_SCREEN_INFO_OK");
}
else if (probeWindows)
{
    Console.WriteLine("ERGE_LOCAL_PROVIDER_WINDOWS_LIST_OK");
}

return 0;

static string? ReadOption(string[] arguments, string name)
{
    for (var index = 0; index < arguments.Length; index++)
    {
        if (!string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (index + 1 >= arguments.Length)
        {
            return null;
        }

        return arguments[index + 1];
    }

    return null;
}

static async Task<string> ReadLineWithTimeoutAsync(
    StreamReader reader,
    TimeSpan timeout,
    CancellationToken cancellationToken)
{
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken);
    linked.CancelAfter(timeout);

    try
    {
        return await reader.ReadLineAsync(linked.Token)
            ?? throw new EndOfStreamException(
                "Core local-provider pipe closed unexpectedly.");
    }
    catch (OperationCanceledException)
        when (!cancellationToken.IsCancellationRequested)
    {
        throw new TimeoutException(
            $"Core local-provider response exceeded {timeout.TotalSeconds:g} seconds.");
    }
}
