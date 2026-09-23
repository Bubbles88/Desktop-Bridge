using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ErGe.Core.Ipc;

if (!args.Contains("--probe-screen", StringComparer.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Usage: ErGe.LocalProvider.Cli --probe-screen");
    return 2;
}

await using var pipe = new NamedPipeClientStream(
    ".",
    LocalProviderProtocol.PipeName,
    PipeDirection.InOut,
    PipeOptions.Asynchronous,
    TokenImpersonationLevel.Identification);

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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
    Action: "screen.info",
    Arguments: JsonSerializer.SerializeToElement(new { }));

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

Console.WriteLine("ERGE_LOCAL_PROVIDER_SCREEN_INFO_OK");
return 0;

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
