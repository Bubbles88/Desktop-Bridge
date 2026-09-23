using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using ErGe.Core.Ipc;

namespace ErGe.OwnerControl.Client;

public sealed class OwnerControlClient
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    public Task<OwnerControlResponse> GetStatusAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync("get.status", null, cancellationToken);

    public Task<OwnerControlResponse> SetPersistentModeAsync(
        string mode,
        CancellationToken cancellationToken = default) =>
        SendAsync("set.persistent-mode", mode, cancellationToken);

    public Task<OwnerControlResponse> SetSessionOverrideAsync(
        string sessionOverride,
        CancellationToken cancellationToken = default) =>
        SendAsync("set.session-override", sessionOverride, cancellationToken);

    public Task<OwnerControlResponse> ClearSessionOverrideAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync("clear.session-override", null, cancellationToken);

    public async Task<OwnerControlResponse> SendAsync(
        string command,
        string? value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(DefaultTimeout);

        await using var pipe = new NamedPipeClientStream(
            ".",
            OwnerControlProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);

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

        var hello = new OwnerControlHello(
            Type: "hello",
            ProtocolVersion: OwnerControlProtocol.Version,
            ProcessId: Environment.ProcessId,
            SessionId: process.SessionId,
            UserName: identity.Name ?? Environment.UserName,
            ClientName: "ErGe.OwnerControl.Client");

        await writer.WriteLineAsync(OwnerControlProtocol.Serialize(hello));

        var handshakeLine = await ReadLineAsync(
            reader,
            timeout.Token);

        var handshake =
            OwnerControlProtocol.Deserialize<OwnerControlHandshake>(handshakeLine);

        if (!handshake.Accepted)
        {
            throw new UnauthorizedAccessException(
                handshake.Error ?? "Core rejected Owner Control authentication.");
        }

        var requestId = Guid.NewGuid().ToString("N");

        await writer.WriteLineAsync(OwnerControlProtocol.Serialize(
            new OwnerControlRequest(
                Type: "command",
                RequestId: requestId,
                Command: command,
                Value: value)));

        var responseLine = await ReadLineAsync(
            reader,
            timeout.Token);

        var response =
            OwnerControlProtocol.Deserialize<OwnerControlResponse>(responseLine);

        if (!string.Equals(
                response.RequestId,
                requestId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Owner Control response correlation failed.");
        }

        return response;
    }

    private static async Task<string> ReadLineAsync(
        StreamReader reader,
        CancellationToken cancellationToken) =>
        await reader.ReadLineAsync(cancellationToken)
        ?? throw new EndOfStreamException(
            "Owner Control pipe closed unexpectedly.");
}
